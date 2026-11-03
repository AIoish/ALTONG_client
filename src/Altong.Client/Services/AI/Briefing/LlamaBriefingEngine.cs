using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Altong.Client.Data.Models;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Altong.Client.Services.AI.Briefing;

public sealed record BriefingRawOutput(
    [property: JsonPropertyName("primary_category")] string PrimaryCategory,
    [property: JsonPropertyName("summary_lines")] IReadOnlyList<string> SummaryLines);

/// <summary>
/// Qwen 0.6B Q8_0 GGUF 모델을 구동하는 C# 세션 브리핑 추론 엔진.
/// BriefingPromptBuilder를 통해 AI 레포와 동일한 ChatML 규격으로 추론하고,
/// 응답은 AI 레포 category_prompt.parse_response와 같은 기준으로만 받아들인다.
/// </summary>
public sealed class LlamaBriefingEngine : IDisposable
{
    /// <summary>요약 중 메모리를 늘리지 않도록 문맥은 2048 토큰으로 둔다 (학습 max_length와 동일).</summary>
    public const uint ContextSize = 2048;
    public const int MaxNewTokens = 256;

    private readonly string _modelPath;
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private bool _disposed;

    public LlamaBriefingEngine(string modelPath)
    {
        _modelPath = modelPath;
    }

    public bool IsModelAvailable => File.Exists(_modelPath);

    public Task<BriefingRawOutput?> SummarizeNotificationGroupAsync(
        string appName,
        IReadOnlyList<NotificationRecord> notifications,
        int maxSummaryLines = BriefingPromptBuilder.MaxSummaryLines,
        CancellationToken ct = default)
    {
        var sender = notifications.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.Sender))?.Sender ?? "";
        return SummarizeNotificationGroupAsync(appName, sender, notifications, maxSummaryLines, ct);
    }

    /// <summary>그룹 하나만 요약한다. 여러 그룹이면 <see cref="OpenSessionAsync"/>로 시스템 지시문을 재사용한다.</summary>
    public async Task<BriefingRawOutput?> SummarizeNotificationGroupAsync(
        string appName,
        string sender,
        IReadOnlyList<NotificationRecord> notifications,
        int maxSummaryLines = BriefingPromptBuilder.MaxSummaryLines,
        CancellationToken ct = default)
    {
        if (notifications.Count == 0) return null;
        using var session = await OpenSessionAsync(ct).ConfigureAwait(false);
        return session is null
            ? null
            : await session.SummarizeAsync(appName, sender, notifications, maxSummaryLines, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 모델을 올리고 시스템 지시문을 한 번 처리한 요약 세션을 연다. 세션이 열려 있는 동안 다른 요약은 기다린다.
    /// 세션을 닫으면 문맥과 모델 가중치를 모두 내려 평상시 메모리를 차지하지 않는다 (다시 올리는 데 약 0.6초).
    /// 모델 파일이 없거나 불러오지 못하면 null.
    /// </summary>
    public async Task<BriefingSession?> OpenSessionAsync(CancellationToken ct = default)
    {
        if (!IsModelAvailable) return null;
        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        bool handedOver = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _parameters ??= new ModelParams(ResolveNativeModelPath(_modelPath))
            {
                ContextSize = ContextSize,
                GpuLayerCount = 0 // CPU 고속 구동 기본
            };
            _weights ??= LLamaWeights.LoadFromFile(_parameters);

            var session = await BriefingSession.CreateAsync(_weights, _parameters, CloseSession, ct).ConfigureAwait(false);
            handedOver = true;
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            AppLogger.Error($"[LlamaBriefingEngine] 요약 모델 준비 실패: {ex.Message}", ex);
            return null;
        }
        finally
        {
            if (!handedOver) CloseSession();
        }
    }

    /// <summary>요약이 끝나면 가중치를 내리고 다음 요약이 들어올 수 있게 한다.</summary>
    private void CloseSession()
    {
        try
        {
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <summary>
    /// AI 레포 category_prompt.parse_response와 같은 기준으로 응답을 검사한다.
    /// JSON 객체 하나, primary_category와 summary_lines 두 필드만, 허용된 분류, 1~maxSummaryLines줄, 줄당 160자 이하.
    /// 기준을 벗어나면 고치거나 일부만 주워 쓰지 않고 null을 돌려준다 (호출부가 대체 요약을 쓴다).
    /// </summary>
    public static BriefingRawOutput? ParseResponse(string? raw, int maxSummaryLines = BriefingPromptBuilder.MaxSummaryLines)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();

        // 끝난 사고 블록과 코드 펜스는 내용이 아닌 겉포장이라 벗긴다.
        const string closeThinkTag = "</think>";
        int closeThinkIdx = cleaned.LastIndexOf(closeThinkTag, StringComparison.OrdinalIgnoreCase);
        if (closeThinkIdx >= 0) cleaned = cleaned[(closeThinkIdx + closeThinkTag.Length)..].Trim();
        if (cleaned.Contains("<think>", StringComparison.OrdinalIgnoreCase)) return null;
        if (cleaned.StartsWith("```", StringComparison.Ordinal) && cleaned.EndsWith("```", StringComparison.Ordinal))
        {
            var fenceLines = cleaned.Split('\n');
            if (fenceLines.Length < 3) return null;
            cleaned = string.Join('\n', fenceLines[1..^1]).Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != 2 || !names.Contains("primary_category") || !names.Contains("summary_lines")) return null;

            var category = root.GetProperty("primary_category");
            if (category.ValueKind != JsonValueKind.String
                || !BriefingPromptBuilder.AllowedCategories.Contains(category.GetString())) return null;

            var linesElement = root.GetProperty("summary_lines");
            if (linesElement.ValueKind != JsonValueKind.Array) return null;
            int limit = Math.Min(maxSummaryLines, BriefingPromptBuilder.MaxSummaryLines);
            int count = linesElement.GetArrayLength();
            if (count < 1 || count > limit) return null;

            var lines = new List<string>(count);
            foreach (var line in linesElement.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.String) return null;
                var text = line.GetString()!.Trim();
                if (text.Length == 0 || text.Length > BriefingPromptBuilder.MaxLineLength) return null;
                lines.Add(text);
            }
            return new BriefingRawOutput(category.GetString()!, lines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveNativeModelPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return modelPath;

        // LLamaSharp P/Invoke(Windows)의 ANSI/CP949 마샬링으로 인해 경로에 비-ASCII 문자가 있으면
        // llama.cpp의 UTF-8 파서(ggml_fopen)에서 열지 못할 수 있습니다.
        // 현재 작업 디렉터리 기준 상대 경로가 순수 ASCII이면 이를 우선 전달합니다.
        if (modelPath.Any(c => c > 127))
        {
            try
            {
                var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), modelPath);
                if (!rel.Any(c => c > 127) && File.Exists(rel))
                {
                    return rel;
                }
            }
            catch
            {
                // Fallback to original path
            }
        }

        return modelPath;
    }

    public void Dispose()
    {
        _inferenceGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }
}

/// <summary>
/// 시스템 지시문(약 1,150토큰)을 한 번만 처리해 두고, 그룹마다 그 상태를 복제(Fork)해 사용자 부분만 처리한다.
/// 그룹당 시간이 약 11초에서 약 4초로 줄어든다. 세션을 닫으면 문맥과 모델 가중치를 모두 돌려준다.
/// </summary>
public sealed class BriefingSession : IDisposable
{
    private readonly BatchedExecutor _executor;
    private readonly Conversation _root;
    private readonly int _prefixTokenCount;
    private readonly HashSet<LLamaToken> _stopTokens;
    private readonly Action _onClosed;
    private bool _disposed;

    private BriefingSession(BatchedExecutor executor, Conversation root, int prefixTokenCount,
        HashSet<LLamaToken> stopTokens, Action onClosed)
    {
        _executor = executor;
        _root = root;
        _prefixTokenCount = prefixTokenCount;
        _stopTokens = stopTokens;
        _onClosed = onClosed;
    }

    internal static async Task<BriefingSession> CreateAsync(
        LLamaWeights weights, ModelParams parameters, Action onClosed, CancellationToken ct)
    {
        var executor = new BatchedExecutor(weights, parameters);
        Conversation? root = null;
        try
        {
            var prefix = executor.Context.Tokenize(
                BriefingPromptBuilder.RenderChatMlPrefix(BriefingPromptBuilder.SystemPrompt), false, true);
            root = executor.Create();
            root.Prompt(prefix);
            await DrainAsync(executor, ct).ConfigureAwait(false);

            var stopTokens = new HashSet<LLamaToken>();
            foreach (var marker in new[] { "<|im_end|>", "<|endoftext|>", "<|im_start|>" })
                stopTokens.UnionWith(executor.Context.Tokenize(marker, false, true));

            return new BriefingSession(executor, root, prefix.Length, stopTokens, onClosed);
        }
        catch
        {
            root?.Dispose();
            executor.Dispose();
            throw;
        }
    }

    /// <summary>이 그룹의 프롬프트와 최대 생성 토큰이 문맥(2048)에 들어가는지.</summary>
    public bool Fits(string appName, string sender, IReadOnlyList<NotificationRecord> notifications,
        int maxSummaryLines = BriefingPromptBuilder.MaxSummaryLines) =>
        _prefixTokenCount + TokenizeSuffix(appName, sender, notifications, maxSummaryLines).Length
            + LlamaBriefingEngine.MaxNewTokens <= LlamaBriefingEngine.ContextSize;

    public async Task<BriefingRawOutput?> SummarizeAsync(
        string appName,
        string sender,
        IReadOnlyList<NotificationRecord> notifications,
        int maxSummaryLines = BriefingPromptBuilder.MaxSummaryLines,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (notifications.Count == 0) return null;
        var suffix = TokenizeSuffix(appName, sender, notifications, maxSummaryLines);
        if (_prefixTokenCount + suffix.Length + LlamaBriefingEngine.MaxNewTokens > LlamaBriefingEngine.ContextSize)
        {
            AppLogger.Warn($"[LlamaBriefingEngine] 알림 {notifications.Count}개가 요약 문맥보다 길어 대체 요약을 사용합니다.");
            return null;
        }

        try
        {
            using var conversation = _root.Fork();
            conversation.Prompt(suffix);
            await DrainAsync(_executor, ct).ConfigureAwait(false);

            // AI 레포 GGUF 검증과 같은 greedy 디코딩 (temperature 0, 반복 패널티 없음).
            using var sampler = new DefaultSamplingPipeline { Temperature = 0.0f, RepeatPenalty = 1.0f };
            var decoder = new StreamingTokenDecoder(_executor.Context);
            for (int i = 0; i < LlamaBriefingEngine.MaxNewTokens; i++)
            {
                var token = sampler.Sample(_executor.Context.NativeHandle, conversation.GetSampleIndex());
                if (_stopTokens.Contains(token) || token.IsEndOfGeneration(_executor.Model.Vocab)) break;
                sampler.Accept(token);
                decoder.Add(token);
                conversation.Prompt(token);
                await DrainAsync(_executor, ct).ConfigureAwait(false);
            }

            var output = LlamaBriefingEngine.ParseResponse(decoder.Read(), maxSummaryLines);
            if (output is null)
                AppLogger.Warn($"[LlamaBriefingEngine] 요약 응답이 형식 기준을 벗어나 대체 요약을 사용합니다. (알림 {notifications.Count}개)");
            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[LlamaBriefingEngine] LLM 추론 실패: {ex.Message}", ex);
            return null;
        }
    }

    private LLamaToken[] TokenizeSuffix(string appName, string sender,
        IReadOnlyList<NotificationRecord> notifications, int maxSummaryLines)
    {
        var promptItems = notifications
            .OrderBy(n => n.ReceivedAt)
            .Select(n => new NotificationPromptItem(
                Timestamp: n.ReceivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                Title: n.Title ?? string.Empty,
                Body: n.Body ?? string.Empty))
            .ToList();
        var userPrompt = BriefingPromptBuilder.BuildUserPrompt(appName, sender, promptItems, maxSummaryLines);
        return _executor.Context.Tokenize(BriefingPromptBuilder.RenderChatMlSuffix(userPrompt), false, true);
    }

    private static async Task DrainAsync(BatchedExecutor executor, CancellationToken ct)
    {
        // 한 번의 Infer는 배치 크기만큼만 처리하므로 대기 토큰이 없어질 때까지 반복한다.
        while (executor.BatchedTokenCount > 0)
        {
            ct.ThrowIfCancellationRequested();
            await executor.Infer(ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _root.Dispose();
            _executor.Dispose();
        }
        finally
        {
            // 문맥을 먼저 해제한 뒤 가중치를 내린다 (엔진의 CloseSession).
            _onClosed();
        }
    }
}

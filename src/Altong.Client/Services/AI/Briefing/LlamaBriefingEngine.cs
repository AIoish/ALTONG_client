using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Altong.Client.Data.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Altong.Client.Services.AI.Briefing;

public sealed record BriefingRawOutput(
    [property: JsonPropertyName("primary_category")] string PrimaryCategory,
    [property: JsonPropertyName("summary_lines")] IReadOnlyList<string> SummaryLines);

/// <summary>
/// Qwen 0.6B Q8_0 GGUF 모델을 구동하는 순수 C# 네이티브 세션 브리핑 추론 엔진.
/// BriefingPromptBuilder를 통해 AI 파이프라인과 100% 동일한 ChatML 규격으로 LLM 추론을 수행합니다.
/// </summary>
public sealed class LlamaBriefingEngine : IDisposable
{
    private readonly string _modelPath;
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private readonly object _lock = new();

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

    public async Task<BriefingRawOutput?> SummarizeNotificationGroupAsync(
        string appName,
        string sender,
        IReadOnlyList<NotificationRecord> notifications,
        int maxSummaryLines = BriefingPromptBuilder.MaxSummaryLines,
        CancellationToken ct = default)
    {
        if (!IsModelAvailable || notifications.Count == 0) return null;

        var promptItems = notifications
            .OrderBy(n => n.ReceivedAt)
            .Select(n => new NotificationPromptItem(
                Timestamp: n.ReceivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                Title: n.Title ?? string.Empty,
                Body: n.Body ?? string.Empty
            )).ToList();

        var systemPrompt = BriefingPromptBuilder.BuildSystemPrompt();
        var userPrompt = BriefingPromptBuilder.BuildUserPrompt(appName, sender, promptItems, maxSummaryLines);
        var chatMl = BriefingPromptBuilder.RenderChatMl(systemPrompt, userPrompt);

        string rawResponse;
        try
        {
            rawResponse = await GenerateResponseAsync(chatMl, ct).ConfigureAwait(false);
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

        return ParseResponse(rawResponse, notifications);
    }

    private async Task<string> GenerateResponseAsync(string chatMlPrompt, CancellationToken ct)
    {
        LLamaWeights weights;
        ModelParams parameters;
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();

            if (_weights is null)
            {
                var effectivePath = ResolveNativeModelPath(_modelPath);
                _parameters = new ModelParams(effectivePath)
                {
                    ContextSize = 2048,
                    GpuLayerCount = 0 // CPU 고속 구동 기본
                };
                _weights = LLamaWeights.LoadFromFile(_parameters);
            }

            weights = _weights;
            parameters = _parameters!;
        }

        using var context = weights.CreateContext(parameters);
        var executor = new InteractiveExecutor(context);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 256,
            AntiPrompts = ["<|im_end|>", "<|endoftext|>", "<|im_start|>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.0f // AI 레포와 동일한 Greedy 디코딩
            }
        };

        var sb = new StringBuilder();
        await foreach (var token in executor.InferAsync(chatMlPrompt, inferenceParams, ct).ConfigureAwait(false))
        {
            sb.Append(token);
        }

        return sb.ToString();
    }

    public static BriefingRawOutput? ParseResponse(string raw, IReadOnlyList<NotificationRecord> fallbackNotifications)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 마크다운 코드 블록 제거 (```json ... ```)
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];
            if (cleaned.EndsWith("```", StringComparison.Ordinal))
                cleaned = cleaned[..^3];
            cleaned = cleaned.Trim();
        }

        // JSON 블록 파싱 시도
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var jsonSnippet = cleaned[start..(end + 1)];
            try
            {
                using var doc = JsonDocument.Parse(jsonSnippet);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    string? category = null;
                    if (root.TryGetProperty("primary_category", out var catProp) && catProp.ValueKind == JsonValueKind.String)
                    {
                        category = catProp.GetString()?.Trim();
                    }

                    var lines = new List<string>();
                    if (root.TryGetProperty("summary_lines", out var linesProp) && linesProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var lineElem in linesProp.EnumerateArray())
                        {
                            if (lineElem.ValueKind == JsonValueKind.String)
                            {
                                var text = lineElem.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    if (text.Length > BriefingPromptBuilder.MaxLineLength)
                                        text = text[..BriefingPromptBuilder.MaxLineLength];
                                    lines.Add(text);
                                }
                            }
                        }
                    }

                    var validCategory = category is not null && BriefingPromptBuilder.AllowedCategories.Contains(category)
                        ? category
                        : "기타";

                    if (lines.Count > 0)
                    {
                        if (lines.Count > BriefingPromptBuilder.MaxSummaryLines)
                            lines = lines.Take(BriefingPromptBuilder.MaxSummaryLines).ToList();

                        return new BriefingRawOutput(validCategory, lines);
                    }
                }
            }
            catch { /* 파싱 실패 시 fallback 시도 */ }
        }

        // Fallback: 정규식으로 복구 시도
        var catMatch = Regex.Match(cleaned, @"""primary_category""\s*:\s*""(?<cat>[^""]+)""");
        var primaryCategory = catMatch.Success ? catMatch.Groups["cat"].Value.Trim() : "기타";
        if (!BriefingPromptBuilder.AllowedCategories.Contains(primaryCategory))
            primaryCategory = "기타";

        var fallbackLines = new List<string>();
        var lineMatches = Regex.Matches(cleaned, @"""(?<line>[^""]{4,})""");
        foreach (Match m in lineMatches)
        {
            var text = m.Groups["line"].Value.Trim();
            if (text != "primary_category" && text != "summary_lines" && text != primaryCategory && !BriefingPromptBuilder.AllowedCategories.Contains(text))
            {
                if (text.Length > BriefingPromptBuilder.MaxLineLength)
                    text = text[..BriefingPromptBuilder.MaxLineLength];
                fallbackLines.Add(text);
            }
        }

        if (fallbackLines.Count == 0 && fallbackNotifications.Count > 0)
        {
            var last = fallbackNotifications[^1];
            var line = string.IsNullOrWhiteSpace(last.Sender) ? last.Body : $"{last.Sender}: {last.Body}";
            fallbackLines.Add(line);
        }

        if (fallbackLines.Count > BriefingPromptBuilder.MaxSummaryLines)
            fallbackLines = fallbackLines.Take(BriefingPromptBuilder.MaxSummaryLines).ToList();

        return fallbackLines.Count > 0 ? new BriefingRawOutput(primaryCategory, fallbackLines) : null;
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
        lock (_lock)
        {
            _weights?.Dispose();
            _weights = null;
        }
    }
}

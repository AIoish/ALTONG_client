using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Altong.Client.Models;
using Altong.Client.Services.AI.Briefing;

namespace Altong.Client.Services.Insights;

/// <summary>
/// Gemma 4 E2B 온디바이스 모델을 사용한 집중 인사이트 코칭 생성기 및 로컬 JSON 캐시 서비스.
/// 평상시에는 집계 지표 기반의 규칙 코칭이나 유효한 캐시 결과를 즉시 반환하고,
/// 사용자가 요청할 때만 집계 데이터로 AI 코칭을 생성한다.
/// </summary>
public sealed class FocusInsightCoachingService : IFocusInsightCoachingService
{
    public const int DefaultMaxTokens = 320;
    private const int CacheVersion = 1;

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    internal sealed record ParsedCoachingPayload(
        string Headline,
        IReadOnlyList<string> Findings,
        string ActionTip);

    private sealed record CacheDocument(
        int Version,
        Dictionary<string, CachedCoachingEntry> Entries);

    private sealed record CachedCoachingEntry(
        string Headline,
        string[] Findings,
        string ActionTip,
        DateTime GeneratedAtUtc,
        string DataFingerprint);

    private readonly Func<string, string, CancellationToken, Task<string?>>? _textGenerator;
    private readonly Func<bool> _isAiAvailable;
    private readonly string? _cacheFilePath;
    private readonly object _cacheSync = new();
    private Dictionary<string, CachedCoachingEntry> _cacheEntries = new(StringComparer.Ordinal);

    public static string DefaultCacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Altong",
        "insight-coaching-cache.json");

    public FocusInsightCoachingService(LlamaBriefingEngine? engine, string? cacheFilePath = null)
        : this(
            textGenerator: engine is null
                ? null
                : (systemPrompt, userPrompt, ct) => engine.GenerateTextAsync(systemPrompt, userPrompt, DefaultMaxTokens, ct),
            isAiAvailable: () => engine?.IsModelAvailable == true,
            cacheFilePath: string.IsNullOrWhiteSpace(cacheFilePath) ? DefaultCacheFilePath : cacheFilePath)
    {
    }

    internal FocusInsightCoachingService(
        Func<string, string, CancellationToken, Task<string?>>? textGenerator,
        bool isAiAvailable,
        string? cacheFilePath = null)
        : this(
            textGenerator: textGenerator,
            isAiAvailable: () => isAiAvailable,
            cacheFilePath: string.IsNullOrWhiteSpace(cacheFilePath) ? null : cacheFilePath)
    {
    }

    private FocusInsightCoachingService(
        Func<string, string, CancellationToken, Task<string?>>? textGenerator,
        Func<bool> isAiAvailable,
        string? cacheFilePath)
    {
        _textGenerator = textGenerator;
        _isAiAvailable = isAiAvailable;
        _cacheFilePath = cacheFilePath;
        LoadCache();
    }

    public bool IsAiAvailable => _isAiAvailable();

    public InsightCoachingResult GetInitialOrCached(FocusInsightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.HasData)
        {
            return BuildEmptyStateResult(snapshot);
        }

        if (TryGetMatchingCache(snapshot, out var cached))
        {
            return cached;
        }

        return BuildRuleBasedResult(snapshot);
    }

    public async Task<InsightCoachingResult> GenerateAsync(
        FocusInsightSnapshot snapshot,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        if (!snapshot.HasData)
        {
            return BuildEmptyStateResult(snapshot);
        }

        if (!forceRefresh && TryGetMatchingCache(snapshot, out var cached))
        {
            return cached;
        }

        if (!IsAiAvailable || _textGenerator is null)
        {
            return BuildRuleBasedResult(snapshot);
        }

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(snapshot);

        string? raw;
        try
        {
            raw = await _textGenerator(systemPrompt, userPrompt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[FocusInsightCoachingService] AI 코칭 생성 중 오류가 발생해 규칙 기반 코칭으로 대체합니다: {ex.Message}");
            return BuildRuleBasedResult(snapshot);
        }

        var parsed = ParseCoachingJson(raw);
        if (parsed is null)
        {
            AppLogger.Warn("[FocusInsightCoachingService] AI 코칭 응답이 JSON 형식 기준을 벗어나 규칙 기반 코칭으로 대체합니다.");
            return BuildRuleBasedResult(snapshot);
        }

        var result = new InsightCoachingResult(
            Headline: parsed.Headline,
            Findings: parsed.Findings,
            ActionTip: parsed.ActionTip,
            IsAiGenerated: true,
            IsFromCache: false,
            GeneratedAtUtc: DateTime.UtcNow,
            DataFingerprint: snapshot.DataFingerprint);

        SaveCacheEntry(snapshot.Period, result);
        return result;
    }

    internal static string BuildSystemPrompt() =>
        "당신은 PC 집중 도우미 ALTONG의 '집중 인사이트' 코치입니다.\n" +
        "제공된 집계 지표만 근거로 사용자의 집중 패턴을 분석해 간결한 한국어 코칭을 작성하세요.\n\n" +
        "규칙:\n" +
        "1. 입력에 없는 숫자, 시각, 앱 이름, 발신자, 원인을 절대 추측하거나 만들어 내지 마세요.\n" +
        "2. 자연스럽고 따뜻한 한국어 해요체로 간결하게 작성하세요.\n" +
        "3. 마크다운, 코드 블록, 설명, 사고 과정 없이 아래 JSON 객체 하나만 출력하세요.\n" +
        "4. 반드시 headline(문자열), findings(문자열 2개 배열), action(문자열) 세 필드만 포함하세요.\n" +
        "형식: {\"headline\":\"핵심 한 줄 요약\",\"findings\":[\"발견 1\",\"발견 2\"],\"action\":\"구체적 실천 팁\"}";

    internal static string BuildUserPrompt(FocusInsightSnapshot snapshot)
    {
        var sb = new StringBuilder(768);
        var periodLabel = GetPeriodLabel(snapshot.Period);
        var totalMinutes = (int)Math.Floor(snapshot.TotalFocusDuration.TotalMinutes);

        sb.AppendLine($"기간: {periodLabel}");
        sb.AppendLine($"총 집중 시간: {totalMinutes}분 ({FormatDuration(snapshot.TotalFocusDuration)})");
        sb.AppendLine($"집중 세션 수: {snapshot.FocusSessionCount}회");
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "알림 집계: 전체 {0}건, 차단 {1}건, 통과 {2}건 (긴급 통과 {3}건, 연관 통과 {4}건), 차단율 {5:F1}%",
            snapshot.TotalNotifications,
            snapshot.BlockedCount,
            snapshot.PassedCount,
            snapshot.UrgentPassedCount,
            snapshot.RelevancePassedCount,
            snapshot.BlockRatePercent));

        sb.AppendLine(snapshot.GoldenHour is int goldenHour
            ? $"골든타임: {goldenHour:00}시 ({FormatDuration(snapshot.GoldenHourDuration)})"
            : "골든타임: 없음");

        sb.AppendLine(snapshot.DistractionPeakHour is int peakHour
            ? $"방해 피크 시간대: {peakHour:00}시 ({snapshot.DistractionPeakCount}건)"
            : "방해 피크 시간대: 없음");

        var topDistractors = snapshot.TopDistractors.Take(3).ToList();
        if (topDistractors.Count > 0)
        {
            sb.AppendLine("주요 방해원 TOP 3:");
            foreach (var item in topDistractors)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "- {0}: 차단 {1}건 / 전체 {2}건 (차단율 {3:F0}%)",
                    item.DisplayLabel,
                    item.BlockedCount,
                    item.TotalCount,
                    item.BlockRatePercent));
            }
        }
        else
        {
            sb.AppendLine("주요 방해원 TOP 3: 없음");
        }

        var topContexts = snapshot.TopWorkContexts.Take(3).ToList();
        if (topContexts.Count > 0)
        {
            sb.AppendLine("작업 맥락 TOP 3:");
            foreach (var item in topContexts)
            {
                var ctxMinutes = (int)Math.Floor(item.FocusDuration.TotalMinutes);
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "- {0}: 집중 {1}분, 차단 {2}건, 통과 {3}건",
                    item.DisplayName,
                    ctxMinutes,
                    item.BlockedCount,
                    item.PassedCount));
            }
        }
        else
        {
            sb.AppendLine("작업 맥락 TOP 3: 없음");
        }

        if (snapshot.Quality is { } q)
        {
            if (q.FlowProtectionEligibleCount > 0)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "몰입 유지율: {0:F1}% (차단 {1}건 중 {2}건 작업 흐름 유지)",
                    q.FlowProtectionRatePercent,
                    q.FlowProtectionEligibleCount,
                    q.FlowProtectedCount));
            }

            if (q.WorkDuration + q.NeutralDuration + q.LeisureDuration > TimeSpan.Zero)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "집중 순도: 순수 작업 {0:F0}% · 메신저·웹 {1:F0}% · 비작업 {2:F0}%",
                    q.WorkPurityPercent,
                    q.NeutralPercent,
                    q.LeisurePercent));
            }

            if (!string.IsNullOrWhiteSpace(q.TopSelfDistractionApp))
            {
                sb.AppendLine($"집중 중 직접 확인 최다 앱: {q.TopSelfDistractionApp.Trim()} ({FormatDuration(q.TopSelfDistractionDuration)})");
            }

            sb.AppendLine($"깊은 몰입(25분+): {q.DeepFocusSessionCount}회, 평균 세션: {FormatDuration(q.AverageSessionDuration)}, 연속 집중: {q.CurrentStreakDays}일");

            if (q.PeriodChangePercent is double periodChange && !double.IsNaN(periodChange) && !double.IsInfinity(periodChange))
            {
                var roundedChange = (int)Math.Round(periodChange, MidpointRounding.AwayFromZero);
                var sign = roundedChange > 0 ? "+" : string.Empty;
                sb.AppendLine($"전기 대비 집중 변화: {sign}{roundedChange}%");
            }
        }

        sb.Append("위 집계 수치만 사용해 JSON 객체 하나만 출력하세요.");
        return sb.ToString();
    }

    internal static ParsedCoachingPayload? ParseCoachingJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim();

        const string closeThinkTag = "</think>";
        int closeThinkIdx = cleaned.LastIndexOf(closeThinkTag, StringComparison.OrdinalIgnoreCase);
        if (closeThinkIdx >= 0)
        {
            cleaned = cleaned[(closeThinkIdx + closeThinkTag.Length)..].Trim();
        }

        if (cleaned.Contains("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        cleaned = StripMarkdownFence(cleaned);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("headline", out var headlineEl) || headlineEl.ValueKind != JsonValueKind.String)
                return null;
            var headline = headlineEl.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(headline)) return null;

            if (!root.TryGetProperty("findings", out var findingsEl) || findingsEl.ValueKind != JsonValueKind.Array)
                return null;

            int rawCount = findingsEl.GetArrayLength();
            if (rawCount < 1 || rawCount > 3) return null;

            var findings = new List<string>(Math.Min(rawCount, 2));
            foreach (var item in findingsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                var text = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (findings.Count < 2)
                {
                    findings.Add(text);
                }
            }

            if (findings.Count == 0) return null;

            if (!root.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
                return null;
            var action = actionEl.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(action)) return null;

            return new ParsedCoachingPayload(headline, findings, action);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3) return string.Empty;
        return string.Join('\n', lines[1..^1]).Trim();
    }

    private static InsightCoachingResult BuildEmptyStateResult(FocusInsightSnapshot snapshot)
    {
        var periodLabel = GetPeriodLabel(snapshot.Period);
        return new InsightCoachingResult(
            Headline: $"{periodLabel} 집중 기록이 아직 없어요.",
            Findings:
            [
                "집중 세션을 시작하면 시간대별 몰입 패턴과 알림 차단 효과가 자동으로 집계돼요.",
                "기록이 쌓이면 주요 방해원과 작업 맥락을 바탕으로 맞춤 코칭을 받을 수 있어요."
            ],
            ActionTip: "짧은 집중 세션을 한 번 시작해 오늘의 첫 집중 데이터를 만들어 보세요.",
            IsAiGenerated: false,
            IsFromCache: false,
            GeneratedAtUtc: snapshot.GeneratedAtUtc,
            DataFingerprint: snapshot.DataFingerprint);
    }

    private static InsightCoachingResult BuildRuleBasedResult(FocusInsightSnapshot snapshot)
    {
        var periodLabel = GetPeriodLabel(snapshot.Period);
        var durationText = FormatDuration(snapshot.TotalFocusDuration);
        var blockRateText = snapshot.BlockRatePercent.ToString("F0", CultureInfo.InvariantCulture);

        string headline;
        if (snapshot.TotalNotifications > 0 && snapshot.FocusSessionCount > 0)
        {
            headline = $"{periodLabel} {durationText} 동안 방해 알림 {snapshot.BlockedCount}건을 차단했어요 ({blockRateText}% 보호).";
        }
        else if (snapshot.FocusSessionCount > 0)
        {
            headline = $"{periodLabel} {snapshot.FocusSessionCount}회의 집중 세션으로 총 {durationText} 몰입했어요.";
        }
        else
        {
            headline = $"{periodLabel} 전체 알림 {snapshot.TotalNotifications}건 중 {snapshot.BlockedCount}건을 차단했어요 ({blockRateText}% 보호).";
        }

        var quality = snapshot.Quality;
        bool hasFlowProtection = quality is { FlowProtectionEligibleCount: > 0 };
        bool hasWorkPurity = quality is not null
            && quality.WorkDuration + quality.NeutralDuration + quality.LeisureDuration > TimeSpan.Zero;

        string finding1;
        if (hasFlowProtection && hasWorkPurity)
        {
            var flowRateText = quality!.FlowProtectionRatePercent.ToString("F0", CultureInfo.InvariantCulture);
            var purityText = quality.WorkPurityPercent.ToString("F0", CultureInfo.InvariantCulture);
            finding1 = $"차단 알림 {quality.FlowProtectionEligibleCount}건 중 {flowRateText}%에서 작업 흐름을 지켜냈고, 순수 작업 비율 {purityText}%를 유지했어요.";
        }
        else if (hasFlowProtection)
        {
            var flowRateText = quality!.FlowProtectionRatePercent.ToString("F0", CultureInfo.InvariantCulture);
            finding1 = $"차단 알림 {quality.FlowProtectionEligibleCount}건 중 {flowRateText}%에서 작업 흐름을 지켜냈어요.";
        }
        else if (hasWorkPurity)
        {
            var purityText = quality!.WorkPurityPercent.ToString("F0", CultureInfo.InvariantCulture);
            finding1 = quality.DeepFocusSessionCount > 0
                ? $"순수 작업 비율 {purityText}%로 집중했고, 25분 이상 깊은 몰입을 {quality.DeepFocusSessionCount}회 달성했어요."
                : $"집중 시간 기준 순수 작업 비율 {purityText}%를 기록했어요.";
        }
        else if (snapshot.GoldenHour is int gh && snapshot.DistractionPeakHour is int ph)
        {
            finding1 = $"가장 몰입이 잘 된 골든타임은 {gh:00}시({FormatDuration(snapshot.GoldenHourDuration)})이며, 방해 알림이 가장 몰린 시간대는 {ph:00}시({snapshot.DistractionPeakCount}건)예요.";
        }
        else if (snapshot.GoldenHour is int onlyGh)
        {
            finding1 = $"가장 몰입이 잘 된 골든타임은 {onlyGh:00}시로, 이 시간대에 {FormatDuration(snapshot.GoldenHourDuration)} 집중했어요.";
        }
        else if (snapshot.DistractionPeakHour is int onlyPh)
        {
            finding1 = $"알림 유입이 가장 많았던 시간대는 {onlyPh:00}시로 총 {snapshot.DistractionPeakCount}건이 기록되었어요.";
        }
        else
        {
            finding1 = $"{periodLabel} 기준 집중 세션 {snapshot.FocusSessionCount}회, 누적 집중 시간 {durationText}이 기록되었어요.";
        }

        string finding2;
        if (snapshot.TopDistractors.Count > 0 && snapshot.TopWorkContexts.Count > 0)
        {
            var topD = snapshot.TopDistractors[0];
            var topW = snapshot.TopWorkContexts[0];
            finding2 = $"주요 방해원은 {topD.DisplayLabel}(차단 {topD.BlockedCount}건/{topD.TotalCount}건)이며, {topW.DisplayName} 작업 중 알림 {topW.BlockedCount}건을 막아냈어요.";
        }
        else if (snapshot.TopDistractors.Count > 0)
        {
            var topD = snapshot.TopDistractors[0];
            var topDRate = topD.BlockRatePercent.ToString("F0", CultureInfo.InvariantCulture);
            finding2 = $"가장 많은 방해 알림은 {topD.DisplayLabel}에서 발생했고 전체 {topD.TotalCount}건 중 {topD.BlockedCount}건({topDRate}%)을 차단했어요.";
        }
        else if (snapshot.TopWorkContexts.Count > 0)
        {
            var topW = snapshot.TopWorkContexts[0];
            finding2 = $"가장 오래 집중한 작업은 {topW.DisplayName}({FormatDuration(topW.FocusDuration)})이며, 차단 {topW.BlockedCount}건 · 통과 {topW.PassedCount}건이 기록되었어요.";
        }
        else if ((hasFlowProtection || hasWorkPurity) && snapshot.GoldenHour is int qualityGh)
        {
            finding2 = $"가장 몰입이 잘 된 골든타임은 {qualityGh:00}시로, 이 시간대에 {FormatDuration(snapshot.GoldenHourDuration)} 집중했어요.";
        }
        else if (quality is not null && (quality.DeepFocusSessionCount > 0 || quality.CurrentStreakDays >= 2))
        {
            if (quality.DeepFocusSessionCount > 0 && quality.CurrentStreakDays >= 2)
            {
                finding2 = $"25분 이상 깊은 몰입을 {quality.DeepFocusSessionCount}회 달성하며 {quality.CurrentStreakDays}일 연속 집중 흐름을 이어가고 있어요.";
            }
            else if (quality.DeepFocusSessionCount > 0)
            {
                finding2 = $"25분 이상 깊은 몰입 세션을 {quality.DeepFocusSessionCount}회 완료했고 평균 세션은 {FormatDuration(quality.AverageSessionDuration)}이에요.";
            }
            else
            {
                finding2 = $"{quality.CurrentStreakDays}일 연속 집중 기록을 이어가며 꾸준한 루틴을 만들고 있어요.";
            }
        }
        else if (snapshot.PassedCount > 0)
        {
            finding2 = $"통과된 알림 {snapshot.PassedCount}건 중 긴급 {snapshot.UrgentPassedCount}건, 업무 연관 {snapshot.RelevancePassedCount}건이 선별되어 전달되었어요.";
        }
        else
        {
            finding2 = "집중 시간 동안 불필요한 알림 방해 없이 안정적인 몰입 흐름을 유지했어요.";
        }

        string actionTip;
        if (quality is not null
            && !string.IsNullOrWhiteSpace(quality.TopSelfDistractionApp)
            && quality.TopSelfDistractionDuration >= TimeSpan.FromMinutes(15)
            && snapshot.BlockedCount > 0)
        {
            actionTip = $"알림은 잘 차단했지만 집중 중 {quality.TopSelfDistractionApp.Trim()} 창을 직접 연 시간({FormatDuration(quality.TopSelfDistractionDuration)})이 길어요. 집중 중에는 해당 창을 최소화해 두면 몰입 순도가 확 올라가요.";
        }
        else if (snapshot.GoldenHour is int golden && snapshot.DistractionPeakHour is int peak && golden == peak)
        {
            actionTip = $"골든타임과 방해 피크가 {golden:00}시에 겹쳐요. 이 시간대에 집중 모드를 미리 켜 두면 몰입 손실을 크게 줄일 수 있어요.";
        }
        else if (snapshot.TopDistractors.Count > 0 && snapshot.TopDistractors[0].BlockedCount >= 3)
        {
            actionTip = $"{snapshot.TopDistractors[0].DisplayLabel}에서 차단 알림이 반복되고 있어요. 집중 전 해당 채널 알림을 잠시 무음으로 두면 더 쾌적해요.";
        }
        else if (snapshot.GoldenHour is int goldenOnly)
        {
            actionTip = $"몰입 효율이 가장 높은 {goldenOnly:00}시 전후에 핵심 업무를 배치하고 집중 세션을 이어가 보세요.";
        }
        else if (snapshot.DistractionPeakHour is int peakOnly)
        {
            actionTip = $"알림이 몰리는 {peakOnly:00}시 구간에 집중 세션을 켜 두면 중요한 알림만 골라 받으며 흐름을 지킬 수 있어요.";
        }
        else if (quality is { CurrentStreakDays: >= 2 })
        {
            actionTip = $"{quality.CurrentStreakDays}일 연속 집중 흐름이 아주 좋아요. 내일도 짧은 집중 세션으로 기록을 이어가 보세요.";
        }
        else
        {
            actionTip = "지금의 안정적인 집중 패턴을 유지하며 다음 작업도 짧은 집중 세션으로 시작해 보세요.";
        }

        return new InsightCoachingResult(
            Headline: headline,
            Findings: [finding1, finding2],
            ActionTip: actionTip,
            IsAiGenerated: false,
            IsFromCache: false,
            GeneratedAtUtc: snapshot.GeneratedAtUtc,
            DataFingerprint: snapshot.DataFingerprint);
    }

    private bool TryGetMatchingCache(FocusInsightSnapshot snapshot, out InsightCoachingResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(snapshot.DataFingerprint)) return false;

        lock (_cacheSync)
        {
            if (!_cacheEntries.TryGetValue(snapshot.Period.ToString(), out var entry))
            {
                return false;
            }

            if (!string.Equals(entry.DataFingerprint, snapshot.DataFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsValidCachedEntry(entry))
            {
                return false;
            }

            result = new InsightCoachingResult(
                Headline: entry.Headline,
                Findings: entry.Findings,
                ActionTip: entry.ActionTip,
                IsAiGenerated: true,
                IsFromCache: true,
                GeneratedAtUtc: entry.GeneratedAtUtc,
                DataFingerprint: entry.DataFingerprint);
            return true;
        }
    }

    private void LoadCache()
    {
        if (_cacheFilePath is null) return;

        lock (_cacheSync)
        {
            try
            {
                if (!File.Exists(_cacheFilePath)) return;

                var json = File.ReadAllText(_cacheFilePath, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<CacheDocument>(json);
                if (doc is not { Version: CacheVersion, Entries: not null })
                {
                    AppLogger.Warn("[FocusInsightCoachingService] 코칭 캐시 파일 형식이 유효하지 않아 무시합니다.");
                    return;
                }

                var valid = new Dictionary<string, CachedCoachingEntry>(StringComparer.Ordinal);
                foreach (var (key, entry) in doc.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(key) && IsValidCachedEntry(entry))
                    {
                        valid[key] = entry;
                    }
                }

                _cacheEntries = valid;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLogger.Warn($"[FocusInsightCoachingService] 코칭 캐시 불러오기 실패: {ex.Message}");
                _cacheEntries = new Dictionary<string, CachedCoachingEntry>(StringComparer.Ordinal);
            }
        }
    }

    private void SaveCacheEntry(InsightPeriod period, InsightCoachingResult result)
    {
        if (string.IsNullOrWhiteSpace(result.DataFingerprint)) return;

        lock (_cacheSync)
        {
            _cacheEntries[period.ToString()] = new CachedCoachingEntry(
                Headline: result.Headline,
                Findings: result.Findings.ToArray(),
                ActionTip: result.ActionTip,
                GeneratedAtUtc: result.GeneratedAtUtc,
                DataFingerprint: result.DataFingerprint);

            if (_cacheFilePath is null) return;

            var doc = new CacheDocument(
                CacheVersion,
                new Dictionary<string, CachedCoachingEntry>(_cacheEntries, StringComparer.Ordinal));

            var fullPath = Path.GetFullPath(_cacheFilePath);
            var dir = Path.GetDirectoryName(fullPath);
            var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, doc, CacheJsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, fullPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn($"[FocusInsightCoachingService] 코칭 캐시 저장 실패: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of temp file
                }
            }
        }
    }

    private static bool IsValidCachedEntry(CachedCoachingEntry? entry) =>
        entry is not null
        && !string.IsNullOrWhiteSpace(entry.Headline)
        && !string.IsNullOrWhiteSpace(entry.ActionTip)
        && !string.IsNullOrWhiteSpace(entry.DataFingerprint)
        && entry.Findings is { Length: >= 1 and <= 2 }
        && entry.Findings.All(f => !string.IsNullOrWhiteSpace(f));

    private static string GetPeriodLabel(InsightPeriod period) => period switch
    {
        InsightPeriod.Today => "오늘",
        InsightPeriod.Last7Days => "최근 7일",
        InsightPeriod.AllTime => "전체 기간",
        _ => "선택 기간"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0분";
        var totalMinutes = (int)Math.Floor(duration.TotalMinutes);
        if (totalMinutes <= 0) return "1분 미만";
        if (totalMinutes < 60) return $"{totalMinutes}분";

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return minutes == 0 ? $"{hours}시간" : $"{hours}시간 {minutes}분";
    }
}

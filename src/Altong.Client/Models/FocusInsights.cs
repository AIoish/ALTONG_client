using Altong.Client.Data.Models;

namespace Altong.Client.Models;

public enum InsightPeriod
{
    Today,
    Last7Days,
    AllTime
}

public sealed record InsightHourlyBucket(
    int Hour,
    TimeSpan FocusDuration,
    int BlockedCount,
    int PassedCount,
    double FocusBarHeight,
    double NotificationBarHeight,
    bool IsGoldenHour,
    bool IsDistractionPeak,
    string TooltipText);

public sealed record InsightDistractorItem(
    string DisplayLabel,
    string AppName,
    string? Sender,
    int TotalCount,
    int BlockedCount,
    int PassedCount,
    double BlockRatePercent);

public sealed record InsightWorkContextItem(
    string ProcessName,
    string DisplayName,
    TimeSpan FocusDuration,
    int TotalNotifications,
    int BlockedCount,
    int PassedCount,
    double BlockRatePercent);

public sealed record InsightDailyTrendItem(
    DateTime LocalDate,
    string DayLabel,
    TimeSpan FocusDuration,
    double BarHeight,
    bool IsToday,
    string TooltipText);

public sealed record InsightHeatmapDay(
    DateTime LocalDate,
    TimeSpan FocusDuration,
    int IntensityLevel,
    bool IsToday,
    bool IsFuture,
    string TooltipText);

public sealed record InsightQualityMetrics(
    int FlowProtectionEligibleCount,
    int FlowProtectedCount,
    double FlowProtectionRatePercent,
    TimeSpan WorkDuration,
    TimeSpan NeutralDuration,
    TimeSpan LeisureDuration,
    double WorkPurityPercent,
    double NeutralPercent,
    double LeisurePercent,
    string? TopSelfDistractionApp,
    TimeSpan TopSelfDistractionDuration,
    int DeepFocusSessionCount,
    TimeSpan AverageSessionDuration,
    int CurrentStreakDays,
    double? PeriodChangePercent,
    IReadOnlyList<InsightDailyTrendItem> DailyTrends,
    int BestStreakDays = 0,
    bool IsTodayFocused = false,
    IReadOnlyList<InsightHeatmapDay>? HeatmapDays = null);

public sealed record FocusInsightSnapshot(
    InsightPeriod Period,
    DateTime GeneratedAtUtc,
    TimeSpan TotalFocusDuration,
    int FocusSessionCount,
    int TotalNotifications,
    int BlockedCount,
    int PassedCount,
    int UrgentPassedCount,
    int RelevancePassedCount,
    int OtherPassedCount,
    double BlockRatePercent,
    int? GoldenHour,
    TimeSpan GoldenHourDuration,
    int? DistractionPeakHour,
    int DistractionPeakCount,
    IReadOnlyList<InsightHourlyBucket> HourlyBuckets,
    IReadOnlyList<InsightDistractorItem> TopDistractors,
    IReadOnlyList<InsightWorkContextItem> TopWorkContexts,
    string DataFingerprint,
    string RuleSummaryLine,
    InsightQualityMetrics? Quality = null)
{
    public bool HasData => FocusSessionCount > 0 || TotalNotifications > 0;
}

public sealed record InsightCoachingResult(
    string Headline,
    IReadOnlyList<string> Findings,
    string ActionTip,
    bool IsAiGenerated,
    bool IsFromCache,
    DateTime GeneratedAtUtc,
    string DataFingerprint);

public interface IFocusInsightRepository
{
    Task<FocusInsightSnapshot> ReadAsync(
        InsightPeriod period,
        DateTime nowUtc,
        FocusSessionRecord? activeSession = null,
        TimeZoneInfo? timeZone = null,
        CancellationToken ct = default);
}

public interface IFocusInsightCoachingService
{
    bool IsAiAvailable { get; }

    InsightCoachingResult GetInitialOrCached(FocusInsightSnapshot snapshot);

    Task<InsightCoachingResult> GenerateAsync(
        FocusInsightSnapshot snapshot,
        bool forceRefresh = false,
        CancellationToken ct = default);
}

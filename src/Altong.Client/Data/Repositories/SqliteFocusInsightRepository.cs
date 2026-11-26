using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Focus;
using Microsoft.Data.Sqlite;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// 기존 focus_sessions, notifications, window_sessions 테이블을 읽기 전용으로 집계해
/// 집중 인사이트 스냅샷을 생성하는 SQLite 분석 리포지토리.
/// </summary>
public sealed class SqliteFocusInsightRepository : IFocusInsightRepository
{
    private const double MaxBarHeight = 88.0;
    private const double MaxDailyTrendBarHeight = 72.0;
    private static readonly TimeSpan BoundarySkewTolerance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlowProtectionStayThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FlowProtectionSameAppGapTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeepFocusThreshold = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan SelfDistractionMinDuration = TimeSpan.FromMinutes(1);
    private static readonly string[] KoreanDayLabels = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly IAltongDatabase _database;

    public SqliteFocusInsightRepository(IAltongDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<FocusInsightSnapshot> ReadAsync(
        InsightPeriod period,
        DateTime nowUtc,
        FocusSessionRecord? activeSession = null,
        TimeZoneInfo? timeZone = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var zone = timeZone ?? TimeZoneInfo.Local;
        var now = AsUtc(nowUtc);
        var (fromUtc, toUtc) = ResolveRange(period, now, zone);
        var limit = toUtc.HasValue && toUtc.Value < now ? toUtc.Value : now;

        using var connection = _database.CreateOpenConnection();

        var allFocusRecords = await ReadFocusSessionsAsync(connection, null, null, ct).ConfigureAwait(false);
        var notifications = await ReadNotificationsAsync(connection, fromUtc, toUtc, ct).ConfigureAwait(false);
        var windowSessions = await ReadFocusWindowSessionsAsync(connection, fromUtc, toUtc, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        // 1. 집중 세션 구간 병합 및 시간대별 버킷 집계
        var (totalFocusDuration, focusSessionCount, deepFocusSessionCount, hourlyFocusTicks, latestFocusEnd) =
            AggregateFocusSessions(allFocusRecords, activeSession, fromUtc, limit, now, zone);

        // 2. 알림 집계 (차단/통과, 통과 사유 분류, 시간대별 건수, 방해원 그룹핑)
        var hourlyBlocked = new int[24];
        var hourlyPassed = new int[24];
        int blockedCount = 0;
        int passedCount = 0;
        int urgentPassedCount = 0;
        int relevancePassedCount = 0;
        int otherPassedCount = 0;
        DateTime? latestNotificationAt = null;

        var distractorGroups = new Dictionary<(string AppKey, string SenderKey), DistractorAccumulator>();

        foreach (var n in notifications)
        {
            if (latestNotificationAt is null || n.ReceivedAtUtc > latestNotificationAt.Value)
                latestNotificationAt = n.ReceivedAtUtc;

            int localHour = TimeZoneInfo.ConvertTimeFromUtc(n.ReceivedAtUtc, zone).Hour;
            if (n.IsPassed)
            {
                passedCount++;
                hourlyPassed[localHour]++;

                if (n.UrgencyScore >= 4)
                    urgentPassedCount++;
                else if (n.RelevanceScore >= 3)
                    relevancePassedCount++;
                else
                    otherPassedCount++;
            }
            else
            {
                blockedCount++;
                hourlyBlocked[localHour]++;
            }

            string normalizedApp = string.IsNullOrWhiteSpace(n.AppName) ? "알 수 없는 앱" : n.AppName.Trim();
            string? normalizedSender = string.IsNullOrWhiteSpace(n.Sender) ? null : n.Sender.Trim();
            var groupKey = (normalizedApp.ToUpperInvariant(), (normalizedSender ?? string.Empty).ToUpperInvariant());

            if (!distractorGroups.TryGetValue(groupKey, out var acc))
            {
                acc = new DistractorAccumulator(normalizedApp, normalizedSender);
                distractorGroups[groupKey] = acc;
            }

            if (n.IsPassed) acc.PassedCount++;
            else acc.BlockedCount++;
        }

        int totalNotifications = blockedCount + passedCount;
        double blockRatePercent = ComputePercent(blockedCount, totalNotifications);

        // 3. 24시간 버킷, 골든타임, 방해 피크 계산
        var (hourlyBuckets, goldenHour, goldenHourDuration, distractionPeakHour, distractionPeakCount) =
            BuildHourlyBuckets(hourlyFocusTicks, hourlyBlocked, hourlyPassed);

        // 4. 주요 방해원 TOP 5
        var topDistractors = distractorGroups.Values
            .Select(acc =>
            {
                int total = acc.BlockedCount + acc.PassedCount;
                string displayApp = AppNameDisplayConverter.Format(acc.AppName);
                string displayLabel = acc.Sender is not null ? $"{displayApp} · {acc.Sender}" : displayApp;
                return new InsightDistractorItem(
                    displayLabel,
                    acc.AppName,
                    acc.Sender,
                    total,
                    acc.BlockedCount,
                    acc.PassedCount,
                    ComputePercent(acc.BlockedCount, total));
            })
            .OrderByDescending(item => item.BlockedCount)
            .ThenByDescending(item => item.TotalCount)
            .ThenBy(item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        // 5. 작업 맥락별 보호 현황 TOP 5 + 몰입 유지율 + 집중 순도 + 자기 방해 앱 분석
        var windowAnalysis = AnalyzeWindowContexts(notifications, windowSessions, fromUtc, limit);

        // 6. 연속 집중 일수, 최장 스트릭, 10주 잔디밭, 최근 7일 일별 추이, 전기 대비 증감률 계산
        var (currentStreakDays, bestStreakDays, isTodayFocused, periodChangePercent, dailyTrends, heatmapDays) =
            BuildTrendAndStreakMetrics(allFocusRecords, activeSession, period, now, zone);

        TimeSpan averageSessionDuration = focusSessionCount > 0
            ? TimeSpan.FromTicks(totalFocusDuration.Ticks / focusSessionCount)
            : TimeSpan.Zero;

        var quality = new InsightQualityMetrics(
            FlowProtectionEligibleCount: windowAnalysis.FlowProtectionEligibleCount,
            FlowProtectedCount: windowAnalysis.FlowProtectedCount,
            FlowProtectionRatePercent: windowAnalysis.FlowProtectionRatePercent,
            WorkDuration: windowAnalysis.WorkDuration,
            NeutralDuration: windowAnalysis.NeutralDuration,
            LeisureDuration: windowAnalysis.LeisureDuration,
            WorkPurityPercent: windowAnalysis.WorkPurityPercent,
            NeutralPercent: windowAnalysis.NeutralPercent,
            LeisurePercent: windowAnalysis.LeisurePercent,
            TopSelfDistractionApp: windowAnalysis.TopSelfDistractionApp,
            TopSelfDistractionDuration: windowAnalysis.TopSelfDistractionDuration,
            DeepFocusSessionCount: deepFocusSessionCount,
            AverageSessionDuration: averageSessionDuration,
            CurrentStreakDays: currentStreakDays,
            PeriodChangePercent: periodChangePercent,
            DailyTrends: dailyTrends,
            BestStreakDays: bestStreakDays,
            IsTodayFocused: isTodayFocused,
            HeatmapDays: heatmapDays);

        // 7. 결정적 데이터 지문 및 1줄 규칙 요약
        string dataFingerprint = ComputeFingerprint(
            period,
            focusSessionCount,
            totalFocusDuration,
            totalNotifications,
            blockedCount,
            passedCount,
            urgentPassedCount,
            relevancePassedCount,
            otherPassedCount,
            latestFocusEnd,
            latestNotificationAt,
            quality.FlowProtectedCount,
            quality.WorkDuration,
            quality.CurrentStreakDays);

        string ruleSummaryLine = BuildRuleSummaryLine(
            period,
            focusSessionCount,
            totalFocusDuration,
            totalNotifications,
            blockedCount,
            passedCount,
            blockRatePercent,
            goldenHour);

        return new FocusInsightSnapshot(
            Period: period,
            GeneratedAtUtc: now,
            TotalFocusDuration: totalFocusDuration,
            FocusSessionCount: focusSessionCount,
            TotalNotifications: totalNotifications,
            BlockedCount: blockedCount,
            PassedCount: passedCount,
            UrgentPassedCount: urgentPassedCount,
            RelevancePassedCount: relevancePassedCount,
            OtherPassedCount: otherPassedCount,
            BlockRatePercent: blockRatePercent,
            GoldenHour: goldenHour,
            GoldenHourDuration: goldenHourDuration,
            DistractionPeakHour: distractionPeakHour,
            DistractionPeakCount: distractionPeakCount,
            HourlyBuckets: hourlyBuckets,
            TopDistractors: topDistractors,
            TopWorkContexts: windowAnalysis.TopWorkContexts,
            DataFingerprint: dataFingerprint,
            RuleSummaryLine: ruleSummaryLine,
            Quality: quality);
    }

    private static (DateTime? FromUtc, DateTime? ToUtc) ResolveRange(
        InsightPeriod period,
        DateTime nowUtc,
        TimeZoneInfo zone)
    {
        if (period == InsightPeriod.AllTime)
            return (null, null);

        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        var startLocalDate = period == InsightPeriod.Last7Days ? localToday.AddDays(-6) : localToday;
        var range = SqliteSessionReportRepository.LocalRange(startLocalDate, localToday.AddDays(1), zone);
        return (range.FromUtc, range.ToUtc);
    }

    private static async Task<List<FocusSessionRecord>> ReadFocusSessionsAsync(
        SqliteConnection connection,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH observed_sessions AS (
                SELECT f.session_id, f.started_at,
                    COALESCE(f.ended_at, (
                        SELECT w.ended_at FROM window_sessions w
                        WHERE w.session_id = f.session_id
                        ORDER BY julianday(w.ended_at) DESC LIMIT 1
                    )) AS observed_end
                FROM focus_sessions f
                WHERE ($to IS NULL OR julianday(f.started_at) < julianday($to))
            )
            SELECT session_id, started_at, observed_end FROM observed_sessions
            WHERE observed_end IS NOT NULL
              AND ($from IS NULL OR julianday(observed_end) > julianday($from));
            """;
        command.Parameters.AddWithValue("$from", fromUtc.HasValue ? StampUtc(fromUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", toUtc.HasValue ? StampUtc(toUtc.Value) : DBNull.Value);

        var list = new List<FocusSessionRecord>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new FocusSessionRecord(
                reader.GetString(0),
                ParseUtc(reader.GetString(1)),
                ParseUtc(reader.GetString(2))));
        }

        return list;
    }

    private static async Task<List<NotificationRow>> ReadNotificationsAsync(
        SqliteConnection connection,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT app_name, sender, received_at, is_passed, urgency_score, relevance_score, session_id
            FROM notifications
            WHERE is_passed IN (0, 1)
              AND ($from IS NULL OR julianday(received_at) >= julianday($from))
              AND ($to IS NULL OR julianday(received_at) < julianday($to));
            """;
        command.Parameters.AddWithValue("$from", fromUtc.HasValue ? StampUtc(fromUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", toUtc.HasValue ? StampUtc(toUtc.Value) : DBNull.Value);

        var list = new List<NotificationRow>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new NotificationRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ParseUtc(reader.GetString(2)),
                reader.GetInt32(3) == 1,
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return list;
    }

    private static async Task<List<WindowSessionRow>> ReadFocusWindowSessionsAsync(
        SqliteConnection connection,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, started_at, ended_at, duration_seconds, session_id
            FROM window_sessions
            WHERE session_id IS NOT NULL AND trim(session_id) != ''
              AND ($to IS NULL OR julianday(started_at) < julianday($to))
              AND ($from IS NULL OR julianday(ended_at) > julianday($from));
            """;
        DateTime? paddedFrom = fromUtc?.Subtract(BoundarySkewTolerance);
        DateTime? paddedTo = toUtc?.Add(BoundarySkewTolerance);
        command.Parameters.AddWithValue("$from", paddedFrom.HasValue ? StampUtc(paddedFrom.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", paddedTo.HasValue ? StampUtc(paddedTo.Value) : DBNull.Value);

        var list = new List<WindowSessionRow>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new WindowSessionRow(
                reader.GetString(0),
                ParseUtc(reader.GetString(1)),
                ParseUtc(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetString(4)));
        }

        return list;
    }

    private static (
        TimeSpan TotalFocusDuration,
        int FocusSessionCount,
        int DeepFocusSessionCount,
        long[] HourlyFocusTicks,
        DateTime? LatestFocusEnd)
        AggregateFocusSessions(
            IEnumerable<FocusSessionRecord> records,
            FocusSessionRecord? activeSession,
            DateTime? fromUtc,
            DateTime limit,
            DateTime nowUtc,
            TimeZoneInfo zone)
    {
        var intervals = new List<(DateTime Start, DateTime End)>();
        var intervalsBySession = new Dictionary<string, List<(DateTime Start, DateTime End)>>(StringComparer.Ordinal);
        DateTime? latestFocusEnd = null;

        var candidates = activeSession is null ? records : records.Append(activeSession);
        foreach (var record in candidates)
        {
            DateTime? end = record.EndedAt.HasValue ? AsUtc(record.EndedAt.Value) : null;
            if (end is null && activeSession is not null && record.SessionId == activeSession.SessionId)
                end = nowUtc;
            if (end is null)
                continue;

            var start = AsUtc(record.StartedAt);
            if (fromUtc.HasValue && start < fromUtc.Value)
                start = fromUtc.Value;

            var clippedEnd = end.Value > limit ? limit : end.Value;
            if (clippedEnd <= start)
                continue;

            intervals.Add((start, clippedEnd));
            if (!intervalsBySession.TryGetValue(record.SessionId, out var sessionIntervals))
            {
                sessionIntervals = [];
                intervalsBySession[record.SessionId] = sessionIntervals;
            }
            sessionIntervals.Add((start, clippedEnd));

            if (latestFocusEnd is null || clippedEnd > latestFocusEnd.Value)
                latestFocusEnd = clippedEnd;
        }

        int deepFocusSessionCount = 0;
        foreach (var sessionIntervals in intervalsBySession.Values)
        {
            var mergedSession = MergeIntervals(sessionIntervals);
            long sessionTicks = 0;
            foreach (var segment in mergedSession)
                sessionTicks += (segment.End - segment.Start).Ticks;

            if (sessionTicks >= DeepFocusThreshold.Ticks)
                deepFocusSessionCount++;
        }

        // 겹치는 집중 구간을 병합하여 시간이 이중 계산되지 않도록 보장한다.
        var merged = MergeIntervals(intervals);

        var hourlyTicks = new long[24];
        long totalTicks = 0;

        foreach (var interval in merged)
        {
            totalTicks += (interval.End - interval.Start).Ticks;
            var cursor = interval.Start;
            while (cursor < interval.End)
            {
                int hour = TimeZoneInfo.ConvertTimeFromUtc(cursor, zone).Hour;
                var nextHourUtc = new DateTime(cursor.Ticks - cursor.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc)
                    .AddHours(1);
                var hourCandidateEnd = nextHourUtc < interval.End ? nextHourUtc : interval.End;

                DateTime sliceEnd;
                if (TimeZoneInfo.ConvertTimeFromUtc(hourCandidateEnd.AddTicks(-1), zone).Hour == hour)
                {
                    sliceEnd = hourCandidateEnd;
                }
                else
                {
                    var nextMinuteUtc = new DateTime(cursor.Ticks - cursor.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc)
                        .AddMinutes(1);
                    sliceEnd = nextMinuteUtc < interval.End ? nextMinuteUtc : interval.End;
                }

                hourlyTicks[hour] += (sliceEnd - cursor).Ticks;
                cursor = sliceEnd;
            }
        }

        return (
            TimeSpan.FromTicks(totalTicks),
            intervalsBySession.Count,
            deepFocusSessionCount,
            hourlyTicks,
            latestFocusEnd);
    }

    private static List<(DateTime Start, DateTime End)> MergeIntervals(List<(DateTime Start, DateTime End)> intervals)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in intervals.OrderBy(item => item.Start))
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End)
                merged.Add(interval);
            else if (interval.End > merged[^1].End)
                merged[^1] = (merged[^1].Start, interval.End);
        }

        return merged;
    }

    private static (
        int CurrentStreakDays,
        int BestStreakDays,
        bool IsTodayFocused,
        double? PeriodChangePercent,
        IReadOnlyList<InsightDailyTrendItem> DailyTrends,
        IReadOnlyList<InsightHeatmapDay> HeatmapDays)
        BuildTrendAndStreakMetrics(
            IEnumerable<FocusSessionRecord> records,
            FocusSessionRecord? activeSession,
            InsightPeriod period,
            DateTime nowUtc,
            TimeZoneInfo zone)
    {
        var intervals = new List<(DateTime Start, DateTime End)>();
        var candidates = activeSession is null ? records : records.Append(activeSession);
        foreach (var record in candidates)
        {
            DateTime? end = record.EndedAt.HasValue ? AsUtc(record.EndedAt.Value) : null;
            if (end is null && activeSession is not null && record.SessionId == activeSession.SessionId)
                end = nowUtc;
            if (end is null)
                continue;

            var start = AsUtc(record.StartedAt);
            var clippedEnd = end.Value > nowUtc ? nowUtc : end.Value;
            if (clippedEnd <= start)
                continue;

            intervals.Add((start, clippedEnd));
        }

        var merged = MergeIntervals(intervals);
        var dailyTicks = new Dictionary<DateTime, long>();

        foreach (var interval in merged)
        {
            var cursor = interval.Start;
            while (cursor < interval.End)
            {
                var localDate = TimeZoneInfo.ConvertTimeFromUtc(cursor, zone).Date;
                var nextDayUtc = ConvertLocalMidnightToUtc(localDate.AddDays(1), zone);
                if (nextDayUtc <= cursor)
                    nextDayUtc = cursor.AddHours(1);

                var sliceEnd = nextDayUtc < interval.End ? nextDayUtc : interval.End;
                long ticks = (sliceEnd - cursor).Ticks;
                if (ticks > 0)
                    dailyTicks[localDate] = dailyTicks.GetValueOrDefault(localDate) + ticks;

                cursor = sliceEnd;
            }
        }

        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        bool isTodayFocused = dailyTicks.GetValueOrDefault(localToday) > 0;

        int currentStreakDays = 0;
        DateTime streakCursor = isTodayFocused
            ? localToday
            : localToday.AddDays(-1);
        while (streakCursor > DateTime.MinValue.AddDays(1) && dailyTicks.GetValueOrDefault(streakCursor) > 0)
        {
            currentStreakDays++;
            streakCursor = streakCursor.AddDays(-1);
        }

        int bestStreakDays = ComputeBestStreakDays(dailyTicks);
        var heatmapDays = BuildHeatmapDays(dailyTicks, localToday);

        long maxTrendTicks = 0;
        var trendDays = new (DateTime Date, long Ticks)[7];
        for (int i = 0; i < 7; i++)
        {
            var date = localToday.AddDays(i - 6);
            long ticks = dailyTicks.GetValueOrDefault(date);
            trendDays[i] = (date, ticks);
            if (ticks > maxTrendTicks)
                maxTrendTicks = ticks;
        }

        var dailyTrends = new InsightDailyTrendItem[7];
        for (int i = 0; i < 7; i++)
        {
            var (date, ticks) = trendDays[i];
            var duration = TimeSpan.FromTicks(ticks);
            string dayLabel = KoreanDayLabels[(int)date.DayOfWeek];
            double barHeight = maxTrendTicks > 0
                ? Math.Clamp(Math.Round((double)ticks / maxTrendTicks * MaxDailyTrendBarHeight, 2), 0.0, MaxDailyTrendBarHeight)
                : 0.0;
            bool isToday = i == 6;
            string tooltip = string.Create(
                CultureInfo.InvariantCulture,
                $"{date:MM/dd} ({dayLabel}) · 집중 {FocusUsageHour.FormatDuration(duration)}");

            dailyTrends[i] = new InsightDailyTrendItem(
                LocalDate: date,
                DayLabel: dayLabel,
                FocusDuration: duration,
                BarHeight: barHeight,
                IsToday: isToday,
                TooltipText: tooltip);
        }

        double? periodChangePercent = null;
        if (period == InsightPeriod.Today)
        {
            long currentTicks = dailyTicks.GetValueOrDefault(localToday);
            long previousTicks = dailyTicks.GetValueOrDefault(localToday.AddDays(-1));
            if (previousTicks > 0)
                periodChangePercent = Math.Round((double)(currentTicks - previousTicks) * 100.0 / previousTicks, 1);
        }
        else if (period == InsightPeriod.Last7Days)
        {
            long currentTicks = 0;
            for (int d = -6; d <= 0; d++)
                currentTicks += dailyTicks.GetValueOrDefault(localToday.AddDays(d));

            long previousTicks = 0;
            for (int d = -13; d <= -7; d++)
                previousTicks += dailyTicks.GetValueOrDefault(localToday.AddDays(d));

            if (previousTicks > 0)
                periodChangePercent = Math.Round((double)(currentTicks - previousTicks) * 100.0 / previousTicks, 1);
        }

        return (currentStreakDays, bestStreakDays, isTodayFocused, periodChangePercent, dailyTrends, heatmapDays);
    }

    private static int ComputeBestStreakDays(Dictionary<DateTime, long> dailyTicks)
    {
        if (dailyTicks.Count == 0)
            return 0;

        var activeDates = dailyTicks
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .OrderBy(d => d)
            .ToArray();

        if (activeDates.Length == 0)
            return 0;

        int best = 1;
        int current = 1;
        for (int i = 1; i < activeDates.Length; i++)
        {
            if (activeDates[i] == activeDates[i - 1].AddDays(1))
            {
                current++;
                if (current > best)
                    best = current;
            }
            else
            {
                current = 1;
            }
        }

        return best;
    }

    private static IReadOnlyList<InsightHeatmapDay> BuildHeatmapDays(
        Dictionary<DateTime, long> dailyTicks,
        DateTime localToday)
    {
        int todayDow = (int)localToday.DayOfWeek; // 0 = Sunday .. 6 = Saturday
        var currentWeekSunday = localToday.AddDays(-todayDow);
        var gridStart = currentWeekSunday.AddDays(-9 * 7);

        var days = new InsightHeatmapDay[70];
        for (int offset = 0; offset < 70; offset++)
        {
            var date = gridStart.AddDays(offset);
            bool isFuture = date > localToday;
            bool isToday = date == localToday;
            long ticks = !isFuture ? dailyTicks.GetValueOrDefault(date) : 0;
            var duration = TimeSpan.FromTicks(ticks);

            int level = isFuture || duration <= TimeSpan.Zero ? 0
                : duration.TotalMinutes < 30 ? 1
                : duration.TotalMinutes < 60 ? 2
                : duration.TotalMinutes < 120 ? 3
                : 4;

            string dayLabel = KoreanDayLabels[(int)date.DayOfWeek];
            string tooltip = isFuture
                ? string.Empty
                : duration <= TimeSpan.Zero
                    ? $"{date:M월 d일} ({dayLabel}) · 집중 기록 없음"
                    : $"{date:M월 d일} ({dayLabel}) · 집중 {FocusUsageHour.FormatDuration(duration)}";

            days[offset] = new InsightHeatmapDay(
                LocalDate: date,
                FocusDuration: duration,
                IntensityLevel: level,
                IsToday: isToday,
                IsFuture: isFuture,
                TooltipText: tooltip);
        }

        return days;
    }

    private static DateTime ConvertLocalMidnightToUtc(DateTime localMidnight, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        if (zone.IsAmbiguousTime(unspecified))
        {
            var maxOffset = zone.GetAmbiguousTimeOffsets(unspecified).Max();
            return DateTime.SpecifyKind(unspecified - maxOffset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static (
        IReadOnlyList<InsightHourlyBucket> Buckets,
        int? GoldenHour,
        TimeSpan GoldenHourDuration,
        int? DistractionPeakHour,
        int DistractionPeakCount)
        BuildHourlyBuckets(long[] hourlyFocusTicks, int[] hourlyBlocked, int[] hourlyPassed)
    {
        long maxFocusTicks = 0;
        int? goldenHour = null;

        int maxNotificationCount = 0;
        int? distractionPeakHour = null;

        for (int h = 0; h < 24; h++)
        {
            if (hourlyFocusTicks[h] > maxFocusTicks)
            {
                maxFocusTicks = hourlyFocusTicks[h];
                goldenHour = h;
            }

            int totalInHour = hourlyBlocked[h] + hourlyPassed[h];
            if (totalInHour > maxNotificationCount)
            {
                maxNotificationCount = totalInHour;
                distractionPeakHour = h;
            }
        }

        var buckets = new InsightHourlyBucket[24];
        for (int h = 0; h < 24; h++)
        {
            var focusDuration = TimeSpan.FromTicks(hourlyFocusTicks[h]);
            int blocked = hourlyBlocked[h];
            int passed = hourlyPassed[h];
            int totalInHour = blocked + passed;

            double focusBarHeight = maxFocusTicks > 0
                ? Math.Clamp(Math.Round((double)hourlyFocusTicks[h] / maxFocusTicks * MaxBarHeight, 2), 0.0, MaxBarHeight)
                : 0.0;
            double notifBarHeight = maxNotificationCount > 0
                ? Math.Clamp(Math.Round((double)totalInHour / maxNotificationCount * MaxBarHeight, 2), 0.0, MaxBarHeight)
                : 0.0;

            bool isGolden = goldenHour.HasValue && h == goldenHour.Value;
            bool isPeak = distractionPeakHour.HasValue && h == distractionPeakHour.Value;
            string tooltip = $"{h:00}시 · 집중 {FocusUsageHour.FormatDuration(focusDuration)} · 차단 {blocked}건 · 통과 {passed}건";

            buckets[h] = new InsightHourlyBucket(
                Hour: h,
                FocusDuration: focusDuration,
                BlockedCount: blocked,
                PassedCount: passed,
                FocusBarHeight: focusBarHeight,
                NotificationBarHeight: notifBarHeight,
                IsGoldenHour: isGolden,
                IsDistractionPeak: isPeak,
                TooltipText: tooltip);
        }

        return (
            buckets,
            goldenHour,
            TimeSpan.FromTicks(maxFocusTicks),
            distractionPeakHour,
            maxNotificationCount);
    }

    private static WindowContextAnalysis AnalyzeWindowContexts(
        IReadOnlyList<NotificationRow> notifications,
        IReadOnlyList<WindowSessionRow> windowSessions,
        DateTime? fromUtc,
        DateTime limit)
    {
        if (windowSessions.Count == 0)
            return WindowContextAnalysis.Empty;

        var contextsByProcess = new Dictionary<string, WorkContextAccumulator>(StringComparer.OrdinalIgnoreCase);
        var windowsBySession = new Dictionary<string, List<WindowSessionRow>>(StringComparer.Ordinal);
        var selfDistractionByProcess = new Dictionary<string, SelfDistractionAccumulator>(StringComparer.OrdinalIgnoreCase);

        long workTicks = 0;
        long neutralTicks = 0;
        long leisureTicks = 0;

        foreach (var ws in windowSessions)
        {
            string processName = string.IsNullOrWhiteSpace(ws.ProcessName) ? "알 수 없는 앱" : ws.ProcessName.Trim();
            long durationTicks = ComputeWindowDurationTicks(ws, fromUtc, limit);

            var category = WorkContextClassifier.Classify(ws.ProcessName);
            switch (category)
            {
                case WorkCategory.Work:
                    workTicks += durationTicks;
                    break;
                case WorkCategory.Leisure:
                    leisureTicks += durationTicks;
                    break;
                default:
                    neutralTicks += durationTicks;
                    break;
            }

            if (category != WorkCategory.Work && durationTicks > 0)
            {
                string normalizedKey = NormalizeProcessKey(ws.ProcessName);
                if (!string.IsNullOrEmpty(normalizedKey)
                    && !string.Equals(normalizedKey, "Altong.Client", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedKey, "explorer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!selfDistractionByProcess.TryGetValue(normalizedKey, out var distractionAcc))
                    {
                        distractionAcc = new SelfDistractionAccumulator(processName);
                        selfDistractionByProcess[normalizedKey] = distractionAcc;
                    }
                    distractionAcc.Ticks += durationTicks;
                }
            }

            if (!windowsBySession.TryGetValue(ws.SessionId, out var sessionList))
            {
                sessionList = [];
                windowsBySession[ws.SessionId] = sessionList;
            }
            sessionList.Add(ws with { ProcessName = processName });

            if (!contextsByProcess.TryGetValue(processName, out var acc))
            {
                acc = new WorkContextAccumulator(processName);
                contextsByProcess[processName] = acc;
            }

            acc.FocusTicks += durationTicks;
        }

        int flowProtectionEligibleCount = 0;
        int flowProtectedCount = 0;

        if (notifications.Count > 0)
        {
            var indexedWindowsBySession = new Dictionary<string, SessionWindowIndex>(windowsBySession.Count, StringComparer.Ordinal);
            foreach (var (sessionId, list) in windowsBySession)
            {
                list.Sort(static (a, b) => a.StartedAtUtc.CompareTo(b.StartedAtUtc));
                var prefixMaxEnds = new DateTime[list.Count];
                DateTime maxEnd = DateTime.MinValue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].EndedAtUtc > maxEnd)
                        maxEnd = list[i].EndedAtUtc;
                    prefixMaxEnds[i] = maxEnd;
                }
                indexedWindowsBySession[sessionId] = new SessionWindowIndex(list, prefixMaxEnds);
            }

            foreach (var n in notifications)
            {
                if (string.IsNullOrWhiteSpace(n.SessionId))
                    continue;
                if (!indexedWindowsBySession.TryGetValue(n.SessionId, out var candidates))
                    continue;

                var (matched, directMatchIndex) = FindMatchingWindowSession(candidates, n.ReceivedAtUtc);
                if (matched is null)
                    continue;

                var acc = contextsByProcess[matched.ProcessName];
                if (n.IsPassed) acc.PassedCount++;
                else acc.BlockedCount++;

                if (!n.IsPassed && directMatchIndex >= 0)
                {
                    flowProtectionEligibleCount++;
                    var directWindow = candidates.Rows[directMatchIndex];
                    bool stayedInWindow = (directWindow.EndedAtUtc - n.ReceivedAtUtc) >= FlowProtectionStayThreshold;
                    bool continuedSameApp = false;

                    if (!stayedInWindow && directMatchIndex + 1 < candidates.Rows.Count)
                    {
                        var nextWindow = candidates.Rows[directMatchIndex + 1];
                        if (string.Equals(nextWindow.ProcessName, directWindow.ProcessName, StringComparison.OrdinalIgnoreCase)
                            && (nextWindow.StartedAtUtc - directWindow.EndedAtUtc) <= FlowProtectionSameAppGapTolerance)
                        {
                            continuedSameApp = true;
                        }
                    }

                    if (stayedInWindow || continuedSameApp)
                        flowProtectedCount++;
                }
            }
        }

        var topWorkContexts = contextsByProcess.Values
            .Where(acc => acc.BlockedCount + acc.PassedCount > 0)
            .Select(acc =>
            {
                int total = acc.BlockedCount + acc.PassedCount;
                return new InsightWorkContextItem(
                    ProcessName: acc.ProcessName,
                    DisplayName: AppNameDisplayConverter.Format(acc.ProcessName),
                    FocusDuration: TimeSpan.FromTicks(acc.FocusTicks),
                    TotalNotifications: total,
                    BlockedCount: acc.BlockedCount,
                    PassedCount: acc.PassedCount,
                    BlockRatePercent: ComputePercent(acc.BlockedCount, total));
            })
            .OrderByDescending(item => item.TotalNotifications)
            .ThenByDescending(item => item.FocusDuration)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        long totalCategoryTicks = workTicks + neutralTicks + leisureTicks;
        double workPurityPercent = ComputeTicksPercent(workTicks, totalCategoryTicks);
        double neutralPercent = ComputeTicksPercent(neutralTicks, totalCategoryTicks);
        double leisurePercent = ComputeTicksPercent(leisureTicks, totalCategoryTicks);

        string? topSelfDistractionApp = null;
        TimeSpan topSelfDistractionDuration = TimeSpan.Zero;
        var topSelfDistraction = selfDistractionByProcess.Values
            .OrderByDescending(acc => acc.Ticks)
            .ThenBy(acc => AppNameDisplayConverter.Format(acc.ProcessName), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (topSelfDistraction is not null && topSelfDistraction.Ticks >= SelfDistractionMinDuration.Ticks)
        {
            topSelfDistractionApp = AppNameDisplayConverter.Format(topSelfDistraction.ProcessName);
            topSelfDistractionDuration = TimeSpan.FromTicks(topSelfDistraction.Ticks);
        }

        return new WindowContextAnalysis(
            TopWorkContexts: topWorkContexts,
            FlowProtectionEligibleCount: flowProtectionEligibleCount,
            FlowProtectedCount: flowProtectedCount,
            FlowProtectionRatePercent: ComputePercent(flowProtectedCount, flowProtectionEligibleCount),
            WorkDuration: TimeSpan.FromTicks(workTicks),
            NeutralDuration: TimeSpan.FromTicks(neutralTicks),
            LeisureDuration: TimeSpan.FromTicks(leisureTicks),
            WorkPurityPercent: workPurityPercent,
            NeutralPercent: neutralPercent,
            LeisurePercent: leisurePercent,
            TopSelfDistractionApp: topSelfDistractionApp,
            TopSelfDistractionDuration: topSelfDistractionDuration);
    }

    private static string NormalizeProcessKey(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return string.Empty;

        string trimmed = processName.Trim();
        return trimmed.Length > 4 && trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static long ComputeWindowDurationTicks(WindowSessionRow ws, DateTime? fromUtc, DateTime limit)
    {
        if (ws.EndedAtUtc > ws.StartedAtUtc)
        {
            var start = fromUtc.HasValue && ws.StartedAtUtc < fromUtc.Value ? fromUtc.Value : ws.StartedAtUtc;
            var end = ws.EndedAtUtc > limit ? limit : ws.EndedAtUtc;
            return end > start ? (end - start).Ticks : 0;
        }

        if (ws.DurationSeconds > 0
            && (!fromUtc.HasValue || ws.StartedAtUtc >= fromUtc.Value)
            && ws.StartedAtUtc <= limit)
        {
            return TimeSpan.FromSeconds(ws.DurationSeconds).Ticks;
        }

        return 0;
    }

    private static (WindowSessionRow? Matched, int DirectMatchIndex) FindMatchingWindowSession(
        SessionWindowIndex index,
        DateTime receivedAtUtc)
    {
        var rows = index.Rows;
        if (rows.Count == 0)
            return (null, -1);

        // StartedAtUtc <= receivedAtUtc + BoundarySkewTolerance 인 마지막 인덱스를 이진 탐색한다.
        DateTime upperCutoff = receivedAtUtc <= DateTime.MaxValue - BoundarySkewTolerance
            ? receivedAtUtc + BoundarySkewTolerance
            : DateTime.MaxValue;
        int lo = 0;
        int hi = rows.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (rows[mid].StartedAtUtc <= upperCutoff)
                lo = mid + 1;
            else
                hi = mid;
        }

        DateTime lowerCutoff = receivedAtUtc >= DateTime.MinValue + BoundarySkewTolerance
            ? receivedAtUtc - BoundarySkewTolerance
            : DateTime.MinValue;

        WindowSessionRow? directMatch = null;
        int directMatchIndex = -1;
        WindowSessionRow? nearest = null;
        TimeSpan bestDistance = TimeSpan.MaxValue;

        for (int i = hi - 1; i >= 0; i--)
        {
            if (index.PrefixMaxEnds[i] < lowerCutoff)
                break;

            var ws = rows[i];
            if (receivedAtUtc >= ws.StartedAtUtc && receivedAtUtc <= ws.EndedAtUtc)
            {
                if (directMatch is null || ws.StartedAtUtc > directMatch.StartedAtUtc)
                {
                    directMatch = ws;
                    directMatchIndex = i;
                }
                continue;
            }

            if (directMatch is not null)
                continue;

            TimeSpan distance = receivedAtUtc < ws.StartedAtUtc
                ? ws.StartedAtUtc - receivedAtUtc
                : receivedAtUtc - ws.EndedAtUtc;

            if (distance <= BoundarySkewTolerance && distance < bestDistance)
            {
                bestDistance = distance;
                nearest = ws;
            }
        }

        return (directMatch ?? nearest, directMatchIndex);
    }

    private sealed record SessionWindowIndex(List<WindowSessionRow> Rows, DateTime[] PrefixMaxEnds);

    private static string ComputeFingerprint(
        InsightPeriod period,
        int focusSessionCount,
        TimeSpan totalFocusDuration,
        int totalNotifications,
        int blockedCount,
        int passedCount,
        int urgentPassedCount,
        int relevancePassedCount,
        int otherPassedCount,
        DateTime? latestFocusEnd,
        DateTime? latestNotificationAt,
        int flowProtectedCount,
        TimeSpan workDuration,
        int currentStreakDays)
    {
        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{period}|{focusSessionCount}|{(long)totalFocusDuration.TotalSeconds}|{totalNotifications}|{blockedCount}|{passedCount}|{urgentPassedCount}|{relevancePassedCount}|{otherPassedCount}|{latestFocusEnd?.ToUniversalTime().Ticks ?? 0}|{latestNotificationAt?.ToUniversalTime().Ticks ?? 0}|{flowProtectedCount}|{(long)workDuration.TotalSeconds}|{currentStreakDays}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string BuildRuleSummaryLine(
        InsightPeriod period,
        int focusSessionCount,
        TimeSpan totalFocusDuration,
        int totalNotifications,
        int blockedCount,
        int passedCount,
        double blockRatePercent,
        int? goldenHour)
    {
        if (focusSessionCount == 0 && totalNotifications == 0)
            return "아직 분석할 집중 세션이나 알림 기록이 없습니다.";

        string periodLabel = period switch
        {
            InsightPeriod.Today => "오늘",
            InsightPeriod.Last7Days => "최근 7일",
            _ => "전체 기간"
        };

        if (totalFocusDuration > TimeSpan.Zero && totalNotifications > 0)
        {
            string goldenPart = goldenHour.HasValue ? $" · 골든타임 {goldenHour.Value:00}시" : string.Empty;
            return $"{periodLabel} {FocusUsageHour.FormatDuration(totalFocusDuration)} 집중하며 알림 {totalNotifications}건 중 {blockedCount}건을 차단했어요 ({blockRatePercent:0.#}%{goldenPart}).";
        }

        if (totalFocusDuration > TimeSpan.Zero)
        {
            string goldenPart = goldenHour.HasValue ? $" (골든타임 {goldenHour.Value:00}시)" : string.Empty;
            return $"{periodLabel} 총 {FocusUsageHour.FormatDuration(totalFocusDuration)} 동안 방해 알림 없이 몰입했어요{goldenPart}.";
        }

        return $"{periodLabel} 알림 {totalNotifications}건 중 {blockedCount}건을 차단하고 {passedCount}건을 통과시켰어요 ({blockRatePercent:0.#}% 차단).";
    }

    private static double ComputePercent(int numerator, int denominator)
    {
        if (denominator <= 0)
            return 0.0;

        return Math.Round((double)numerator * 100.0 / denominator, 1);
    }

    private static double ComputeTicksPercent(long numeratorTicks, long denominatorTicks)
    {
        if (denominatorTicks <= 0 || numeratorTicks <= 0)
            return 0.0;

        return Math.Clamp(Math.Round((double)numeratorTicks * 100.0 / denominatorTicks, 1), 0.0, 100.0);
    }

    private static string StampUtc(DateTime utc) =>
        AsUtc(utc).ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string text) =>
        AsUtc(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private sealed record NotificationRow(
        string AppName,
        string? Sender,
        DateTime ReceivedAtUtc,
        bool IsPassed,
        int UrgencyScore,
        int RelevanceScore,
        string? SessionId);

    private sealed record WindowSessionRow(
        string ProcessName,
        DateTime StartedAtUtc,
        DateTime EndedAtUtc,
        int DurationSeconds,
        string SessionId);

    private sealed class DistractorAccumulator(string appName, string? sender)
    {
        public string AppName { get; } = appName;
        public string? Sender { get; } = sender;
        public int BlockedCount { get; set; }
        public int PassedCount { get; set; }
    }

    private sealed class WorkContextAccumulator(string processName)
    {
        public string ProcessName { get; } = processName;
        public long FocusTicks { get; set; }
        public int BlockedCount { get; set; }
        public int PassedCount { get; set; }
    }

    private sealed class SelfDistractionAccumulator(string processName)
    {
        public string ProcessName { get; } = processName;
        public long Ticks { get; set; }
    }

    private sealed record WindowContextAnalysis(
        IReadOnlyList<InsightWorkContextItem> TopWorkContexts,
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
        TimeSpan TopSelfDistractionDuration)
    {
        public static WindowContextAnalysis Empty { get; } = new(
            TopWorkContexts: [],
            FlowProtectionEligibleCount: 0,
            FlowProtectedCount: 0,
            FlowProtectionRatePercent: 0.0,
            WorkDuration: TimeSpan.Zero,
            NeutralDuration: TimeSpan.Zero,
            LeisureDuration: TimeSpan.Zero,
            WorkPurityPercent: 0.0,
            NeutralPercent: 0.0,
            LeisurePercent: 0.0,
            TopSelfDistractionApp: null,
            TopSelfDistractionDuration: TimeSpan.Zero);
    }
}

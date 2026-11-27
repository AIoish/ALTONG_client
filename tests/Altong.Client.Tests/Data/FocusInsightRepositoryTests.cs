using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.Data;

[TestClass]
public sealed class FocusInsightRepositoryTests
{
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
    private static readonly DateTime TodayLocal = new(2026, 11, 26);

    private static DateTime KstToUtc(int dayOffset, int hour, int minute = 0, int second = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            TodayLocal.AddDays(dayOffset).AddHours(hour).AddMinutes(minute).AddSeconds(second),
            Korea);

    private static NotificationRecord MakeNotification(
        string id,
        string appName,
        string? sender,
        DateTime receivedAtUtc,
        bool? isPassed,
        int? urgency = null,
        int? relevance = null,
        string? sessionId = null) =>
        new(id, appName, sender, $"제목-{id}", $"본문-{id}", receivedAtUtc, isPassed, urgency, relevance, "업무", null, sessionId);

    [TestMethod]
    public async Task EmptyDatabase_ReturnsZeroedSnapshotWithoutNaNOrExceptions()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var repo = new SqliteFocusInsightRepository(db);
        var nowUtc = KstToUtc(0, 15, 0);

        foreach (var period in new[] { InsightPeriod.Today, InsightPeriod.Last7Days, InsightPeriod.AllTime })
        {
            var snapshot = await repo.ReadAsync(period, nowUtc, timeZone: Korea);

            Assert.AreEqual(period, snapshot.Period);
            Assert.IsFalse(snapshot.HasData);
            Assert.AreEqual(TimeSpan.Zero, snapshot.TotalFocusDuration);
            Assert.AreEqual(0, snapshot.FocusSessionCount);
            Assert.AreEqual(0, snapshot.TotalNotifications);
            Assert.AreEqual(0, snapshot.BlockedCount);
            Assert.AreEqual(0, snapshot.PassedCount);
            Assert.AreEqual(0, snapshot.UrgentPassedCount);
            Assert.AreEqual(0, snapshot.RelevancePassedCount);
            Assert.AreEqual(0, snapshot.OtherPassedCount);
            Assert.AreEqual(0.0, snapshot.BlockRatePercent);
            Assert.IsNull(snapshot.GoldenHour);
            Assert.AreEqual(TimeSpan.Zero, snapshot.GoldenHourDuration);
            Assert.IsNull(snapshot.DistractionPeakHour);
            Assert.AreEqual(0, snapshot.DistractionPeakCount);
            Assert.AreEqual(24, snapshot.HourlyBuckets.Count);
            Assert.IsTrue(snapshot.HourlyBuckets.All(b =>
                b.FocusBarHeight == 0.0
                && !double.IsNaN(b.FocusBarHeight)
                && !double.IsInfinity(b.FocusBarHeight)
                && b.NotificationBarHeight == 0.0
                && !double.IsNaN(b.NotificationBarHeight)
                && !double.IsInfinity(b.NotificationBarHeight)
                && !b.IsGoldenHour
                && !b.IsDistractionPeak));
            Assert.AreEqual(0, snapshot.TopDistractors.Count);
            Assert.AreEqual(0, snapshot.TopWorkContexts.Count);
            Assert.AreEqual(16, snapshot.DataFingerprint.Length);
            Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.RuleSummaryLine));

            Assert.IsNotNull(snapshot.Quality);
            var q = snapshot.Quality!;
            Assert.AreEqual(0, q.FlowProtectionEligibleCount);
            Assert.AreEqual(0, q.FlowProtectedCount);
            Assert.AreEqual(0.0, q.FlowProtectionRatePercent);
            Assert.AreEqual(TimeSpan.Zero, q.WorkDuration);
            Assert.AreEqual(TimeSpan.Zero, q.NeutralDuration);
            Assert.AreEqual(TimeSpan.Zero, q.LeisureDuration);
            Assert.AreEqual(0.0, q.WorkPurityPercent);
            Assert.AreEqual(0.0, q.NeutralPercent);
            Assert.AreEqual(0.0, q.LeisurePercent);
            Assert.IsNull(q.TopSelfDistractionApp);
            Assert.AreEqual(TimeSpan.Zero, q.TopSelfDistractionDuration);
            Assert.AreEqual(0, q.DeepFocusSessionCount);
            Assert.AreEqual(TimeSpan.Zero, q.AverageSessionDuration);
            Assert.AreEqual(0, q.CurrentStreakDays);
            Assert.AreEqual(0, q.BestStreakDays);
            Assert.IsFalse(q.IsTodayFocused);
            Assert.IsNull(q.PeriodChangePercent);
            Assert.IsNull(q.SelfDistractionChangePercent);
            Assert.IsNotNull(q.HeatmapDays);
            Assert.AreEqual(140, q.HeatmapDays!.Count);
            Assert.AreEqual(1, q.HeatmapDays.Count(h => h.IsToday));
            Assert.IsTrue(q.HeatmapDays.All(h =>
                h.IntensityLevel == 0 && h.FocusDuration == TimeSpan.Zero &&
                h.SessionCount == 0 && h.BlockedCount == 0 && h.WorkPurityPercent is null));
            Assert.AreEqual(7, q.DailyTrends.Count);
            Assert.IsTrue(q.DailyTrends.All(d =>
                d.FocusDuration == TimeSpan.Zero
                && d.BarHeight == 0.0
                && !double.IsNaN(d.BarHeight)
                && !double.IsInfinity(d.BarHeight)
                && !string.IsNullOrWhiteSpace(d.DayLabel)
                && !string.IsNullOrWhiteSpace(d.TooltipText)));
            Assert.AreEqual(TodayLocal.AddDays(-6), q.DailyTrends[0].LocalDate);
            Assert.AreEqual(TodayLocal, q.DailyTrends[6].LocalDate);
            Assert.AreEqual(1, q.DailyTrends.Count(d => d.IsToday));
            Assert.IsTrue(q.DailyTrends[6].IsToday);
        }
    }

    [TestMethod]
    public async Task PeriodBoundaries_TodayLast7DaysAndAllTime_FilterAndClipCorrectly()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var notifications = new SqliteNotificationRepository(db);

        // 자정 걸친 세션 (어제 23:30 ~ 오늘 00:30 = 60분, 오늘 기준 30분)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-midnight", KstToUtc(-1, 23, 30), KstToUtc(0, 0, 30)));
        // 오늘 세션 (10:00 ~ 10:20 = 20분)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-today", KstToUtc(0, 10, 0), KstToUtc(0, 10, 20)));
        // 6일 전 세션 (최근 7일 포함: 30분)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-day-6", KstToUtc(-6, 9, 0), KstToUtc(-6, 9, 30)));
        // 7일 전 세션 (최근 7일 제외, 전체 포함: 40분)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-day-7", KstToUtc(-7, 9, 0), KstToUtc(-7, 9, 40)));
        // 30일 전 세션 (전체 포함: 50분)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-day-30", KstToUtc(-30, 14, 0), KstToUtc(-30, 14, 50)));

        await notifications.InsertAsync(MakeNotification("n-today", "Slack", "Alice", KstToUtc(0, 10, 5), false));
        await notifications.InsertAsync(MakeNotification("n-day-6", "Slack", "Alice", KstToUtc(-6, 9, 10), false));
        await notifications.InsertAsync(MakeNotification("n-day-7", "Slack", "Alice", KstToUtc(-7, 9, 10), false));
        await notifications.InsertAsync(MakeNotification("n-day-30", "Slack", "Alice", KstToUtc(-30, 14, 10), true, urgency: 5));

        var repo = new SqliteFocusInsightRepository(db);
        var nowUtc = KstToUtc(0, 15, 0);

        var today = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        Assert.AreEqual(50.0, today.TotalFocusDuration.TotalMinutes);
        Assert.AreEqual(2, today.FocusSessionCount);
        Assert.AreEqual(1, today.TotalNotifications);

        var last7 = await repo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        Assert.AreEqual(110.0, last7.TotalFocusDuration.TotalMinutes);
        Assert.AreEqual(3, last7.FocusSessionCount);
        Assert.AreEqual(2, last7.TotalNotifications);

        var allTime = await repo.ReadAsync(InsightPeriod.AllTime, nowUtc, timeZone: Korea);
        Assert.AreEqual(200.0, allTime.TotalFocusDuration.TotalMinutes);
        Assert.AreEqual(5, allTime.FocusSessionCount);
        Assert.AreEqual(4, allTime.TotalNotifications);
    }

    [TestMethod]
    public async Task OverlappingFocusSessionsAndActiveSession_NeverDoubleCount()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var windows = new SqliteWindowSessionRepository(db);

        // 겹치는 두 세션: 10:00~11:00, 10:30~11:30 -> 병합 90분
        await sessions.StartSessionAsync(new FocusSessionRecord("s-1", KstToUtc(0, 10, 0), KstToUtc(0, 11, 0)));
        await sessions.StartSessionAsync(new FocusSessionRecord("s-2", KstToUtc(0, 10, 30), KstToUtc(0, 11, 30)));

        // 비정상 종료 세션(마지막 창 기록 12:15까지 15분만 복구)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-interrupted", KstToUtc(0, 12, 0)));
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "Editor", KstToUtc(0, 12, 0), KstToUtc(0, 12, 15), 900, "s-interrupted"));

        // 창 기록도 없는 오래된 미종료 세션 -> 무시
        await sessions.StartSessionAsync(new FocusSessionRecord("s-stale", KstToUtc(0, 8, 0)));

        // 현재 활성 세션 (14:00 ~ now=14:25 -> 25분, 창 기록이 14:10까지 있더라도 1세션으로 합산)
        var active = new FocusSessionRecord("s-live", KstToUtc(0, 14, 0));
        await sessions.StartSessionAsync(active);
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "Editor", KstToUtc(0, 14, 0), KstToUtc(0, 14, 10), 600, "s-live"));

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 14, 25), activeSession: active, timeZone: Korea);

        Assert.AreEqual(130.0, snapshot.TotalFocusDuration.TotalMinutes);
        Assert.AreEqual(4, snapshot.FocusSessionCount);
        Assert.AreEqual(60.0, snapshot.HourlyBuckets[10].FocusDuration.TotalMinutes);
        Assert.AreEqual(30.0, snapshot.HourlyBuckets[11].FocusDuration.TotalMinutes);
        Assert.AreEqual(15.0, snapshot.HourlyBuckets[12].FocusDuration.TotalMinutes);
        Assert.AreEqual(25.0, snapshot.HourlyBuckets[14].FocusDuration.TotalMinutes);
    }

    [TestMethod]
    public async Task PassedNotificationsSplit_SeparatesUrgentRelevanceAndOther()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var notifications = new SqliteNotificationRepository(db);

        await notifications.InsertAsync(MakeNotification("n1", "KakaoTalk", "친구", KstToUtc(0, 10, 0), isPassed: false, urgency: 1, relevance: 1));
        await notifications.InsertAsync(MakeNotification("n2", "Slack", "팀장", KstToUtc(0, 10, 1), isPassed: true, urgency: 4, relevance: 3)); // 긴급 우선
        await notifications.InsertAsync(MakeNotification("n3", "PagerDuty", "Ops", KstToUtc(0, 10, 2), isPassed: true, urgency: 5, relevance: 1)); // 긴급
        await notifications.InsertAsync(MakeNotification("n4", "GitHub", "PR", KstToUtc(0, 10, 3), isPassed: true, urgency: 3, relevance: 3)); // 작업 관련
        await notifications.InsertAsync(MakeNotification("n5", "Jira", "Bot", KstToUtc(0, 10, 4), isPassed: true, urgency: 2, relevance: 4)); // 작업 관련 (구 1~5 스케일)
        await notifications.InsertAsync(MakeNotification("n6", "Mail", "HR", KstToUtc(0, 10, 5), isPassed: true, urgency: 2, relevance: 2)); // 기타 통과
        await notifications.InsertAsync(MakeNotification("n7", "Calendar", null, KstToUtc(0, 10, 6), isPassed: true, urgency: null, relevance: null)); // 기타 통과
        await notifications.InsertAsync(MakeNotification("n8", "Pending", null, KstToUtc(0, 10, 7), isPassed: null)); // 미분류 제외

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);

        Assert.AreEqual(7, snapshot.TotalNotifications);
        Assert.AreEqual(1, snapshot.BlockedCount);
        Assert.AreEqual(6, snapshot.PassedCount);
        Assert.AreEqual(2, snapshot.UrgentPassedCount);
        Assert.AreEqual(2, snapshot.RelevancePassedCount);
        Assert.AreEqual(2, snapshot.OtherPassedCount);
        Assert.AreEqual(14.3, snapshot.BlockRatePercent);
    }

    [TestMethod]
    public async Task HourlyBuckets_GoldenHourDistractionPeakBarHeightsAndTooltips()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var notifications = new SqliteNotificationRepository(db);

        await sessions.StartSessionAsync(new FocusSessionRecord("s1", KstToUtc(0, 10, 0), KstToUtc(0, 10, 15)));
        await sessions.StartSessionAsync(new FocusSessionRecord("s2", KstToUtc(0, 14, 0), KstToUtc(0, 14, 45)));

        // 14시: 차단 5건, 통과 1건 -> 6건
        for (int i = 0; i < 5; i++)
            await notifications.InsertAsync(MakeNotification($"n14b-{i}", "KakaoTalk", "단톡방", KstToUtc(0, 14, i), false));
        await notifications.InsertAsync(MakeNotification("n14p-0", "Slack", "PM", KstToUtc(0, 14, 10), true, urgency: 4));

        // 15시: 차단 8건, 통과 4건 -> 12건 (피크)
        for (int i = 0; i < 8; i++)
            await notifications.InsertAsync(MakeNotification($"n15b-{i}", "Slack", "Random", KstToUtc(0, 15, i), false));
        for (int i = 0; i < 4; i++)
            await notifications.InsertAsync(MakeNotification($"n15p-{i}", "Slack", "Lead", KstToUtc(0, 15, 20 + i), true, urgency: 4));

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 18, 0), timeZone: Korea);

        Assert.AreEqual(14, snapshot.GoldenHour);
        Assert.AreEqual(TimeSpan.FromMinutes(45), snapshot.GoldenHourDuration);
        Assert.AreEqual(15, snapshot.DistractionPeakHour);
        Assert.AreEqual(12, snapshot.DistractionPeakCount);

        var h14 = snapshot.HourlyBuckets[14];
        Assert.IsTrue(h14.IsGoldenHour);
        Assert.IsFalse(h14.IsDistractionPeak);
        Assert.AreEqual(88.0, h14.FocusBarHeight);
        Assert.AreEqual(44.0, h14.NotificationBarHeight);
        Assert.AreEqual("14시 · 집중 45분 · 차단 5건 · 통과 1건", h14.TooltipText);

        var h15 = snapshot.HourlyBuckets[15];
        Assert.IsFalse(h15.IsGoldenHour);
        Assert.IsTrue(h15.IsDistractionPeak);
        Assert.AreEqual(0.0, h15.FocusBarHeight);
        Assert.AreEqual(88.0, h15.NotificationBarHeight);

        var h10 = snapshot.HourlyBuckets[10];
        Assert.AreEqual(29.33, h10.FocusBarHeight, 0.01);
        Assert.AreEqual(0.0, h10.NotificationBarHeight);
    }

    [TestMethod]
    public async Task TopDistractors_GroupsByAppAndSender_FormatsLabelsAndReturnsTop5()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var notifications = new SqliteNotificationRepository(db);

        async Task AddBatch(string prefix, string app, string? sender, int blocked, int passed)
        {
            for (int i = 0; i < blocked; i++)
                await notifications.InsertAsync(MakeNotification($"{prefix}-b-{i}", app, sender, KstToUtc(0, 10, i), false));
            for (int i = 0; i < passed; i++)
                await notifications.InsertAsync(MakeNotification($"{prefix}-p-{i}", app, sender, KstToUtc(0, 11, i), true, urgency: 4));
        }

        await AddBatch("d1", "KakaoTalk", "김철수", blocked: 6, passed: 0);
        await AddBatch("d2", "Outlook", null, blocked: 4, passed: 2);        // sender null -> "Outlook"
        await AddBatch("d3", "Slack", "general", blocked: 4, passed: 1);     // blocked 동률, total 5 < 6
        await AddBatch("d4", "Discord", "   ", blocked: 3, passed: 0);       // whitespace sender -> "Discord"
        await AddBatch("d5", "Teams", "마케팅", blocked: 2, passed: 1);
        await AddBatch("d6", "Mail", "뉴스레터", blocked: 1, passed: 5);      // 6위 제외

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);

        Assert.AreEqual(5, snapshot.TopDistractors.Count);

        Assert.AreEqual("KakaoTalk · 김철수", snapshot.TopDistractors[0].DisplayLabel);
        Assert.AreEqual(6, snapshot.TopDistractors[0].BlockedCount);
        Assert.AreEqual(100.0, snapshot.TopDistractors[0].BlockRatePercent);

        Assert.AreEqual("Outlook", snapshot.TopDistractors[1].DisplayLabel);
        Assert.IsNull(snapshot.TopDistractors[1].Sender);
        Assert.AreEqual(6, snapshot.TopDistractors[1].TotalCount);
        Assert.AreEqual(66.7, snapshot.TopDistractors[1].BlockRatePercent);

        Assert.AreEqual("Slack · general", snapshot.TopDistractors[2].DisplayLabel);
        Assert.AreEqual(5, snapshot.TopDistractors[2].TotalCount);

        Assert.AreEqual("Discord", snapshot.TopDistractors[3].DisplayLabel);
        Assert.IsNull(snapshot.TopDistractors[3].Sender);

        Assert.AreEqual("Teams · 마케팅", snapshot.TopDistractors[4].DisplayLabel);
    }

    [TestMethod]
    public async Task TopWorkContexts_CrossJoinsWindowSessionsWithBoundarySkewAndTakesTop5()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var windows = new SqliteWindowSessionRepository(db);
        var notifications = new SqliteNotificationRepository(db);

        await sessions.StartSessionAsync(new FocusSessionRecord("s1", KstToUtc(0, 9, 0), KstToUtc(0, 12, 0)));

        // Code.exe: 두 구간 합산 35분 (09:00~09:20, 10:00~10:15)
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "Program.cs", KstToUtc(0, 9, 0), KstToUtc(0, 9, 20), 1200, "s1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "Tests.cs", KstToUtc(0, 10, 0), KstToUtc(0, 10, 15), 900, "s1"));
        // chrome.exe: 15분 (09:20~09:35)
        await windows.InsertAsync(new WindowSessionRecord(0, "chrome.exe", "Docs", KstToUtc(0, 9, 20), KstToUtc(0, 9, 35), 900, "s1"));
        // devenv.exe: 25분 (10:20~10:45)
        await windows.InsertAsync(new WindowSessionRecord(0, "devenv.exe", "Solution", KstToUtc(0, 10, 20), KstToUtc(0, 10, 45), 1500, "s1"));
        // figma.exe: 10분 (10:50~11:00)
        await windows.InsertAsync(new WindowSessionRecord(0, "figma.exe", "Mockup", KstToUtc(0, 10, 50), KstToUtc(0, 11, 0), 600, "s1"));
        // notion.exe: 12분 (11:05~11:17)
        await windows.InsertAsync(new WindowSessionRecord(0, "notion.exe", "PRD", KstToUtc(0, 11, 5), KstToUtc(0, 11, 17), 720, "s1"));
        // excel.exe: 5분 (11:20~11:25) -> 6위 제외
        await windows.InsertAsync(new WindowSessionRecord(0, "excel.exe", "Sheet", KstToUtc(0, 11, 20), KstToUtc(0, 11, 25), 300, "s1"));
        // 비집중 창(session_id = null) -> 제외
        await windows.InsertAsync(new WindowSessionRecord(0, "notepad.exe", "Memo", KstToUtc(0, 8, 0), KstToUtc(0, 8, 30), 1800, null));

        // Code.exe: 구간 내 2건 + 8초 경계 오차(10:15:08) 1건 = 3건 (차단 2, 통과 1)
        await notifications.InsertAsync(MakeNotification("w-c1", "KakaoTalk", "A", KstToUtc(0, 9, 5), false, sessionId: "s1"));
        await notifications.InsertAsync(MakeNotification("w-c2", "Slack", "B", KstToUtc(0, 10, 5), true, urgency: 4, sessionId: "s1"));
        await notifications.InsertAsync(MakeNotification("w-c3", "Slack", "C", KstToUtc(0, 10, 15, 8), false, sessionId: "s1"));
        // 15초 경계 초과(10:15:15) -> 매칭되지 않아야 함
        await notifications.InsertAsync(MakeNotification("w-miss", "Slack", "D", KstToUtc(0, 10, 15, 15), false, sessionId: "s1"));

        // devenv.exe: 2건 (집중 25분 -> chrome.exe와 알림 수 동률이지만 집중시간이 길어 상위)
        await notifications.InsertAsync(MakeNotification("w-d1", "Slack", "A", KstToUtc(0, 10, 25), false, sessionId: "s1"));
        await notifications.InsertAsync(MakeNotification("w-d2", "Slack", "B", KstToUtc(0, 10, 30), false, sessionId: "s1"));

        // chrome.exe: 2건 (집중 15분)
        await notifications.InsertAsync(MakeNotification("w-ch1", "Slack", "A", KstToUtc(0, 9, 25), false, sessionId: "s1"));
        await notifications.InsertAsync(MakeNotification("w-ch2", "Slack", "B", KstToUtc(0, 9, 30), true, relevance: 3, sessionId: "s1"));

        // notion.exe: 1건 (12분)
        await notifications.InsertAsync(MakeNotification("w-n1", "Slack", "A", KstToUtc(0, 11, 10), false, sessionId: "s1"));
        // figma.exe: 1건 (10분)
        await notifications.InsertAsync(MakeNotification("w-f1", "Slack", "A", KstToUtc(0, 10, 55), false, sessionId: "s1"));
        // excel.exe: 1건 (5분 -> 6위로 탈락)
        await notifications.InsertAsync(MakeNotification("w-e1", "Slack", "A", KstToUtc(0, 11, 22), false, sessionId: "s1"));

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);

        Assert.AreEqual(5, snapshot.TopWorkContexts.Count);

        Assert.AreEqual("Code.exe", snapshot.TopWorkContexts[0].ProcessName);
        Assert.AreEqual("Code", snapshot.TopWorkContexts[0].DisplayName);
        Assert.AreEqual(TimeSpan.FromMinutes(35), snapshot.TopWorkContexts[0].FocusDuration);
        Assert.AreEqual(3, snapshot.TopWorkContexts[0].TotalNotifications);
        Assert.AreEqual(2, snapshot.TopWorkContexts[0].BlockedCount);
        Assert.AreEqual(1, snapshot.TopWorkContexts[0].PassedCount);
        Assert.AreEqual(66.7, snapshot.TopWorkContexts[0].BlockRatePercent);

        Assert.AreEqual("devenv", snapshot.TopWorkContexts[1].DisplayName);
        Assert.AreEqual("chrome", snapshot.TopWorkContexts[2].DisplayName);
        Assert.AreEqual("notion", snapshot.TopWorkContexts[3].DisplayName);
        Assert.AreEqual("figma", snapshot.TopWorkContexts[4].DisplayName);
    }

    [TestMethod]
    public async Task DataFingerprint_ChangesOnNewRecordsOrDatabaseWipe_AndStaysStableWhenUnchanged()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var notifications = new SqliteNotificationRepository(db);
        var repo = new SqliteFocusInsightRepository(db);
        var nowUtc = KstToUtc(0, 15, 0);

        var empty1 = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        var empty2 = await repo.ReadAsync(InsightPeriod.Today, nowUtc.AddMinutes(5), timeZone: Korea);
        Assert.AreEqual(empty1.DataFingerprint, empty2.DataFingerprint);

        var empty7Days = await repo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        Assert.AreNotEqual(empty1.DataFingerprint, empty7Days.DataFingerprint);

        await sessions.StartSessionAsync(new FocusSessionRecord("s1", KstToUtc(0, 10, 0), KstToUtc(0, 10, 30)));
        var afterFocus = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        Assert.AreNotEqual(empty1.DataFingerprint, afterFocus.DataFingerprint);

        await notifications.InsertAsync(MakeNotification("n1", "Slack", "Kim", KstToUtc(0, 10, 10), false, sessionId: "s1"));
        var afterNotif1 = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        var afterNotif2 = await repo.ReadAsync(InsightPeriod.Today, nowUtc.AddMinutes(10), timeZone: Korea);
        Assert.AreNotEqual(afterFocus.DataFingerprint, afterNotif1.DataFingerprint);
        Assert.AreEqual(afterNotif1.DataFingerprint, afterNotif2.DataFingerprint);

        // DB 초기화(전체 삭제) 시 캐시 무효화 확인
        using (var conn = db.CreateOpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM notifications; DELETE FROM window_sessions; DELETE FROM focus_sessions;";
            await cmd.ExecuteNonQueryAsync();
        }

        var afterWipe = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        Assert.IsFalse(afterWipe.HasData);
        Assert.AreNotEqual(afterNotif1.DataFingerprint, afterWipe.DataFingerprint);
        Assert.AreEqual(empty1.DataFingerprint, afterWipe.DataFingerprint);
    }

    [TestMethod]
    public async Task Quality_FlowProtectionRate_Distinguishes20sStaySameAppContinuationAndImmediateSwitch()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var windows = new SqliteWindowSessionRepository(db);
        var notifications = new SqliteNotificationRepository(db);

        await sessions.StartSessionAsync(new FocusSessionRecord("s1", KstToUtc(0, 10, 0), KstToUtc(0, 11, 0)));

        // 창 1: 10:00:00 ~ 10:05:00 Code.exe
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "A.cs", KstToUtc(0, 10, 0, 0), KstToUtc(0, 10, 5, 0), 300, "s1"));
        // 창 2: 10:05:03 ~ 10:06:00 code.exe (같은 프로세스, 3초 이내 재개)
        await windows.InsertAsync(new WindowSessionRecord(0, "code.exe", "B.cs", KstToUtc(0, 10, 5, 3), KstToUtc(0, 10, 6, 0), 57, "s1"));
        // 창 3: 10:06:01 ~ 10:08:00 KakaoTalk.exe (다른 앱으로 즉시 전환)
        await windows.InsertAsync(new WindowSessionRecord(0, "KakaoTalk.exe", "Chat", KstToUtc(0, 10, 6, 1), KstToUtc(0, 10, 8, 0), 119, "s1"));
        // 창 4: 10:08:10 ~ 10:09:00 Code.exe (8초 공백 -> 같은 앱 연속 기준 5초 초과 확인용)
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "C.cs", KstToUtc(0, 10, 8, 10), KstToUtc(0, 10, 9, 0), 50, "s1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "D.cs", KstToUtc(0, 10, 9, 8), KstToUtc(0, 10, 10, 0), 52, "s1"));

        // 1) 보호됨 (20초 이상 유지: 10:04:40 -> 10:05:00 = 정확히 20초)
        await notifications.InsertAsync(MakeNotification("fp-1", "Slack", "A", KstToUtc(0, 10, 4, 40), isPassed: false, sessionId: "s1"));
        // 2) 보호됨 (남은 시간 10초 < 20초이지만 다음 창이 같은 앱 code.exe이며 3초 이내 시작)
        await notifications.InsertAsync(MakeNotification("fp-2", "Slack", "B", KstToUtc(0, 10, 4, 50), isPassed: false, sessionId: "s1"));
        // 3) 미보호 (남은 시간 5초 < 20초이고 다음 창이 KakaoTalk.exe로 앱 전환)
        await notifications.InsertAsync(MakeNotification("fp-3", "KakaoTalk", "C", KstToUtc(0, 10, 5, 55), isPassed: false, sessionId: "s1"));
        // 4) 미보호 (남은 시간 5초 < 20초이고 다음 같은 앱 창까지 공백 8초 > 5초)
        await notifications.InsertAsync(MakeNotification("fp-4", "Slack", "D", KstToUtc(0, 10, 8, 55), isPassed: false, sessionId: "s1"));
        // 제외 1: 통과된 알림 (eligible 아님)
        await notifications.InsertAsync(MakeNotification("fp-pass", "Slack", "E", KstToUtc(0, 10, 1, 0), isPassed: true, urgency: 5, sessionId: "s1"));
        // 제외 2: 경계 오차(5초) 매칭만 되는 알림 (직접 포함 아님 -> eligible 아님)
        await notifications.InsertAsync(MakeNotification("fp-skew", "Slack", "F", KstToUtc(0, 10, 10, 5), isPassed: false, sessionId: "s1"));

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);

        Assert.IsNotNull(snapshot.Quality);
        var q = snapshot.Quality!;
        Assert.AreEqual(4, q.FlowProtectionEligibleCount);
        Assert.AreEqual(2, q.FlowProtectedCount);
        Assert.AreEqual(50.0, q.FlowProtectionRatePercent);
    }

    [TestMethod]
    public async Task Quality_WorkPurityAndTopSelfDistraction_ClassifiesCategoriesAndExcludesSystemApps()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var windows = new SqliteWindowSessionRepository(db);

        await sessions.StartSessionAsync(new FocusSessionRecord("s1", KstToUtc(0, 9, 0), KstToUtc(0, 11, 0)));

        // Work: 60분 (Code.exe 40분 + Notion.exe 20분)
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "IDE", KstToUtc(0, 9, 0), KstToUtc(0, 9, 40), 2400, "s1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "Notion.exe", "Doc", KstToUtc(0, 9, 40), KstToUtc(0, 10, 0), 1200, "s1"));
        // Neutral: 30분 (Altong.Client.exe 15분 + explorer.exe 10분 + KakaoTalk.exe 5분) -> 시스템 앱 제외 시 KakaoTalk 5분
        await windows.InsertAsync(new WindowSessionRecord(0, "Altong.Client.exe", "Dashboard", KstToUtc(0, 10, 0), KstToUtc(0, 10, 15), 900, "s1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "explorer.exe", "Files", KstToUtc(0, 10, 15), KstToUtc(0, 10, 25), 600, "s1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "KakaoTalk.exe", "Chat", KstToUtc(0, 10, 25), KstToUtc(0, 10, 30), 300, "s1"));
        // Leisure: 10분 (LeagueClient.exe 10분 -> 비작업 최다 1위!)
        await windows.InsertAsync(new WindowSessionRecord(0, "LeagueClient.exe", "Game", KstToUtc(0, 10, 30), KstToUtc(0, 10, 40), 600, "s1"));

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);

        Assert.IsNotNull(snapshot.Quality);
        var q = snapshot.Quality!;
        Assert.AreEqual(TimeSpan.FromMinutes(60), q.WorkDuration);
        Assert.AreEqual(TimeSpan.FromMinutes(30), q.NeutralDuration);
        Assert.AreEqual(TimeSpan.FromMinutes(10), q.LeisureDuration);
        Assert.AreEqual(60.0, q.WorkPurityPercent);
        Assert.AreEqual(30.0, q.NeutralPercent);
        Assert.AreEqual(10.0, q.LeisurePercent);
        Assert.AreEqual("LeagueClient", q.TopSelfDistractionApp);
        Assert.AreEqual(TimeSpan.FromMinutes(10), q.TopSelfDistractionDuration);

        // 비작업 앱이 1분 미만 + Altong/explorer만 있는 경우 TopSelfDistractionApp은 null
        using var db2 = SqliteDatabase.CreateInMemory();
        db2.Initialize();
        var sessions2 = new SqliteFocusSessionRepository(db2);
        var windows2 = new SqliteWindowSessionRepository(db2);
        await sessions2.StartSessionAsync(new FocusSessionRecord("s2", KstToUtc(0, 9, 0), KstToUtc(0, 10, 0)));
        await windows2.InsertAsync(new WindowSessionRecord(0, "Code.exe", "IDE", KstToUtc(0, 9, 0), KstToUtc(0, 9, 30), 1800, "s2"));
        await windows2.InsertAsync(new WindowSessionRecord(0, "Altong.Client", "App", KstToUtc(0, 9, 30), KstToUtc(0, 9, 40), 600, "s2"));
        await windows2.InsertAsync(new WindowSessionRecord(0, "EXPLORER.EXE", "Shell", KstToUtc(0, 9, 40), KstToUtc(0, 9, 50), 600, "s2"));
        await windows2.InsertAsync(new WindowSessionRecord(0, "chrome.exe", "Brief", KstToUtc(0, 9, 50, 0), KstToUtc(0, 9, 50, 45), 45, "s2"));

        var repo2 = new SqliteFocusInsightRepository(db2);
        var snapshot2 = await repo2.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);
        Assert.IsNotNull(snapshot2.Quality);
        Assert.IsNull(snapshot2.Quality!.TopSelfDistractionApp);
        Assert.AreEqual(TimeSpan.Zero, snapshot2.Quality.TopSelfDistractionDuration);
    }

    [TestMethod]
    public async Task Quality_DeepFocusAndAverageSessionDuration_ComputesCorrectly()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);

        // 1) 15분 (< 25분)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-short", KstToUtc(0, 9, 0), KstToUtc(0, 9, 15)));
        // 2) 정확히 25분 (>= 25분 -> DeepFocus)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-edge", KstToUtc(0, 10, 0), KstToUtc(0, 10, 25)));
        // 3) 50분 (>= 25분 -> DeepFocus)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-deep", KstToUtc(0, 11, 0), KstToUtc(0, 11, 50)));

        var repo = new SqliteFocusInsightRepository(db);
        var snapshot = await repo.ReadAsync(InsightPeriod.Today, KstToUtc(0, 15, 0), timeZone: Korea);

        Assert.IsNotNull(snapshot.Quality);
        var q = snapshot.Quality!;
        Assert.AreEqual(3, snapshot.FocusSessionCount);
        Assert.AreEqual(2, q.DeepFocusSessionCount);
        Assert.AreEqual(TimeSpan.FromMinutes(30), q.AverageSessionDuration); // (15 + 25 + 50) / 3 = 30분
    }

    [TestMethod]
    public async Task Quality_StreakDailyTrendsAndPeriodChange_HandlesActiveYesterdayBrokenAndComparisons()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var repo = new SqliteFocusInsightRepository(db);
        var nowUtc = KstToUtc(0, 18, 0);

        // 2일 전: 40분, 1일 전(어제): 80분 -> 오늘 기록 없어도 어제부터 2일 연속 유지
        await sessions.StartSessionAsync(new FocusSessionRecord("s-d2", KstToUtc(-2, 10, 0), KstToUtc(-2, 10, 40)));
        await sessions.StartSessionAsync(new FocusSessionRecord("s-d1", KstToUtc(-1, 10, 0), KstToUtc(-1, 11, 20)));
        // 이전 7일 구간(-10일 전): 60분 -> 최근 7일(120분) vs 이전 7일(60분) = +100.0%
        await sessions.StartSessionAsync(new FocusSessionRecord("s-d10", KstToUtc(-10, 10, 0), KstToUtc(-10, 11, 0)));

        var todayBeforeStart = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        Assert.IsNotNull(todayBeforeStart.Quality);
        Assert.AreEqual(2, todayBeforeStart.Quality!.CurrentStreakDays);
        Assert.AreEqual(-100.0, todayBeforeStart.Quality.PeriodChangePercent); // 오늘 0분 vs 어제 80분

        var last7BeforeToday = await repo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        Assert.AreEqual(100.0, last7BeforeToday.Quality!.PeriodChangePercent); // 120분 vs 60분 = +100.0%

        var allTime = await repo.ReadAsync(InsightPeriod.AllTime, nowUtc, timeZone: Korea);
        Assert.IsNull(allTime.Quality!.PeriodChangePercent);

        // 오늘 120분(2시간) 추가 -> 연속 3일, 오늘 vs 어제(80분) = +50.0%
        await sessions.StartSessionAsync(new FocusSessionRecord("s-d0", KstToUtc(0, 14, 0), KstToUtc(0, 16, 0)));

        var todayAfterStart = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        var q = todayAfterStart.Quality!;
        Assert.AreEqual(3, q.CurrentStreakDays);
        Assert.AreEqual(50.0, q.PeriodChangePercent); // (120 - 80) / 80 * 100 = 50.0%

        // 7일 추이 검증 (2026-11-20 금 ~ 2026-11-26 목)
        Assert.AreEqual(7, q.DailyTrends.Count);
        Assert.AreEqual(new DateTime(2026, 11, 20), q.DailyTrends[0].LocalDate);
        Assert.AreEqual("금", q.DailyTrends[0].DayLabel);
        Assert.IsFalse(q.DailyTrends[0].IsToday);
        Assert.AreEqual(0.0, q.DailyTrends[0].BarHeight);

        // -2일(화): 40분 / max 120분 * 72 = 24.0
        Assert.AreEqual(new DateTime(2026, 11, 24), q.DailyTrends[4].LocalDate);
        Assert.AreEqual("화", q.DailyTrends[4].DayLabel);
        Assert.AreEqual(TimeSpan.FromMinutes(40), q.DailyTrends[4].FocusDuration);
        Assert.AreEqual(24.0, q.DailyTrends[4].BarHeight);

        // -1일(수): 80분 / max 120분 * 72 = 48.0
        Assert.AreEqual(new DateTime(2026, 11, 25), q.DailyTrends[5].LocalDate);
        Assert.AreEqual("수", q.DailyTrends[5].DayLabel);
        Assert.AreEqual(TimeSpan.FromMinutes(80), q.DailyTrends[5].FocusDuration);
        Assert.AreEqual(48.0, q.DailyTrends[5].BarHeight);
        Assert.AreEqual("11/25 (수) · 집중 1시간 20분", q.DailyTrends[5].TooltipText);

        // 오늘(목): 120분 -> max 72.0
        Assert.AreEqual(new DateTime(2026, 11, 26), q.DailyTrends[6].LocalDate);
        Assert.AreEqual("목", q.DailyTrends[6].DayLabel);
        Assert.IsTrue(q.DailyTrends[6].IsToday);
        Assert.AreEqual(72.0, q.DailyTrends[6].BarHeight);
        Assert.AreEqual("11/26 (목) · 집중 2시간", q.DailyTrends[6].TooltipText);

        // 끊긴 스트릭 + 이전 기간 0분 검증 (2일 전에만 기록 존재 -> streak 0, PeriodChangePercent null)
        using var dbBroken = SqliteDatabase.CreateInMemory();
        dbBroken.Initialize();
        var brokenSessions = new SqliteFocusSessionRepository(dbBroken);
        await brokenSessions.StartSessionAsync(new FocusSessionRecord("s-old", KstToUtc(-2, 10, 0), KstToUtc(-2, 10, 30)));
        var brokenRepo = new SqliteFocusInsightRepository(dbBroken);

        var brokenToday = await brokenRepo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        Assert.AreEqual(0, brokenToday.Quality!.CurrentStreakDays);
        Assert.AreEqual(1, brokenToday.Quality.BestStreakDays);
        Assert.IsFalse(brokenToday.Quality.IsTodayFocused);
        Assert.IsNull(brokenToday.Quality.PeriodChangePercent);

        var brokenLast7 = await brokenRepo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        Assert.IsNull(brokenLast7.Quality!.PeriodChangePercent);
    }

    [TestMethod]
    public async Task Quality_BestStreakTodayFlagAndHeatmap70Days_ComputesIntensityAndOrder()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var repo = new SqliteFocusInsightRepository(db);
        var nowUtc = KstToUtc(0, 18, 0);

        // 과거 4일 연속(-20..-17일): BestStreak = 4
        await sessions.StartSessionAsync(new FocusSessionRecord("h-20", KstToUtc(-20, 10, 0), KstToUtc(-20, 10, 15))); // 15분 -> level 1
        await sessions.StartSessionAsync(new FocusSessionRecord("h-19", KstToUtc(-19, 10, 0), KstToUtc(-19, 10, 45))); // 45분 -> level 2
        await sessions.StartSessionAsync(new FocusSessionRecord("h-18", KstToUtc(-18, 10, 0), KstToUtc(-18, 11, 30))); // 90분 -> level 3
        await sessions.StartSessionAsync(new FocusSessionRecord("h-17", KstToUtc(-17, 10, 0), KstToUtc(-17, 12, 10))); // 130분 -> level 4

        // 최근 2일 연속(-2, -1일): 어제까지 CurrentStreak = 2, IsTodayFocused = false
        await sessions.StartSessionAsync(new FocusSessionRecord("h-2", KstToUtc(-2, 14, 0), KstToUtc(-2, 14, 20)));
        await sessions.StartSessionAsync(new FocusSessionRecord("h-1", KstToUtc(-1, 14, 0), KstToUtc(-1, 14, 50)));

        var beforeToday = await repo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        var qBefore = beforeToday.Quality!;
        Assert.AreEqual(2, qBefore.CurrentStreakDays);
        Assert.AreEqual(4, qBefore.BestStreakDays);
        Assert.IsFalse(qBefore.IsTodayFocused);
        Assert.IsNotNull(qBefore.HeatmapDays);
        Assert.AreEqual(140, qBefore.HeatmapDays!.Count);

        // 시간 순 오름차순 + 오늘 이후(금·토, TodayLocal=2026-11-26 목) 미래 셀 확인
        for (int i = 1; i < qBefore.HeatmapDays.Count; i++)
            Assert.AreEqual(qBefore.HeatmapDays[i - 1].LocalDate.AddDays(1), qBefore.HeatmapDays[i].LocalDate);

        var futureCells = qBefore.HeatmapDays.Where(d => d.LocalDate > TodayLocal).ToArray();
        Assert.AreEqual(2, futureCells.Length);
        Assert.IsTrue(futureCells.All(d =>
            d.IsFuture && d.IntensityLevel == 0 && d.TooltipText == string.Empty &&
            d.SessionCount == 0 && d.BlockedCount == 0 && d.WorkPurityPercent is null));

        var byDate = qBefore.HeatmapDays.ToDictionary(d => d.LocalDate);
        Assert.AreEqual(1, byDate[TodayLocal.AddDays(-20)].IntensityLevel);
        Assert.AreEqual(2, byDate[TodayLocal.AddDays(-19)].IntensityLevel);
        Assert.AreEqual(3, byDate[TodayLocal.AddDays(-18)].IntensityLevel);
        Assert.AreEqual(4, byDate[TodayLocal.AddDays(-17)].IntensityLevel);
        Assert.IsTrue(byDate[TodayLocal].IsToday);
        Assert.AreEqual(0, byDate[TodayLocal].IntensityLevel);
        StringAssert.Contains(byDate[TodayLocal].TooltipText, "기록 없음");

        // 오늘 2시간 집중 추가 -> CurrentStreak = 3, IsTodayFocused = true, 오늘 level = 4
        await sessions.StartSessionAsync(new FocusSessionRecord("h-0", KstToUtc(0, 15, 0), KstToUtc(0, 17, 0)));
        var afterToday = await repo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        var qAfter = afterToday.Quality!;
        Assert.AreEqual(3, qAfter.CurrentStreakDays);
        Assert.AreEqual(4, qAfter.BestStreakDays);
        Assert.IsTrue(qAfter.IsTodayFocused);
        var todayCell = qAfter.HeatmapDays!.Single(d => d.IsToday);
        Assert.AreEqual(4, todayCell.IntensityLevel);
        Assert.AreEqual(1, todayCell.SessionCount);
        StringAssert.Contains(todayCell.TooltipText, "2시간");
    }

    [TestMethod]
    public async Task Quality_HeatmapDailyDetailsAndSelfDistractionChange_ComputesAccurately()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var notifications = new SqliteNotificationRepository(db);
        var windows = new SqliteWindowSessionRepository(db);
        var repo = new SqliteFocusInsightRepository(db);
        var nowUtc = KstToUtc(0, 18, 0);

        // -10일 (이전 7일 구간): 자발적 이탈 40분
        await sessions.StartSessionAsync(new FocusSessionRecord("s-prev", KstToUtc(-10, 10, 0), KstToUtc(-10, 11, 0)));
        await windows.InsertAsync(new WindowSessionRecord(0, "KakaoTalk.exe", "Chat", KstToUtc(-10, 10, 0), KstToUtc(-10, 10, 40), 2400, "s-prev"));

        // -1일 (어제): 집중 1시간, 차단 2건, 작업(Code) 40분 + 자발적 이탈(KakaoTalk) 20분
        await sessions.StartSessionAsync(new FocusSessionRecord("s-y1", KstToUtc(-1, 14, 0), KstToUtc(-1, 15, 0)));
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "IDE", KstToUtc(-1, 14, 0), KstToUtc(-1, 14, 40), 2400, "s-y1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "KakaoTalk.exe", "Chat", KstToUtc(-1, 14, 40), KstToUtc(-1, 15, 0), 1200, "s-y1"));
        await notifications.InsertAsync(MakeNotification("nb1", "KakaoTalk.exe", "민수", KstToUtc(-1, 14, 10), isPassed: false, sessionId: "s-y1"));
        await notifications.InsertAsync(MakeNotification("nb2", "Slack.exe", "팀", KstToUtc(-1, 14, 30), isPassed: false, sessionId: "s-y1"));
        await notifications.InsertAsync(MakeNotification("np1", "Code.exe", null, KstToUtc(-1, 14, 50), isPassed: true, urgency: 5, sessionId: "s-y1"));

        // 오늘: 2개 세션, 작업(Code) 50분 + 자발적 이탈(KakaoTalk) 10분 (어제 20분 -> 오늘 10분 = -50%, 지난주 40분 -> 이번주 30분 = -25%)
        await sessions.StartSessionAsync(new FocusSessionRecord("s-t1", KstToUtc(0, 9, 0), KstToUtc(0, 9, 30)));
        await sessions.StartSessionAsync(new FocusSessionRecord("s-t2", KstToUtc(0, 10, 0), KstToUtc(0, 10, 30)));
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "IDE", KstToUtc(0, 9, 0), KstToUtc(0, 9, 30), 1800, "s-t1"));
        await windows.InsertAsync(new WindowSessionRecord(0, "Code.exe", "IDE", KstToUtc(0, 10, 0), KstToUtc(0, 10, 20), 1200, "s-t2"));
        await windows.InsertAsync(new WindowSessionRecord(0, "KakaoTalk.exe", "Chat", KstToUtc(0, 10, 20), KstToUtc(0, 10, 30), 600, "s-t2"));
        await notifications.InsertAsync(MakeNotification("nb3", "KakaoTalk.exe", "민수", KstToUtc(0, 10, 5), isPassed: false, sessionId: "s-t2"));

        var todaySnap = await repo.ReadAsync(InsightPeriod.Today, nowUtc, timeZone: Korea);
        Assert.AreEqual(-50.0, todaySnap.Quality!.SelfDistractionChangePercent);

        var weekSnap = await repo.ReadAsync(InsightPeriod.Last7Days, nowUtc, timeZone: Korea);
        Assert.AreEqual(-25.0, weekSnap.Quality!.SelfDistractionChangePercent);

        var allSnap = await repo.ReadAsync(InsightPeriod.AllTime, nowUtc, timeZone: Korea);
        Assert.IsNull(allSnap.Quality!.SelfDistractionChangePercent);

        // Today 조회 시에도 20주(140일) 히트맵에는 어제/오늘의 세션·차단·순도가 모두 채워져야 함
        var byDate = todaySnap.Quality.HeatmapDays!.ToDictionary(d => d.LocalDate);
        var yesterdayCell = byDate[TodayLocal.AddDays(-1)];
        Assert.AreEqual(1, yesterdayCell.SessionCount);
        Assert.AreEqual(2, yesterdayCell.BlockedCount);
        Assert.AreEqual(66.7, yesterdayCell.WorkPurityPercent);

        var todayCell = byDate[TodayLocal];
        Assert.AreEqual(2, todayCell.SessionCount);
        Assert.AreEqual(1, todayCell.BlockedCount);
        Assert.AreEqual(83.3, todayCell.WorkPurityPercent);
    }
}

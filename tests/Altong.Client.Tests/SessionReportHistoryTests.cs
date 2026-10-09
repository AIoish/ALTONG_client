using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SessionReportHistoryTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    private static SessionResultsService Results(IAltongDatabase db) => new(db, new SqliteFocusSessionRepository(db));

    private static async Task<SessionResult> CompleteAsync(SessionResultsService results, DateTime start)
    {
        results.Begin(start, 0);
        return (await results.CompleteFocusSessionAsync(start.AddMinutes(5)))!;
    }

    [TestMethod]
    public async Task NewReport_RequiresExplicitSave_AndUnsavedReportsDoNotReturnOnInitialize()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = Results(db);
        var preview = await CompleteAsync(results, Start);
        Assert.IsNotNull(preview.SessionId);
        Assert.AreEqual(0, (await results.Reports.GetDayPageAsync(Start, timeZone: TimeZoneInfo.Utc)).Count);
        db.Initialize();
        Assert.IsNull(await Results(db).OpenReportAsync(preview.SessionId!));
        await results.Reports.SaveAsync(preview);
        Assert.AreEqual(1, (await results.Reports.GetDayPageAsync(Start, timeZone: TimeZoneInfo.Utc)).Count);
    }

    [TestMethod]
    public async Task Snapshot_PreservesExactSummaryDraftsAndApps_AfterOriginalDataChanges()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = Results(db);
        results.Begin(Start, 0);
        string id = results.CurrentSessionId!;
        var notifications = new SqliteNotificationRepository(db);
        await notifications.InsertAsync(new("snapshot-notification", "Messenger", "동료", "확인 요청", "가상 내용", Start.AddSeconds(1), false, SessionId: id));
        await new SqliteWindowSessionRepository(db).InsertAsync(new(0, "Editor", "가상 문서", Start, Start.AddMinutes(1), 60, id));
        var preview = (await results.CompleteFocusSessionAsync(Start.AddMinutes(5)))! with
        {
            StoredNotificationSummary = "종료 시 확정된 요약",
            StoredNotificationHighlights = new[] { "확정된 주요 알림" },
            StoredQuickReplies = new[] { new QuickReplyDraft("Messenger", "동료", "확인 요청", "종료 시 확정된 답변") }
        };
        await results.Reports.SaveAsync(preview);
        await notifications.UpdateFilterResultAsync("snapshot-notification", true, 5, 5, "changed", "changed");
        await notifications.InsertAsync(new("late-notification", "Messenger", null, "늦게 저장", "", Start.AddSeconds(2), false, SessionId: id));
        await new SqliteWindowSessionRepository(db).InsertAsync(new(0, "Browser", "", Start.AddMinutes(1), Start.AddMinutes(2), 60, id));

        var restored = (await Results(db).OpenReportAsync(id))!;
        Assert.AreEqual("종료 시 확정된 요약", restored.NotificationSummary);
        Assert.AreEqual("확정된 주요 알림", restored.NotificationHighlights.Single());
        Assert.AreEqual("종료 시 확정된 답변", restored.QuickReplies.Single().Text);
        Assert.AreEqual(1, restored.BlockedCount);
        Assert.AreEqual(1, restored.NotificationCount);
        Assert.AreEqual(60d, restored.Apps.Single().Seconds);
        var entry = (await results.Reports.GetDayPageAsync(Start, timeZone: TimeZoneInfo.Utc)).Single();
        Assert.AreEqual(1, entry.NotificationCount);
        Assert.AreEqual(1, entry.BlockedCount);
        Assert.AreEqual(1, entry.ApplicationCount);
        await results.Reports.SaveAsync(preview with { StoredNotificationSummary = "overwrite attempt" });
        Assert.AreEqual("종료 시 확정된 요약", (await results.OpenReportAsync(id))!.NotificationSummary);
    }

    [TestMethod]
    public async Task SameDayReports_PaginateIndependently_AndOtherDatesAreEmpty()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = Results(db);
        for (int i = 0; i < 65; i++)
            await results.Reports.SaveAsync(await CompleteAsync(results, Start.AddMinutes(i * 6)));
        var first = await results.Reports.GetDayPageAsync(Start, timeZone: TimeZoneInfo.Utc);
        var second = await results.Reports.GetDayPageAsync(Start, first.Count, TimeZoneInfo.Utc);
        var third = await results.Reports.GetDayPageAsync(Start, first.Count + second.Count, TimeZoneInfo.Utc);
        Assert.AreEqual(30, first.Count);
        Assert.AreEqual(30, second.Count);
        Assert.AreEqual(5, third.Count);
        Assert.AreEqual(65, first.Concat(second).Concat(third).Select(e => e.SessionId).Distinct().Count());
        Assert.IsTrue(first[0].EndedAt > second[0].EndedAt);
        Assert.AreEqual(0, (await results.Reports.GetDayPageAsync(Start.AddDays(-1), timeZone: TimeZoneInfo.Utc)).Count);
        Assert.AreEqual(65, (await results.Reports.GetMonthCountsAsync(Start, TimeZoneInfo.Utc))[Start.Date]);
    }

    [TestMethod]
    public async Task CrossMidnightReport_AppearsOnlyOnLocalEndDate_AndExactMidnightBelongsToNextDay()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = Results(db);
        var before = new DateTime(2026, 10, 1, 14, 58, 0, DateTimeKind.Utc); // Local 23:58.
        var preview = await CompleteAsync(results, before); // Local next day 00:03.
        await results.Reports.SaveAsync(preview);
        results.Begin(before.AddMinutes(-1), 0);
        var exact = (await results.CompleteFocusSessionAsync(new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc)))!;
        await results.Reports.SaveAsync(exact);
        Assert.AreEqual(0, (await results.Reports.GetDayPageAsync(new DateTime(2026, 10, 1), timeZone: Korea)).Count);
        Assert.AreEqual(2, (await results.Reports.GetDayPageAsync(new DateTime(2026, 10, 2), timeZone: Korea)).Count);
        Assert.AreEqual(2, (await results.Reports.GetMonthCountsAsync(Start, Korea))[new DateTime(2026, 10, 2)]);
        Assert.AreEqual(2, (await results.Reports.GetDayPageAsync(new DateTime(2026, 10, 1), timeZone: TimeZoneInfo.Utc)).Count);
    }

    [TestMethod]
    public void LocalDayRange_UsesBothBoundariesAcrossDaylightSaving()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var spring = SqliteSessionReportRepository.LocalRange(new DateTime(2026, 3, 8), new DateTime(2026, 3, 9), zone);
        var autumn = SqliteSessionReportRepository.LocalRange(new DateTime(2026, 11, 1), new DateTime(2026, 11, 2), zone);
        Assert.AreEqual(TimeSpan.FromHours(23), spring.ToUtc - spring.FromUtc);
        Assert.AreEqual(TimeSpan.FromHours(25), autumn.ToUtc - autumn.FromUtc);
    }

    [TestMethod]
    public async Task FileDatabase_ReopenPreservesSavedReports_DeletionPreservesOriginals_AndDoesNotResurrect()
    {
        string path = Path.Combine(Path.GetTempPath(), "altong-report-test-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        string savedId;
        string unsavedId;
        try
        {
            using (var db = new SqliteDatabase(connectionString))
            {
                db.Initialize();
                var results = Results(db);
                results.Begin(Start, 0);
                savedId = results.CurrentSessionId!;
                await new SqliteNotificationRepository(db).InsertAsync(new("keep-notification", "Messenger", null, "가상 알림", "", Start.AddSeconds(1), false, SessionId: savedId));
                await new SqliteWindowSessionRepository(db).InsertAsync(new(0, "Editor", "", Start, Start.AddMinutes(1), 60, savedId));
                await results.Reports.SaveAsync((await results.CompleteFocusSessionAsync(Start.AddMinutes(5)))!);
                unsavedId = (await CompleteAsync(results, Start.AddMinutes(10))).SessionId!;
            }
            SqliteConnection.ClearAllPools();
            using (var reopened = new SqliteDatabase(connectionString))
            {
                reopened.Initialize();
                var results = Results(reopened);
                Assert.IsNotNull(await results.OpenReportAsync(savedId));
                Assert.IsNull(await results.OpenReportAsync(unsavedId));
                await results.Reports.DeleteAsync(savedId);
                Assert.IsNotNull(await new SqliteFocusSessionRepository(reopened).GetByIdAsync(savedId));
                Assert.IsNotNull(await new SqliteNotificationRepository(reopened).GetByIdAsync("keep-notification"));
                Assert.AreEqual(1, (await new SqliteWindowSessionRepository(reopened).GetSessionsByDateRangeAsync(Start, Start.AddMinutes(5))).Count);
                reopened.Initialize();
                Assert.IsNull(await results.OpenReportAsync(savedId));
                Assert.AreEqual(0, (await results.Reports.GetMonthCountsAsync(Start, TimeZoneInfo.Utc)).Count);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [TestMethod]
    public async Task Upgrade_IndexesOnlyEndedCompletedSessionsOnce_AndLegacyDeletionIsPermanent()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        // Model the old schema before first deployment, with the report table absent.
        using (var connection = db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE focus_session_reports;";
            command.ExecuteNonQuery();
        }
        var sessions = new SqliteFocusSessionRepository(db);
        await sessions.StartSessionAsync(new("old-complete", Start, Start.AddMinutes(5), IsCompleted: true));
        await sessions.StartSessionAsync(new("old-open", Start));
        await sessions.StartSessionAsync(new("old-incomplete", Start, Start.AddMinutes(5)));
        await new SqliteNotificationRepository(db).InsertAsync(new("legacy-notification", "Messenger", null, "과거 알림", "", Start.AddSeconds(1), false, SessionId: "old-complete"));
        await new SqliteWindowSessionRepository(db).InsertAsync(new(0, "Editor", "", Start, Start.AddMinutes(1), 60, "old-complete"));
        // Unrelated records sharing the time range must not leak into this report.
        await new SqliteNotificationRepository(db).InsertAsync(new("unrelated", "Other", null, "", "", Start.AddSeconds(2), false));
        db.Initialize();
        var results = Results(db);
        var entry = (await results.Reports.GetDayPageAsync(Start, timeZone: TimeZoneInfo.Utc)).Single();
        Assert.IsFalse(entry.IsSaved);
        Assert.AreEqual("old-complete", entry.SessionId);
        var restored = (await results.OpenReportAsync(entry.SessionId))!;
        StringAssert.Contains(restored.ReportSource, "재구성");
        Assert.AreEqual(1, restored.NotificationCount);
        Assert.AreEqual(1, restored.ApplicationCount);
        await results.Reports.SaveAsync(restored);
        Assert.AreEqual("저장된 리포트", (await results.OpenReportAsync(entry.SessionId))!.ReportSource);
        await results.Reports.DeleteAsync(entry.SessionId);
        db.Initialize();
        Assert.IsNull(await results.OpenReportAsync(entry.SessionId));
        Assert.AreEqual(0, (await results.Reports.GetMonthCountsAsync(Start, TimeZoneInfo.Utc)).Count);
    }

    [TestMethod]
    public async Task ReportCollection_WaitsForPendingNotificationWrite()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = Results(db);
        results.Begin(Start, 0);
        var id = results.CurrentSessionId!;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var report = results.CompleteFocusSessionAsync(Start.AddMinutes(5), notificationWrites: pending.Task);
        Assert.IsFalse(report.IsCompleted);
        await new SqliteNotificationRepository(db).InsertAsync(new("delayed", "Messenger", null, "처리 지연", "", Start.AddSeconds(1), false, SessionId: id));
        pending.SetResult();
        Assert.AreEqual(1, (await report)!.NotificationCount);
    }
}

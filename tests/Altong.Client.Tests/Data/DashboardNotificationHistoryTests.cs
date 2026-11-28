using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class DashboardNotificationHistoryTests
{
    private static readonly DateTime From = new(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc);
    private static NotificationRecord Record(string id, DateTime at, bool? passed = false, string session = "session-a") =>
        new(id, "Messenger", "가상 동료", "확인 요청", "가상 알림 내용", at, passed, 3, 2, "업무", "가상 판정", session);

    [TestMethod]
    public async Task History_IncludesAllDaysAndSessions_InNewestOrder_ExcludesUnclassifiedRecords()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var raw = new SqliteNotificationRepository(db);
        await raw.InsertAsync(Record("old", From.AddDays(-40)));
        await raw.InsertAsync(Record("immediately-before", From.AddTicks(-1)));
        await raw.InsertAsync(Record("first", From));
        await raw.InsertAsync(Record("second", From.AddHours(8), true, "session-b"));
        await raw.InsertAsync(Record("unclassified", From.AddHours(9), null));
        await raw.InsertAsync(Record("next-day", From.AddDays(1)));
        await raw.InsertAsync(Record("last", From.AddDays(1).AddTicks(-1)));
        var page = await new SqliteDashboardNotificationRepository(db).ReadAsync();
        var rows = page.Records;
        CollectionAssert.AreEqual(new[] { "next-day", "last", "second", "first", "immediately-before", "old" }, rows.Select(row => row.Id).ToArray());
        Assert.AreEqual("session-b", rows[2].SessionId);
        Assert.AreEqual(2, rows[2].RelevanceScore);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public async Task Paging_BoundsBothSections_AndRetainsSelectedRowsAfterNewArrivals()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var raw = new SqliteNotificationRepository(db);
        for (int i = 0; i < 1200; i++)
        {
            await raw.InsertAsync(Record("blocked-" + i, From.AddMinutes(i)));
            await raw.InsertAsync(Record("passed-" + i, From.AddMinutes(i), true));
        }
        var history = new SqliteDashboardNotificationRepository(db);
        Assert.AreEqual(100, (await history.ReadAsync()).Records.Count,
            "수천 개가 쌓여도 최초 조회는 차단·통과 각각 최신 50개를 표시합니다.");
        var first = await history.ReadAsync(3);
        Assert.AreEqual(6, first.Records.Count);
        Assert.IsTrue(first.HasMoreBlocked);
        Assert.IsTrue(first.HasMorePassed);
        CollectionAssert.AreEqual(new[] { "blocked-1199", "blocked-1198", "blocked-1197" },
            first.Records.Where(row => row.IsPassed == false).Select(row => row.Id).ToArray());
        await raw.InsertAsync(Record("new-arrival", From.AddMinutes(1200)));
        var refreshed = await history.ReadAsync(3, ["blocked-1197", "unknown"]);
        Assert.AreEqual(7, refreshed.Records.Count);
        Assert.IsTrue(refreshed.Records.Any(row => row.Id == "blocked-1197"));
        Assert.AreEqual("new-arrival", refreshed.Records[0].Id);
        await history.DismissAsync(["blocked-1197"]);
        Assert.IsFalse((await history.ReadAsync(3, ["blocked-1197"])).Records.Any(row => row.Id == "blocked-1197"));
        var expanded = await history.ReadAsync(2000);
        Assert.AreEqual(2400, expanded.Records.Count);
        Assert.IsFalse(expanded.HasMore);
    }

    [TestMethod]
    public async Task Dismiss_IsIdempotent_SkipsUnknownIds_AndKeepsAllOriginalFields()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var raw = new SqliteNotificationRepository(db);
        var original = Record("blocked", From);
        await raw.InsertAsync(original);
        await raw.InsertAsync(Record("passed", From.AddMinutes(1), true));
        await raw.InsertAsync(Record("unclassified", From, null));
        var history = new SqliteDashboardNotificationRepository(db);
        await history.DismissAsync(["blocked", "blocked", "unknown", "", "unclassified"]);
        await history.DismissAsync(["blocked"]);
        Assert.AreEqual("passed", (await history.ReadAsync()).Records.Single().Id);
        var restored = await raw.GetByIdAsync("blocked");
        Assert.AreEqual(original, restored! with { CreatedAt = original.CreatedAt });
        using var connection = db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dashboard_notification_dismissals;";
        Assert.AreEqual(1L, command.ExecuteScalar());
    }

    [TestMethod]
    public async Task BatchDismiss_RollsBackCompletelyOnFailure()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var raw = new SqliteNotificationRepository(db);
        await raw.InsertAsync(Record("one", From));
        await raw.InsertAsync(Record("two", From, true));
        using (var connection = db.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fake_dismiss_failure BEFORE INSERT ON dashboard_notification_dismissals
                WHEN NEW.notification_id = 'two' BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        var history = new SqliteDashboardNotificationRepository(db);
        await Assert.ThrowsExceptionAsync<SqliteException>(() => history.DismissAsync(["one", "two"]));
        Assert.AreEqual(2, (await history.ReadAsync()).Records.Count);
        Assert.IsNotNull(await raw.GetByIdAsync("one"));
        Assert.IsNotNull(await raw.GetByIdAsync("two"));
    }

    [TestMethod]
    public async Task Dismiss_PersistsAcrossDatabaseReopen_AndRepeatedInitialization()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "altong-notification-history-" + Guid.NewGuid().ToString("N") + ".db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            using (var db = new SqliteDatabase(connectionString))
            {
                db.Initialize();
                var raw = new SqliteNotificationRepository(db);
                await raw.InsertAsync(Record("blocked", From));
                await raw.InsertAsync(Record("passed", From, true));
                await new SqliteDashboardNotificationRepository(db).DismissAsync(["blocked", "passed"]);
            }
            using (var reopened = new SqliteDatabase(connectionString))
            {
                reopened.Initialize();
                reopened.Initialize();
                Assert.AreEqual(0, (await new SqliteDashboardNotificationRepository(reopened).ReadAsync()).Records.Count);
                Assert.IsNotNull(await new SqliteNotificationRepository(reopened).GetByIdAsync("blocked"));
                Assert.IsNotNull(await new SqliteNotificationRepository(reopened).GetByIdAsync("passed"));
            }
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (System.IO.File.Exists(path + suffix)) System.IO.File.Delete(path + suffix);
        }
    }

    [TestMethod]
    public async Task DashboardRemoval_PreservesSnapshotAndReportReconstruction()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = new SessionResultsService(db, new SqliteFocusSessionRepository(db));
        results.Begin(From, 0);
        string session = results.CurrentSessionId!;
        var raw = new SqliteNotificationRepository(db);
        await raw.InsertAsync(Record("blocked", From.AddSeconds(1), false, session));
        await raw.InsertAsync(Record("passed", From.AddSeconds(2), true, session));
        var preview = (await results.CompleteFocusSessionAsync(From.AddMinutes(5)))!;
        await results.Reports.SaveAsync(preview);
        await results.DashboardNotifications.DismissAsync(["blocked", "passed"]);
        Assert.AreEqual(0, (await results.DashboardNotifications.ReadAsync()).Records.Count);
        Assert.AreEqual(2, (await results.ReadNotificationsAsync(From, From.AddMinutes(5), session)).Count);
        var report = (await new SessionResultsService(db, new SqliteFocusSessionRepository(db)).OpenReportAsync(session))!;
        Assert.AreEqual(preview.NotificationSummary, report.NotificationSummary);
        Assert.AreEqual(2, report.NotificationCount);
        Assert.AreEqual(1, report.BlockedCount);
        Assert.AreEqual(preview.QuickReplies.Single().Text, report.QuickReplies.Single().Text);
    }
}

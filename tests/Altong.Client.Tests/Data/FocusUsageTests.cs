using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class FocusUsageTests
{
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
    private static readonly DateTime Day = new(2026, 10, 4);
    private static DateTime At(int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(hour).AddMinutes(minute), Korea);
    private static FocusSessionRecord Session(string id, int fromHour, int fromMinute, int toHour, int toMinute) =>
        new(id, At(fromHour, fromMinute), At(toHour, toMinute));

    [TestMethod]
    public void MultipleSessions_SplitAtLocalHours_KeepTimeBetweenSessionsEmpty()
    {
        var day = SqliteFocusUsageRepository.Aggregate(Day, At(23),
            [Session("a", 19, 10, 19, 40), Session("b", 19, 50, 20, 15)], timeZone: Korea);
        Assert.AreEqual(24, day.Hours.Count);
        Assert.AreEqual(40d, day.Hours[19].Duration.TotalMinutes);
        Assert.AreEqual(15d, day.Hours[20].Duration.TotalMinutes);
        Assert.AreEqual(55d, day.Total.TotalMinutes);
        Assert.AreEqual(0d, day.Hours[18].BarHeight);
    }

    [TestMethod]
    public void MidnightAndFutureTimes_AreClippedToSelectedLocalDayAndNow()
    {
        var records = new[] { Session("midnight", -1, 30, 0, 20), Session("future-end", 10, 0, 11, 0),
            Session("tomorrow", 24, 0, 25, 0) };
        var day = SqliteFocusUsageRepository.Aggregate(Day, At(10, 15), records, timeZone: Korea);
        Assert.AreEqual(20d, day.Hours[0].Duration.TotalMinutes);
        Assert.AreEqual(15d, day.Hours[10].Duration.TotalMinutes);
        Assert.AreEqual(35d, day.Total.TotalMinutes);
        Assert.AreEqual(0d, day.Hours[23].Duration.TotalMinutes);
    }

    [TestMethod]
    public void OverlapsAndLiveSession_DoNotDoubleCount_StaleOpenSessionsAreIgnored()
    {
        var active = new FocusSessionRecord("live", At(19, 40));
        var day = SqliteFocusUsageRepository.Aggregate(Day, At(20, 15),
            [Session("a", 19, 10, 20, 0), Session("b", 19, 30, 20, 10),
                new FocusSessionRecord("stale", At(1)), active], active, Korea);
        Assert.AreEqual(65d, day.Total.TotalMinutes);
        Assert.AreEqual(50d, day.Hours[19].Duration.TotalMinutes);
        Assert.AreEqual(15d, day.Hours[20].Duration.TotalMinutes);
        Assert.IsTrue(day.Hours[20].IsCurrentHour);
    }

    [TestMethod]
    public void EmptyDay_HasZeroBarsAndStableScale()
    {
        var day = SqliteFocusUsageRepository.Aggregate(Day, At(12), [], timeZone: Korea);
        Assert.AreEqual(TimeSpan.Zero, day.Total);
        Assert.AreEqual(60d, day.ScaleMinutes);
        Assert.IsTrue(day.Hours.All(hour => hour.BarHeight == 0));
    }

    [TestMethod]
    public void RepeatedDstHour_PreservesElapsedTimeAndExpandsScale()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var start = new DateTime(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc);
        var day = SqliteFocusUsageRepository.Aggregate(new DateTime(2026, 11, 1), start.AddHours(3),
            [new FocusSessionRecord("fall", start, start.AddHours(2))], timeZone: zone);
        Assert.AreEqual(120d, day.Hours[1].Duration.TotalMinutes);
        Assert.AreEqual(120d, day.ScaleMinutes);
        Assert.AreEqual(88d, day.Hours[1].BarHeight);
    }

    [TestMethod]
    public void SkippedDstHour_DoesNotInventUsage()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var start = new DateTime(2026, 3, 8, 6, 30, 0, DateTimeKind.Utc);
        var day = SqliteFocusUsageRepository.Aggregate(new DateTime(2026, 3, 8), start.AddHours(2),
            [new FocusSessionRecord("spring", start, start.AddHours(1))], timeZone: zone);
        Assert.AreEqual(30d, day.Hours[1].Duration.TotalMinutes);
        Assert.AreEqual(0d, day.Hours[2].Duration.TotalMinutes);
        Assert.AreEqual(30d, day.Hours[3].Duration.TotalMinutes);
        Assert.AreEqual(60d, day.Total.TotalMinutes);
    }

    [TestMethod]
    public async Task ReadDay_UsesRawSessionsWithoutSavedReports_AndIncludesOnlyCurrentLiveSession()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        await sessions.StartSessionAsync(Session("ended", 19, 0, 19, 30));
        await sessions.StartSessionAsync(new("stale", At(1)));
        var active = new FocusSessionRecord("live", At(20));
        await sessions.StartSessionAsync(active);
        var repository = new SqliteFocusUsageRepository(db);
        var day = await repository.ReadDayAsync(Day, At(20, 10), active, Korea);
        Assert.AreEqual(40d, day.Total.TotalMinutes);
        Assert.AreEqual(0, (await new SqliteSessionReportRepository(db).GetDayPageAsync(Day, timeZone: Korea)).Count);
        var later = await repository.ReadDayAsync(Day, At(20, 20), active, Korea);
        Assert.AreEqual(50d, later.Total.TotalMinutes);
        Assert.AreEqual(30d, (await repository.ReadDayAsync(Day, At(20, 20), timeZone: Korea)).Total.TotalMinutes);
    }

    [TestMethod]
    public async Task ReopenedDatabase_RetainsCompletedAndInterruptedObservedUsage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"altong-focus-usage-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            using (var db = new SqliteDatabase(connectionString))
            {
                db.Initialize();
                await new SqliteFocusSessionRepository(db).StartSessionAsync(Session("persisted", 19, 0, 20, 0));
                await new SqliteFocusSessionRepository(db).StartSessionAsync(new("interrupted", At(20)));
                await new SqliteWindowSessionRepository(db).InsertAsync(new(0, "Editor.exe", "가상 창",
                    At(20), At(20, 10), 600, "interrupted"));
            }
            using (var reopened = new SqliteDatabase(connectionString))
            {
                reopened.Initialize();
                var usage = await new SqliteFocusUsageRepository(reopened).ReadDayAsync(Day, At(23), timeZone: Korea);
                Assert.AreEqual(70d, usage.Total.TotalMinutes,
                    "미종료 세션은 마지막 확인 시각까지만 복구하며 재실행까지의 시간을 더하면 안 됩니다.");
                Assert.IsTrue(usage.HasRecoveredSession);
                Assert.IsNull((await new SqliteFocusSessionRepository(reopened).GetByIdAsync("interrupted"))!.EndedAt,
                    "그래프 조회가 원본 종료 시각을 추정해서 저장하면 안 됩니다.");
            }
        }
        finally { File.Delete(path); }
    }
}

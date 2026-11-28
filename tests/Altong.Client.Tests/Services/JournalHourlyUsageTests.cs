using Altong.Client.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class JournalHourlyUsageTests
{
    private static ActivityJournalHour Hour(int day, int hour, params (string App, int Minutes)[] apps) =>
        new(new DateTime(2026, 11, day, hour, 0, 0), apps.Select(app => new ActivityJournalHourApp(app.App, TimeSpan.FromMinutes(app.Minutes), "")).ToArray());

    [TestMethod]
    public void Build_RanksApps_SumsSameHourAcrossDays_AndFindsBusiestHour()
    {
        var map = JournalHourlyUsage.Build(
        [
            Hour(24, 10, ("Code.exe", 40), ("chrome.exe", 10)),
            Hour(25, 10, ("Code.exe", 20)),
            Hour(25, 15, ("chrome.exe", 30), ("Notepad.exe", 5)),
        ]);

        CollectionAssert.AreEqual(new[] { "Code.exe", "chrome.exe", "Notepad.exe" }, map.Ranking.Select(app => app.AppName).ToArray());
        Assert.AreEqual(60 * 60, map.CellSeconds("Code.exe", 10), "여러 날의 같은 시각은 하나로 합칩니다.");
        Assert.AreEqual(70 * 60, map.HourSeconds(10), "그 시각 전체에는 모든 앱이 들어갑니다.");
        Assert.AreEqual(70 * 60, map.MaxHourSeconds);
        Assert.AreEqual(9, map.FirstHour);
        Assert.AreEqual(22, map.LastHour);
        Assert.AreEqual("10시~11시", map.BusiestRangeText);
        StringAssert.Contains(map.BusiestTailText, "Code 사용이 가장 길었어요");
    }

    [TestMethod]
    public void Build_KeepsTopFive_ExtendsHoursOutsideDay_AndHandlesEmpty()
    {
        var hours = Enumerable.Range(0, 7).Select(i => Hour(25, 7, ($"App{i}.exe", 10 + i))).ToArray();
        var map = JournalHourlyUsage.Build(hours);
        Assert.AreEqual(5, map.Ranking.Count);
        Assert.AreEqual(7, map.FirstHour, "기록이 9시 전이면 열지도를 앞으로 넓힙니다.");
        Assert.AreEqual(0, map.CellSeconds("App0.exe", 7), "순위 밖 앱은 따로 세지 않고 기타로 묶습니다.");

        var empty = JournalHourlyUsage.Build([]);
        Assert.AreEqual(0, empty.Ranking.Count);
        Assert.IsNull(empty.BusiestRangeText);
    }
}

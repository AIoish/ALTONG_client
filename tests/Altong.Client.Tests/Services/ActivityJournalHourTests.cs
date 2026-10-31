using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ActivityJournalHourTests
{
    private static readonly TimeZoneInfo Korea = TimeZoneInfo.CreateCustomTimeZone("Journal-test-Korea",
        TimeSpan.FromHours(9), "Korea", "Korea");

    [TestMethod]
    public void Group_MergesRepeatedWindowsAndProcessCasingWithinAnHour()
    {
        var groups = ActivityJournalHour.Group([
            Entry("Editor.exe", "draft.cs", 19, 5, 0, 19, 12, 30),
            Entry("EDITOR.exe", "draft.cs", 19, 5, 0, 19, 12, 30),
            Entry("Editor.exe", "draft.cs", 19, 6, 0, 19, 10, 0),
            Entry("EDITOR.exe", "review.cs", 19, 20, 0, 19, 28, 15),
            Entry("Browser.exe", "Docs", 19, 30, 0, 19, 35, 0)
        ], Korea);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("19시", groups[0].HourText);
        Assert.AreEqual(2, groups[0].Apps.Count);
        Assert.AreEqual(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(45), groups[0].Apps[0].Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(45), groups[0].Duration);
        StringAssert.Contains(groups[0].Apps[0].WindowSummary, "외 1개 창");
        Assert.AreEqual("앱 2개 · 총 20분", groups[0].SummaryText);
    }

    [TestMethod]
    public void Group_SplitsCrossHourRecordsAndExcludesAnEmptyEndingHour()
    {
        var groups = ActivityJournalHour.Group([
            Entry("Editor.exe", "draft.cs", 20, 10, 0, 20, 15, 0),
            Entry("Editor.exe", "draft.cs", 19, 50, 0, 20, 10, 0),
            Entry("EDITOR.exe", "draft.cs", 19, 55, 0, 20, 5, 0),
            Entry("Browser.exe", "Docs", 20, 45, 0, 21, 0, 0)
        ], Korea);

        CollectionAssert.AreEqual(new[] { "19시", "20시" }, groups.Select(group => group.HourText).ToArray());
        Assert.AreEqual(TimeSpan.FromMinutes(10), groups[0].Apps.Single().Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(15), groups[1].Apps.Single(app => app.AppName == "Editor.exe").Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(40), TimeSpan.FromTicks(groups.Sum(group => group.Duration.Ticks)));
        var apps = ActivityJournalHour.SummarizeApps(groups);
        Assert.AreEqual(25 * 60d, apps.Single(app => app.AppName == "Editor.exe").Seconds);
        Assert.AreEqual(40 * 60d, apps.Sum(app => app.Seconds));
    }

    [TestMethod]
    public void Group_KeepsLocalDatesDistinctAcrossMidnight()
    {
        var start = new DateTimeOffset(2026, 10, 2, 23, 50, 0, TimeSpan.FromHours(9)).UtcDateTime;
        var groups = ActivityJournalHour.Group([new ActivityJournalEntry("Editor", "", start, start.AddMinutes(20))], Korea);

        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual(new DateTime(2026, 10, 2, 23, 0, 0), groups[0].LocalHour);
        Assert.AreEqual(new DateTime(2026, 10, 3), groups[1].LocalHour);
        Assert.IsTrue(groups.All(group => group.Duration == TimeSpan.FromMinutes(10)));
    }

    [TestMethod]
    public void Group_PreservesRealDurationWhenDaylightSavingRepeatsAnHour()
    {
        var groups = ActivityJournalHour.Group([new ActivityJournalEntry("Editor", "",
            new DateTime(2025, 11, 2, 4, 30, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 2, 7, 30, 0, DateTimeKind.Utc))], DaylightSavingZone());

        CollectionAssert.AreEqual(new[] { "00시", "01시", "02시" }, groups.Select(group => group.HourText).ToArray());
        Assert.AreEqual(TimeSpan.FromHours(2), groups[1].Duration);
        Assert.AreEqual(TimeSpan.FromHours(3), TimeSpan.FromTicks(groups.Sum(group => group.Duration.Ticks)));
    }

    [TestMethod]
    public void Group_SkipsANonexistentLocalHourDuringDaylightSaving()
    {
        var groups = ActivityJournalHour.Group([new ActivityJournalEntry("Editor", "",
            new DateTime(2025, 3, 9, 6, 30, 0, DateTimeKind.Utc),
            new DateTime(2025, 3, 9, 7, 30, 0, DateTimeKind.Utc))], DaylightSavingZone());

        CollectionAssert.AreEqual(new[] { "01시", "03시" }, groups.Select(group => group.HourText).ToArray());
        Assert.IsTrue(groups.All(group => group.Duration == TimeSpan.FromMinutes(30)));
    }

    [TestMethod]
    public void Group_CompactsManyShortRecordsWithoutLosingSeconds()
    {
        var start = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        var entries = Enumerable.Range(0, 1440).Select(minute => new ActivityJournalEntry("Editor", "draft.cs",
            start.AddMinutes(minute).AddSeconds(15), start.AddMinutes(minute).AddSeconds(45)));
        var groups = ActivityJournalHour.Group(entries, TimeZoneInfo.Utc);

        Assert.AreEqual(24, groups.Count);
        Assert.IsTrue(groups.All(group => group.Apps.Count == 1 && group.Duration == TimeSpan.FromMinutes(30)));
        Assert.AreEqual(TimeSpan.FromHours(12), TimeSpan.FromTicks(groups.Sum(group => group.Duration.Ticks)));
    }

    [TestMethod]
    public void Group_IgnoresEmptyAndReversedIntervals()
    {
        Assert.AreEqual(0, ActivityJournalHour.Group([], Korea).Count);
        Assert.AreEqual(0, ActivityJournalHour.Group([
            Entry("Editor", "", 19, 0, 0, 19, 0, 0),
            Entry("Editor", "", 19, 1, 0, 19, 0, 0)
        ], Korea).Count);
    }

    private static ActivityJournalEntry Entry(string app, string title,
        int startHour, int startMinute, int startSecond, int endHour, int endMinute, int endSecond) => new(app, title,
        new DateTimeOffset(2026, 10, 2, startHour, startMinute, startSecond, TimeSpan.FromHours(9)).UtcDateTime,
        new DateTimeOffset(2026, 10, 2, endHour, endMinute, endSecond, TimeSpan.FromHours(9)).UtcDateTime);

    private static TimeZoneInfo DaylightSavingZone()
    {
        var transitionStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 9);
        var transitionEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 2);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1),
            new DateTime(2025, 12, 31), TimeSpan.FromHours(1), transitionStart, transitionEnd);
        return TimeZoneInfo.CreateCustomTimeZone("Journal-test-DST", TimeSpan.FromHours(-5),
            "Test", "Standard", "Daylight", [rule]);
    }
}

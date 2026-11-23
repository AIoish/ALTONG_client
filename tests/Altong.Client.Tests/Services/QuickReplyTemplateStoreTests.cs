using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class QuickReplyTemplateStoreTests
{
    [TestMethod]
    public void Save_TrimsBlankAndDuplicateLines_AndSurvivesReload()
    {
        var path = Path.Combine(Path.GetTempPath(), "altong-replies-" + Guid.NewGuid().ToString("N"), "replies.json");
        var store = new QuickReplyTemplateStore(path);
        CollectionAssert.AreEqual(QuickReplyTemplateStore.Defaults.ToArray(), store.Current.ToArray());

        store.Save(["  지금 볼게요  ", "", "지금 볼게요", "회의 끝나고 연락드릴게요"]);

        CollectionAssert.AreEqual(new[] { "지금 볼게요", "회의 끝나고 연락드릴게요" }, store.Current.ToArray());
        CollectionAssert.AreEqual(store.Current.ToArray(), new QuickReplyTemplateStore(path).Current.ToArray());
    }

    [TestMethod]
    public void Save_RejectsEmptyTooManyOrTooLong_AndKeepsCurrent()
    {
        var store = new QuickReplyTemplateStore();
        Assert.ThrowsException<ArgumentException>(() => store.Save(["", "  "]));
        Assert.ThrowsException<ArgumentException>(() => store.Save(Enumerable.Range(1, 6).Select(i => $"문구 {i}")));
        Assert.ThrowsException<ArgumentException>(() => store.Save([new string('가', QuickReplyTemplateStore.MaxLength + 1)]));
        CollectionAssert.AreEqual(QuickReplyTemplateStore.Defaults.ToArray(), store.Current.ToArray());
    }

    [TestMethod]
    public void CorruptFile_FallsBackToDefaultsWithWarning()
    {
        var folder = Path.Combine(Path.GetTempPath(), "altong-replies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "replies.json");
        File.WriteAllText(path, "{ not json");

        var store = new QuickReplyTemplateStore(path);

        CollectionAssert.AreEqual(QuickReplyTemplateStore.Defaults.ToArray(), store.Current.ToArray());
        Assert.IsNotNull(store.LoadWarning);
    }
}

[TestClass]
public sealed class ScheduleEditorTimeTests
{
    private static CalendarSummaryItemViewModel Row(CalendarSchedule schedule) =>
        new(new NotificationSummaryItem("s1", "회의 요약", true, schedule), "session");

    private static CalendarSummaryItemViewModel SameDayRow()
    {
        var row = Row(new("evt", "회의", null, null, null, null, null));
        row.StartDate = new DateTime(2026, 11, 20);
        row.EndDate = new DateTime(2026, 11, 20);
        return row;
    }

    [TestMethod]
    public void MissingTimeZone_DefaultsToKorea_AndCanBeApplied()
    {
        var row = SameDayRow();
        Assert.AreEqual("Asia/Seoul", row.TimeZone);
        Assert.AreEqual("시간대 · 한국 시간", row.TimeZoneHeader);

        row.StartTime = "14:00";
        Assert.AreEqual("15:00", row.EndTime, "같은 날 시작만 고르면 종료가 1시간 뒤로 맞춰져야 합니다.");
        row.TimeZone = "";
        Assert.IsTrue(row.ApplyEdits(), row.Status);
        Assert.AreEqual("Asia/Seoul", row.Draft.TimeZone);
    }

    [TestMethod]
    public void OffSlotTime_RefreshesOptionList_SoDropdownIsNotBlank()
    {
        var row = SameDayRow();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.EndTime = "15:15";
        CollectionAssert.Contains(changed, nameof(CalendarSummaryItemViewModel.EndTimeOptions),
            "30분 단위가 아닌 시각이 들어오면 목록을 다시 읽어야 합니다.");
        CollectionAssert.Contains(row.EndTimeOptions.ToList(), "15:15");

        changed.Clear();
        row.EndTime = "16:00";
        CollectionAssert.Contains(changed, nameof(CalendarSummaryItemViewModel.EndTimeOptions), "빠질 때도 목록을 다시 읽어야 합니다.");
        CollectionAssert.DoesNotContain(row.EndTimeOptions.ToList(), "15:15");

        changed.Clear();
        row.EndTime = "16:30";
        CollectionAssert.DoesNotContain(changed, nameof(CalendarSummaryItemViewModel.EndTimeOptions),
            "30분 단위끼리 바꿀 때는 목록을 다시 만들 필요가 없습니다.");
    }

    [TestMethod]
    public void TimeOptions_AreHalfHourSlots_AndKeepUnusualExtractedTime()
    {
        Assert.AreEqual(48, CalendarSummaryItemViewModel.HalfHourSlots.Count);
        Assert.AreEqual("23:30", CalendarSummaryItemViewModel.HalfHourSlots[^1]);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var start = new DateTimeOffset(2026, 11, 20, 14, 15, 0, zone.GetUtcOffset(new DateTime(2026, 11, 20)));
        var row = Row(new("evt", "회의", start, start.AddHours(1), "Asia/Seoul", null, null));

        CollectionAssert.Contains(row.StartTimeOptions.ToArray(), "14:15");
        Assert.AreEqual(49, row.StartTimeOptions.Count);
        Assert.AreEqual(48, CalendarSummaryItemViewModel.HalfHourSlots.Count);
    }

    [TestMethod]
    public void MovingStartBeforeEnd_DoesNotChangeEnd()
    {
        var row = SameDayRow();
        row.EndTime = "18:00";
        row.StartTime = "09:00";
        Assert.AreEqual("18:00", row.EndTime);
    }
}

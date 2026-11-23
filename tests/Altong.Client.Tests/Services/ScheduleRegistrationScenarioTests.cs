using Altong.Client.Models;
using Altong.Client.Services.AI.Briefing;
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// 알림 문장 → 일정 추출 → 리포트 일정 카드 → [캘린더에 저장] → 대시보드 캘린더 → Google Calendar 링크까지
/// 실제 흐름을 여러 문장으로 끝까지 돌려 본다.
/// </summary>
[TestClass]
public sealed class ScheduleRegistrationScenarioTests
{
    // 2026-11-20(금) 오전 10시 기준으로 "내일", "다음주 월요일"을 해석한다.
    private static readonly DateTime Reference = new(2026, 11, 20, 10, 0, 0);

    [DataTestMethod]
    [DataRow("내일 오후 3시에 주간 회의 있습니다", "2026-11-21 15:00", "2026-11-21 16:00")]
    [DataRow("11월 25일 14:00~15:30 회의실 A에서 기획 회의", "2026-11-25 14:00", "2026-11-25 15:30")]
    [DataRow("다음주 월요일 10시 팀 미팅 잡혔어요", "2026-11-30 10:00", "2026-11-30 11:00")]
    [DataRow("오늘 오후 7시 강남역에서 회식", "2026-11-20 19:00", "2026-11-20 20:00")]
    [DataRow("모레 오전 9시 30분 면접 일정 안내드립니다", "2026-11-22 09:30", "2026-11-22 10:30")]
    public void ExtractedSchedule_SavesToDashboard_AndOpensMatchingGoogleLink(string message, string start, string end)
    {
        var row = ExtractRow(message);
        Assert.IsTrue(row.IsScheduleRelated, $"일정으로 인식해야 합니다: {message}");
        Assert.AreEqual(start, Local(row.Draft.Start), message);
        Assert.AreEqual(end, Local(row.Draft.End), message);
        Assert.IsNull(row.Draft.Validate(), $"추출한 일정은 바로 저장할 수 있어야 합니다: {row.Draft.Validate()}");

        var (store, folder) = Store();
        try
        {
            store.Save(new("session", row.Summary.SummaryId, "Slack", row.Draft));
            var reloaded = new DashboardCalendarStore(Path.Combine(folder, "calendar.json"));
            var entry = reloaded.GetDay(row.Draft.Start!.Value.LocalDateTime.Date).Single();
            Assert.AreEqual(row.Draft, entry.Schedule, "다시 열어도 같은 일정이어야 합니다.");

            var query = Query(GoogleCalendarLink.Build(entry.Schedule));
            Assert.AreEqual("TEMPLATE", query["action"]);
            Assert.AreEqual(row.Draft.Title, query["text"]);
            Assert.AreEqual("Asia/Seoul", query["ctz"]);
            Assert.AreEqual(Utc(start) + "/" + Utc(end), query["dates"], "Google에는 UTC로 정확한 시각이 가야 합니다.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [DataTestMethod]
    [DataRow("50% 할인 쿠폰이 도착했어요 내일까지", "광고/홍보")]
    [DataRow("ㅋㅋㅋ 그거 진짜 웃기다", "일반 업무")]
    [DataRow("점심 맛있게 드세요", "일반 업무")]
    public void NonSchedule_HasNoCalendarAction(string message, string category)
    {
        var row = ExtractRow(message, category);
        Assert.IsFalse(row.IsScheduleRelated, $"일정이 아닌 알림에 캘린더 저장 버튼이 나오면 안 됩니다: {message}");
    }

    [TestMethod]
    public void CancelledMeeting_IsMarkedInDetails_SoUserSeesItBeforeSaving()
    {
        var row = ExtractRow("내일 오후 3시 회의 취소되었습니다");
        Assert.IsTrue(row.IsScheduleRelated);
        StringAssert.StartsWith(row.Draft.Details, "[취소/연기]");
        StringAssert.Contains(Query(GoogleCalendarLink.Build(row.Draft))["details"], "[취소/연기]");
    }

    [TestMethod]
    public void MeetingWithoutTime_PickingOnlyStartDateAndTime_IsEnoughToSave()
    {
        // AI가 일정이라고 봤지만 날짜·시각을 못 뽑은 경우: 사용자가 시작 날짜와 시각만 고르면 저장할 수 있어야 한다.
        var row = ExtractRow("회의 자료 미리 봐 주세요", "일정/회의");
        Assert.IsTrue(row.IsScheduleRelated);
        Assert.IsNotNull(row.Draft.Validate(), "날짜가 없으면 바로 저장되면 안 됩니다.");

        row.IsEditing = true;
        row.StartDate = new DateTime(2026, 11, 24);
        row.StartTime = "14:00";
        Assert.AreEqual(new DateTime(2026, 11, 24), row.EndDate, "종료 날짜는 시작 날짜를 따라가야 합니다.");
        Assert.AreEqual("15:00", row.EndTime);
        Assert.IsTrue(row.ApplyEdits(), row.Status);
        Assert.AreEqual("2026-11-24 14:00", Local(row.Draft.Start));
        Assert.AreEqual("2026-11-24 15:00", Local(row.Draft.End));
        Assert.IsNull(row.Draft.Validate());
    }

    [TestMethod]
    public void MovingStartDateLater_KeepsEndOnOrAfterStart()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        row.IsEditing = true;
        row.StartDate = new DateTime(2026, 11, 27);
        Assert.AreEqual(new DateTime(2026, 11, 27), row.EndDate, "시작 날짜를 종료 날짜 뒤로 옮기면 종료 날짜도 따라와야 합니다.");
        Assert.IsTrue(row.ApplyEdits(), row.Status);
        Assert.AreEqual("2026-11-27 15:00", Local(row.Draft.Start));
        Assert.AreEqual("2026-11-27 16:00", Local(row.Draft.End));
    }

    [TestMethod]
    public void EditedTitleWithSymbols_IsEscapedInGoogleLink()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        row.IsEditing = true;
        row.Title = "Q&A #2 / 50% 점검 + 회고";
        row.Location = "본사 3층 (A룸)";
        Assert.IsTrue(row.ApplyEdits(), row.Status);
        var link = GoogleCalendarLink.Build(row.Draft);
        var query = Query(link);
        Assert.AreEqual("Q&A #2 / 50% 점검 + 회고", query["text"], "특수문자가 링크를 깨뜨리면 안 됩니다.");
        Assert.AreEqual("본사 3층 (A룸)", query["location"]);
    }

    [TestMethod]
    public void LateNightStart_DoesNotInventNextDayEnd_AndAsksUserToFixIt()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        row.IsEditing = true;
        row.EndTime = "";
        row.StartTime = "23:30";
        Assert.AreEqual("", row.EndTime, "23시 이후 시작은 종료를 멋대로 정하지 않습니다.");
        Assert.IsFalse(row.ApplyEdits());
        Assert.AreEqual("시작·종료 날짜와 시각을 선택해 주세요.", row.Status);

        row.EndDate = new DateTime(2026, 11, 22);
        row.EndTime = "00:30";
        Assert.IsTrue(row.ApplyEdits(), row.Status);
        Assert.AreEqual("2026-11-21 23:30", Local(row.Draft.Start));
        Assert.AreEqual("2026-11-22 00:30", Local(row.Draft.End));
    }

    [TestMethod]
    public void EndBeforeStart_IsRejectedWithClearMessage()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        row.IsEditing = true;
        row.EndTime = "14:00";
        Assert.IsFalse(row.ApplyEdits());
        Assert.AreEqual("종료 시각은 시작 시각보다 늦어야 합니다.", row.Status);
    }

    [TestMethod]
    public void OtherTimeZone_KeepsWallClock_AndSendsCorrectUtc()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        row.IsEditing = true;
        row.TimeZone = "America/New_York";
        Assert.IsTrue(row.ApplyEdits(), row.Status);
        var query = Query(GoogleCalendarLink.Build(row.Draft));
        Assert.AreEqual("America/New_York", query["ctz"]);
        // 뉴욕 11월 21일 15:00(EST, UTC-5) = 20:00Z
        Assert.AreEqual("20261121T200000Z/20261121T210000Z", query["dates"]);
    }

    [TestMethod]
    public void UnknownTimeZone_IsRejected_NotSilentlyChanged()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        row.IsEditing = true;
        row.TimeZone = "Mars/Olympus";
        Assert.IsFalse(row.ApplyEdits());
        StringAssert.Contains(row.Status, "시간대");
        Assert.AreEqual("Asia/Seoul", row.Draft.TimeZone, "실패한 수정은 기존 일정에 반영되면 안 됩니다.");
    }

    [TestMethod]
    public void SavingSameScheduleTwice_KeepsOneEntry_AndEditReplacesIt()
    {
        var row = ExtractRow("내일 오후 3시에 주간 회의 있습니다");
        var (store, folder) = Store();
        try
        {
            store.Save(new("session", row.Summary.SummaryId, "Slack", row.Draft));
            store.Save(new("session", row.Summary.SummaryId, "Slack", row.Draft));
            Assert.AreEqual(1, store.GetDay(new DateTime(2026, 11, 21)).Count);

            row.IsEditing = true;
            row.StartDate = new DateTime(2026, 11, 23);
            row.EndDate = new DateTime(2026, 11, 23);
            Assert.IsTrue(row.ApplyEdits(), row.Status);
            store.Save(new("session", row.Summary.SummaryId, "Slack", row.Draft));
            Assert.AreEqual(0, store.GetDay(new DateTime(2026, 11, 21)).Count, "고친 일정은 원래 날짜에서 빠져야 합니다.");
            Assert.AreEqual(1, store.GetDay(new DateTime(2026, 11, 23)).Count);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestMethod]
    public void GoogleLinkOpener_RejectsAnythingButCalendarTemplate()
    {
        Assert.ThrowsException<ArgumentException>(() => GoogleCalendarLink.Open("https://evil.example.com/calendar/render?action=TEMPLATE"));
        Assert.ThrowsException<ArgumentException>(() => GoogleCalendarLink.Open("http://calendar.google.com/calendar/render?action=TEMPLATE"));
        Assert.ThrowsException<ArgumentException>(() => GoogleCalendarLink.Open("file:///C:/Windows/System32/calc.exe"));
    }

    private static CalendarSummaryItemViewModel ExtractRow(string message, string category = "일반 업무")
    {
        var (related, schedule) = BriefingScheduleExtractor.Extract(message, category, [message], Reference, eventId: "evt-test");
        var summary = new NotificationSummaryItem("sum-test", message, related,
            schedule ?? (related ? new CalendarSchedule("evt-test", message, null, null, "Asia/Seoul", null, message) : null))
            { AppName = "Slack" };
        return new CalendarSummaryItemViewModel(summary, "session");
    }

    private static (DashboardCalendarStore Store, string Folder) Store()
    {
        var folder = Path.Combine(Path.GetTempPath(), "altong-schedule-scenario-" + Guid.NewGuid().ToString("N"));
        return (new DashboardCalendarStore(Path.Combine(folder, "calendar.json")), folder);
    }

    private static string Local(DateTimeOffset? value) =>
        value is { } time ? time.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm") : "(없음)";

    private static string Utc(string kst) =>
        new DateTimeOffset(DateTime.Parse(kst), TimeSpan.FromHours(9)).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

    private static Dictionary<string, string> Query(string link) =>
        new Uri(link).Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
}

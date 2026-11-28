using System.IO;
using System.Text.Json;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class GoogleCalendarTests
{
    private static CalendarSchedule Schedule() => new("stable-meeting-id", "프로젝트 회의",
        new DateTimeOffset(2026, 10, 5, 14, 0, 0, TimeSpan.FromHours(9)),
        new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.FromHours(9)), "Asia/Seoul", "회의실 A", "진행 상황 공유");

    [DataTestMethod]
    [DataRow("2026-10-12T10:00:17.250Z", "Asia/Seoul", "2026-10-12", "19:00")]
    [DataRow("2026-10-12T20:00:00Z", "Asia/Seoul", "2026-10-13", "05:00")]
    [DataRow("2026-11-01T06:30:00Z", "America/New_York", "2026-11-01", "01:30")]
    public void UtcSchedule_DisplayAndUnchangedEditsPreserveTheInstant(string utc, string zone, string date, string time)
    {
        var original = DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture);
        var schedule = Schedule() with { Start = original, End = original.AddHours(1), TimeZone = zone };
        var row = new CalendarSummaryItemViewModel(new("summary", "가상 일정", true, schedule));
        Assert.AreEqual(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), row.StartDate);
        Assert.AreEqual(time, row.StartTime);
        StringAssert.Contains(row.ScheduleWhen, time);
        Assert.IsNull(row.Draft.Validate());
        Assert.IsTrue(row.ApplyEdits());
        Assert.AreEqual(original.UtcDateTime, row.Draft.Start!.Value.UtcDateTime);
        Assert.AreEqual(original.AddHours(1).UtcDateTime, row.Draft.End!.Value.UtcDateTime);
        Assert.AreEqual(TimeSpan.Zero, row.Summary.Schedule!.Start!.Value.Offset, "원본 요약은 수정하지 않습니다.");
        StringAssert.Contains(GoogleCalendarLink.Build(row.Draft), original.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'"));
    }

    [DataTestMethod]
    [DataRow("[null]")]
    [DataRow("[{\"SessionId\":\"partial\",\"SummaryId\":\"summary\"},null]")]
    public void SummaryState_InvalidEntriesWarnWithoutCrashingOrOverwriting(string broken)
    {
        var path = Path.Combine(Path.GetTempPath(), "altong-summary-invalid-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new DashboardSummaryStateStore(path);
            store.Save(new("saved", "summary", Schedule(), true));
            File.WriteAllText(path, broken);
            store.Load();
            Assert.IsNotNull(store.LoadWarning);
            Assert.IsNotNull(store.Get("saved", "summary"));
            Assert.IsNull(store.Get("partial", "summary"), "실패한 읽기 결과를 부분적으로 반영하지 않습니다.");
            Assert.ThrowsException<IOException>(() => store.Save(new("new", "summary")));
            Assert.AreEqual(broken, File.ReadAllText(path));
            var restarted = new DashboardSummaryStateStore(path);
            restarted.Load();
            Assert.IsNotNull(restarted.LoadWarning);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void DashboardCalendar_PersistsDeduplicatesUpdatesAndDeletes_UsingLocalStartDate()
    {
        var path = Path.Combine(Path.GetTempPath(), "altong-local-calendar-" + Guid.NewGuid() + ".json");
        try
        {
            var day = new DateTime(2026, 10, 5);
            var start = new DateTimeOffset(DateTime.SpecifyKind(day.AddHours(23).AddMinutes(50), DateTimeKind.Local));
            var schedule = Schedule() with
            {
                Start = start.ToOffset(TimeSpan.FromHours(9)),
                End = start.AddMinutes(30).ToOffset(TimeSpan.FromHours(9))
            };
            var entry = new DashboardCalendarEntry("first", "summary", "KakaoTalk", schedule);
            var store = new DashboardCalendarStore(path);
            store.Save(entry);
            store.Save(entry);
            Assert.AreEqual(1, store.GetDay(day).Count);
            Assert.AreEqual(0, store.GetDay(day.AddDays(1)).Count, "자정을 넘긴 일정은 로컬 시작 날짜에만 표시합니다.");
            StringAssert.Contains(entry.WhenText, "10월 6일");
            store.Save(entry with { SessionId = "second" });
            Assert.AreEqual(2, store.GetDay(day).Count, "세션이 다르면 같은 summary_id를 구분합니다.");
            var changed = entry with { Schedule = schedule with { Start = schedule.Start!.Value.AddDays(2), End = schedule.End!.Value.AddDays(2) } };
            store.Save(changed);
            var restarted = new DashboardCalendarStore(path);
            Assert.AreEqual(1, restarted.GetDay(day).Count);
            Assert.AreEqual(changed, restarted.GetDay(day.AddDays(2)).Single());
            Assert.AreEqual(1, restarted.GetMonthCounts(day)[day.AddDays(2)]);
            restarted.Delete("first", "summary");
            var afterDelete = new DashboardCalendarStore(path);
            Assert.AreEqual(0, afterDelete.GetDay(day.AddDays(2)).Count);
            Assert.AreEqual(1, afterDelete.GetDay(day).Count);
            Assert.ThrowsException<ArgumentException>(() => afterDelete.Save(entry with { Schedule = schedule with { End = null } }));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void DashboardCalendar_UnreadableFileIsPreserved_AndDoesNotReportSuccessfulSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "altong-local-calendar-invalid-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{invalid-json");
            var store = new DashboardCalendarStore(path);
            Assert.IsNotNull(store.LoadWarning);
            Assert.ThrowsException<IOException>(() => store.Save(new("session", "summary", null, Schedule())));
            Assert.AreEqual("{invalid-json", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void Link_PrefillsFields_WithUtcDatesAndEscapedText()
    {
        var schedule = Schedule() with
        {
            Title = "개발팀 회의 & 점검 + 일정",
            Location = "창의관 402호 #A",
            Details = "내용 확인\n문서: https://example.com/?a=1&b=2"
        };
        var link = new Uri(GoogleCalendarLink.Build(schedule));
        Assert.AreEqual("https", link.Scheme);
        Assert.AreEqual("calendar.google.com", link.Host);
        Assert.AreEqual("/calendar/render", link.AbsolutePath);
        var fields = link.Query[1..].Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        Assert.AreEqual(6, fields.Count, "Text must not inject additional URL parameters.");
        Assert.AreEqual("TEMPLATE", fields["action"]);
        Assert.AreEqual(schedule.Title, fields["text"]);
        Assert.AreEqual("20261005T050000Z/20261005T060000Z", fields["dates"]);
        Assert.AreEqual("Asia/Seoul", fields["ctz"]);
        Assert.AreEqual(schedule.Location, fields["location"]);
        Assert.AreEqual(schedule.Details, fields["details"]);
        Assert.ThrowsException<ArgumentException>(() => GoogleCalendarLink.Build(schedule with { End = null }));
    }

    [TestMethod]
    public void Contract_MissingOffset_IsNotInferred_AndUserCanSupplyDates()
    {
        var draft = JsonSerializer.Deserialize<CalendarSchedule>("""
            {"event_id":"meeting","title":"회의","start":"2026-10-05T14:00:00",
             "end":null,"time_zone":null,"location":null,"details":"약속 확인"}
            """)!;
        Assert.IsNull(draft.Start);
        Assert.IsNull(JsonSerializer.Deserialize<CalendarSchedule>(
            "{\"event_id\":\"meeting\",\"title\":\"회의\",\"start\":\"2026-10-05Z\"}")!.Start,
            "날짜만 있는 입력을 자정 일정으로 추정하지 않습니다.");
        var row = new CalendarSummaryItemViewModel(new("summary", "다음 주에 회의", true, draft));
        Assert.IsFalse(row.ApplyEdits());
        row.StartDate = row.EndDate = new DateTime(2026, 10, 5);
        row.StartTime = "14:00"; row.EndTime = "15:00"; row.TimeZone = "Asia/Seoul";
        Assert.IsTrue(row.ApplyEdits());
        Assert.IsNull(row.Draft.Validate());
        Assert.AreEqual(TimeSpan.FromHours(9), row.Draft.Start!.Value.Offset);
        Assert.IsNotNull((row.Draft with { End = row.Draft.Start }).Validate());
        Assert.IsNotNull((row.Draft with { TimeZone = "America/New_York" }).Validate());
    }

    [TestMethod]
    public async Task JsonProvider_ChecksSessionAndVersion_AndRetainsClassification()
    {
        var folder = Path.Combine(Path.GetTempPath(), "altong-calendar-json-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var items = new[] { new NotificationSummaryItem("summary", "회의 일정", true, Schedule()),
                new NotificationSummaryItem("ordinary", "자료 확인 요청", false, null) };
            var batch = new NotificationSummaryBatch(1, "session-a", items);
            var file = Path.Combine(folder, "session-a.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(batch));
            var provider = new JsonReportSummaryProvider(folder);
            var supply = await provider.ReadAsync("session-a", CancellationToken.None);
            Assert.AreEqual(2, supply.Items.Count);
            Assert.IsFalse(supply.Items[1].IsScheduleRelated);
            Assert.AreEqual(Schedule(), supply.Items[0].Schedule);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(batch with { SessionId = "other-session" }));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => provider.ReadAsync("session-a", CancellationToken.None));
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(batch with { SchemaVersion = 2 }));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => provider.ReadAsync("session-a", CancellationToken.None));
        }
        finally { foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file); Directory.Delete(folder); }
    }

    [TestMethod]
    public async Task Snapshot_PreservesScheduleCorrections_AndAcceptsOldJsonWithoutItems()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = new SessionResultsService(db, new SqliteFocusSessionRepository(db));
        var start = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        results.Begin(start, 0);
        var report = (await results.CompleteFocusSessionAsync(start.AddMinutes(5)))! with
        {
            SummaryItems = [new("summary", "프로젝트 회의", true, Schedule() with { Location = "사용자가 보완한 장소" })]
        };
        await results.Reports.SaveAsync(report);
        var restored = (await new SqliteSessionReportRepository(db).GetSnapshotAsync(report.SessionId!))!;
        Assert.AreEqual(report.SummaryItems.Single(), restored.SummaryItems.Single());
        using var connection = db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE focus_session_reports SET snapshot_json = json_remove(snapshot_json, '$.SummaryItems') WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", report.SessionId);
        await command.ExecuteNonQueryAsync();
        var old = (await results.Reports.GetSnapshotAsync(report.SessionId!))!;
        Assert.AreEqual(0, old.SummaryItems.Count);
        Assert.AreEqual(report.StartedAt, old.StartedAt);
    }
}

using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class AppNameDisplayTests
{
    [DataTestMethod]
    [DataRow("Discord.exe", "Discord")]
    [DataRow("KakaoTalk.EXE", "KakaoTalk")]
    [DataRow("My.App.exe", "My.App")]
    [DataRow(" .NET Tool.exe ", ".NET Tool")]
    [DataRow("Library.dll", "Library.dll")]
    [DataRow("app.exe backup", "app.exe backup")]
    [DataRow(".exe", ".exe")]
    [DataRow("", "알 수 없는 앱")]
    [DataRow(null, "알 수 없는 앱")]
    public void DisplayName_HidesOnlyTrailingExecutableExtension(string? original, string expected) =>
        Assert.AreEqual(expected, AppNameDisplayConverter.Format(original));

    [TestMethod]
    public void DisplayFormatting_PreservesOriginalIdentityAndNotificationContent()
    {
        var record = new NotificationRecord("notification", "Discord.exe", "사용자.exe", "제목.exe", "본문.exe", DateTime.UtcNow, false);
        var notification = new NotificationHistoryItem(record);
        Assert.AreEqual("Discord", notification.DisplayName);
        Assert.AreEqual("Discord.exe", notification.AppName);
        Assert.AreSame(record, notification.Record);
        Assert.AreEqual("사용자.exe", notification.SenderText);
        Assert.AreEqual("제목.exe", notification.TitleText);
        Assert.AreEqual("본문.exe", notification.BodyText);

        var summary = new NotificationSummaryItem("summary", "요약", false, null) { AppName = record.AppName };
        Assert.AreEqual("Discord", new CalendarSummaryItemViewModel(summary).AppDisplayName);
        Assert.AreEqual("Discord.exe", summary.AppName);
        var entry = new DashboardCalendarEntry("session", summary.SummaryId, summary.AppName,
            new CalendarSchedule("event", "일정", null, null, null, null, null));
        Assert.AreEqual("Discord · 알림에서 저장", entry.SourceText);
        Assert.AreEqual("Discord.exe", entry.AppName);
    }
}

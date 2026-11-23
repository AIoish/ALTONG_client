using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ReportSummaryAttachTests
{
    [TestMethod]
    public async Task AttachSummary_FillsOnlyReportsSavedWithoutSummary()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var reports = new SqliteSessionReportRepository(db);
        var sessions = new SqliteFocusSessionRepository(db);
        var end = DateTime.UtcNow;
        NotificationSummaryItem Summary(string text) => new("summary-" + text, text, false, null);

        await sessions.StartSessionAsync(new("empty", end.AddMinutes(-30), end, IsCompleted: true));
        await reports.SaveAsync(new SessionResult(end.AddMinutes(-30), end, [], [], true) { SessionId = "empty" });
        Assert.IsTrue(await reports.AttachSummaryAsync("empty", [Summary("나중 요약")]));
        Assert.AreEqual("나중 요약", (await reports.GetSnapshotAsync("empty"))!.SummaryItems.Single().Text);
        Assert.IsFalse(await reports.AttachSummaryAsync("empty", [Summary("다시 만든 요약")]),
            "이미 요약이 있는 저장본은 바꾸면 안 됩니다.");
        Assert.AreEqual("나중 요약", (await reports.GetSnapshotAsync("empty"))!.SummaryItems.Single().Text);

        Assert.IsFalse(await reports.AttachSummaryAsync("missing", [Summary("요약")]), "저장하지 않은 리포트에는 만들지 않습니다.");
        Assert.IsNull(await reports.GetSnapshotAsync("missing"));
    }
}

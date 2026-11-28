using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services.AI.Briefing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SummaryGroupResolverTests
{
    [TestMethod]
    public void Restore_UsesRecentFiftyAndKeepsDifferentConversationGroupsSeparate()
    {
        const string session = "session";
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var notifications = Enumerable.Range(0, 52).Select(index =>
            new NotificationRecord($"id-{index:D3}", "Slack", "사용자", "프로젝트", "프로젝트 자료",
                start.AddMinutes(index), false, SessionId: session)).ToArray();

        NotificationSummaryItem OldSummary(int offset, int count)
        {
            var chunk = notifications.Skip(offset).Take(count).ToArray();
            var id = LlamaReportSummaryProvider.ComputeDeterministicId("sum", session,
                SummaryGroupResolver.GroupKey(chunk[0]), chunk.Select(item => item.Id));
            return new NotificationSummaryItem(id, $"요약 {offset}", false, null) { AppName = "Slack" };
        }

        var summaries = new[] { OldSummary(2, 6), OldSummary(8, 6), OldSummary(33, 3), OldSummary(0, 2) };
        var restored = SummaryGroupResolver.Restore(summaries, session, notifications);

        Assert.IsFalse(string.IsNullOrWhiteSpace(restored[0].GroupId));
        Assert.AreEqual(restored[0].GroupId, restored[1].GroupId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(restored[2].GroupId));
        Assert.AreNotEqual(restored[0].GroupId, restored[2].GroupId);
        Assert.IsNull(restored[3].GroupId, "요약 생성 범위 밖의 오래된 알림은 복원하지 않습니다.");
    }
}

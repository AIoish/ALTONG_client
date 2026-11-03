using System.Globalization;
using Altong.Client.Data.Models;
using Altong.Client.Services.AI.Briefing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// ALTONG_ai tests/briefing/test_pipeline.py의 그룹화 사례를 그대로 옮겨, 같은 입력이면 같은 그룹이 나오는지 확인한다.
/// </summary>
[TestClass]
public sealed class BriefingGroupingTests
{
    private static NotificationRecord N(string id, string app = "Slack", string sender = "가상 사용자",
        string title = "테스트 서버 오류", string body = "테스트 서버 오류를 확인해 주세요.",
        string timestamp = "2026-09-13T18:05:00Z") =>
        new(id, app, sender, title, body,
            DateTime.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));

    private static List<string[]> Ids(params NotificationRecord[] notifications) =>
        BriefingGrouping.Group(notifications).Select(group => group.Select(n => n.Id).ToArray()).ToList();

    private static void AssertGroups(List<string[]> actual, params string[][] expected)
    {
        Assert.AreEqual(expected.Length, actual.Count, string.Join(" | ", actual.Select(g => string.Join(",", g))));
        foreach (var group in expected)
            Assert.IsTrue(actual.Any(a => a.ToHashSet().SetEquals(group)), $"그룹 {{{string.Join(",", group)}}} 없음");
    }

    [TestMethod]
    public void GroupingUsesAppSenderTimeSpanAndText()
    {
        var groups = Ids(
            N("n1"),
            N("n2", title: "테스트 서버 복구", body: "테스트 서버 오류 복구를 진행합니다.", timestamp: "2026-09-13T18:30:00Z"),
            N("n3", sender: "다른 가상 사용자"),
            N("n4", timestamp: "2026-09-13T19:05:00Z"),
            N("n5", title: "점심 메뉴 투표", body: "점심 메뉴를 선택해 주세요."));

        AssertGroups(groups, ["n1", "n2"], ["n3"], ["n4"], ["n5"]);
    }

    [TestMethod]
    public void AdjacentSequenceIdsGroupShortChatFragmentsAcrossHours()
    {
        var groups = Ids(
            N("chat_001", app: "KakaoTalk", sender: "가상 팀원", title: "프로젝트방", body: "내일 보자.", timestamp: "2026-09-13T18:59:00Z"),
            N("chat_002", app: "KakaoTalk", sender: "가상 팀원", title: "프로젝트방", body: "오후 세 시고.", timestamp: "2026-09-13T19:01:00Z"),
            N("chat_003", app: "KakaoTalk", sender: "가상 팀원", title: "프로젝트방", body: "B강의실로 와.", timestamp: "2026-09-13T19:03:00Z"));

        Assert.AreEqual(1, groups.Count);
        CollectionAssert.AreEqual(new[] { "chat_001", "chat_002", "chat_003" }, groups[0]);
    }

    [TestMethod]
    public void SharedTokensGroupAcrossHourBoundaryWithinThirtyMinutes()
    {
        var groups = Ids(
            N("notice_010", title: "프로젝트 공지", body: "프로젝트 공지를 올립니다.", timestamp: "2026-09-13T18:59:00Z"),
            N("notice_020", title: "프로젝트 자료", body: "프로젝트 자료를 공유합니다.", timestamp: "2026-09-13T19:05:00Z"));

        Assert.AreEqual(1, groups.Count);
    }

    [TestMethod]
    public void SharedTokensDoNotGroupBeyondThirtyMinuteSpan()
    {
        var groups = Ids(
            N("notice_010", title: "프로젝트 공지", body: "프로젝트 공지를 올립니다.", timestamp: "2026-09-13T18:00:00Z"),
            N("notice_020", title: "프로젝트 자료", body: "프로젝트 자료를 공유합니다.", timestamp: "2026-09-13T18:31:00Z"));

        Assert.AreEqual(2, groups.Count);
    }

    [DataTestMethod]
    [DataRow("chat_001", "chat_003", DisplayName = "non_adjacent")]
    [DataRow("chat_a_001", "chat_b_002", DisplayName = "different_prefix")]
    [DataRow("550e8400-e29b-41d4-a716-446655440001", "550e8400-e29b-41d4-a716-446655440002", DisplayName = "uuid")]
    public void SequenceGroupingRejectsUnreliableOrUnrelatedIds(string firstId, string secondId)
    {
        var groups = Ids(
            N(firstId, title: "첫번째", body: "내일 보자."),
            N(secondId, title: "둘째", body: "오후에 와.", timestamp: "2026-09-13T18:07:00Z"));

        Assert.AreEqual(2, groups.Count);
    }

    [TestMethod]
    public void SequenceGroupingRequiresCloseTimeSameSenderAndSameApp()
    {
        Assert.AreEqual(2, Ids(
            N("chat_001", title: "첫번째", body: "내일 보자."),
            N("chat_002", title: "둘째", body: "오후에 와.", timestamp: "2026-09-13T18:11:00Z")).Count, "time_gap");
        Assert.AreEqual(2, Ids(
            N("chat_001", title: "첫번째", body: "내일 보자."),
            N("chat_002", sender: "다른 가상 사용자", title: "둘째", body: "오후에 와.", timestamp: "2026-09-13T18:07:00Z")).Count, "different_sender");
        Assert.AreEqual(2, Ids(
            N("chat_001", title: "첫번째", body: "내일 보자."),
            N("chat_002", app: "KakaoTalk", title: "둘째", body: "오후에 와.", timestamp: "2026-09-13T18:07:00Z")).Count, "different_app");
    }

    [TestMethod]
    public void SequenceGroupingIsDisabledForSenderFallbackToApp()
    {
        var groups = Ids(
            N("chat_001", app: "KakaoTalk", sender: "KakaoTalk", title: "첫번째", body: "내일 보자."),
            N("chat_002", app: "KakaoTalk", sender: "KakaoTalk", title: "둘째", body: "오후에 와.", timestamp: "2026-09-13T18:07:00Z"));

        Assert.AreEqual(2, groups.Count);
    }

    [TestMethod]
    public void SequenceIdsDoNotMergeUnrelatedCalendarAppEvents()
    {
        var groups = Ids(
            N("noti_001", app: "Calendar", sender: "일정 알림봇", title: "치과 예약", body: "내일 오전 10시 치과 예약입니다."),
            N("noti_002", app: "Calendar", sender: "일정 알림봇", title: "프로젝트 회의", body: "내일 오후 3시 프로젝트 회의입니다.",
                timestamp: "2026-09-13T18:07:00Z"));

        Assert.AreEqual(2, groups.Count);
    }

    [TestMethod]
    public void EmptySenderFallsBackToAppNameForModelAndGrouping()
    {
        var notification = new NotificationRecord("1", "Outlook", null, "주간 보고", "보고서 공유", DateTime.UtcNow);

        Assert.AreEqual("Outlook", BriefingGrouping.ModelSender(notification));
        Assert.IsNull(BriefingGrouping.KakaoRoom(notification), "카카오톡이 아니면 title은 방 이름이 아니다.");
    }

    [DataTestMethod]
    [DataRow(7, "7")]
    [DataRow(8, "4,4")]
    [DataRow(12, "6,6")]
    [DataRow(15, "5,5,5")]
    [DataRow(20, "6,7,7")]
    public void SplitEvenlyUsesFewestChunksOfEqualSize(int count, string expected)
    {
        var chunks = BriefingGrouping.SplitEvenly(Enumerable.Range(0, count).ToList(), BriefingGrouping.MaxNotificationsPerChunk);

        Assert.AreEqual(expected, string.Join(",", chunks.Select(c => c.Count)));
        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToArray(), chunks.SelectMany(c => c).ToArray(), "시간 순서를 유지해야 한다.");
    }

    [TestMethod]
    public void SplitEvenlyKeepsSplittingUntilEveryChunkFits()
    {
        // 문맥에 3개까지만 들어간다고 가정: 6개 → 3 + 3.
        var chunks = BriefingGrouping.SplitEvenly(Enumerable.Range(0, 6).ToList(), 7, chunk => chunk.Count <= 3);
        Assert.AreEqual("3,3", string.Join(",", chunks.Select(c => c.Count)));

        // 하나도 들어가지 않으면 더 나눌 수 없으므로 한 개씩 둔다 (요약 단계에서 대체 요약).
        var single = BriefingGrouping.SplitEvenly(Enumerable.Range(0, 2).ToList(), 7, _ => false);
        Assert.AreEqual("1,1", string.Join(",", single.Select(c => c.Count)));
    }
}

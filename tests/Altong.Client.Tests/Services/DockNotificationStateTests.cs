using Altong.Client.Data.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class DockNotificationStateTests
{
    private static readonly DateTime Received = new(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);
    private static NotificationRecord Record(string id, int minute = 0, int urgency = 3,
        bool? passed = true, string? session = "current") => new(
        id, "KakaoTalk", "팀원", "회의 안내", "10분 뒤 회의를 시작합니다.", Received.AddMinutes(minute),
        passed, urgency, 4, "일정/회의", SessionId: session);

    [TestMethod]
    public void Receive_OnlyAcceptsUniquePassedRecordsForActiveSession()
    {
        var state = new DockNotificationState();
        Assert.IsFalse(state.Receive(Record("before")));
        state.BeginSession("current");
        Assert.IsFalse(state.Receive(Record("blocked", passed: false)));
        Assert.IsFalse(state.Receive(Record("normal", passed: null)));
        Assert.IsFalse(state.Receive(Record("old", session: "previous")));
        Assert.IsFalse(state.Receive(Record("missing", session: null)));
        Assert.IsTrue(state.Receive(Record("accepted")));
        Assert.IsFalse(state.Receive(Record("accepted")));
        Assert.AreEqual(1, state.UnreadCount);
        Assert.IsFalse(state.IsPanelOpen, "도착만으로 패널이 열리면 안 됩니다.");
        state.OpenPanel();
        Assert.AreEqual(1, state.UnreadCount, "목록 열기는 읽음 처리가 아닙니다.");
    }

    [TestMethod]
    public void MarkAllRead_PreservesDetailAndPin_AndLaterArrivalIsUnread()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.Receive(Record("older", 0));
        state.Receive(Record("newer", 1, 5));
        state.OpenPanel();
        state.TogglePin();
        state.OpenDetail("older");
        var selected = state.SelectedItem;
        state.MarkAllRead();
        Assert.AreEqual(0, state.UnreadCount);
        Assert.AreEqual(0, state.HighestUrgency);
        Assert.AreEqual(2, state.Items.Count);
        Assert.AreSame(selected, state.SelectedItem);
        Assert.IsTrue(state.IsPanelOpen && state.IsPinned);
        CollectionAssert.AreEqual(new[] { "newer", "older" }, state.Items.Select(item => item.Id).ToArray());
        state.Receive(Record("late-result", -1));
        Assert.AreEqual(1, state.UnreadCount);
        Assert.AreEqual("late-result", state.Items[0].Id);
    }

    [TestMethod]
    public void DeleteRead_LeavesUnreadAndPin_AndRejectsDeletedDuplicatesUntilNextSession()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.Receive(Record("read"));
        state.Receive(Record("unread", 1));
        state.OpenPanel();
        state.TogglePin();
        state.OpenDetail("read");
        state.DeleteRead();
        Assert.IsNull(state.SelectedItem);
        Assert.IsTrue(state.IsPanelOpen && state.IsPinned);
        Assert.AreEqual("unread", state.Items.Single().Id);
        Assert.AreEqual(1, state.UnreadCount);
        Assert.IsFalse(state.Receive(Record("read")));
        state.MarkAllRead();
        state.DeleteRead();
        Assert.AreEqual(0, state.Items.Count);
        state.DeleteRead();
        state.BeginSession("next");
        Assert.IsTrue(state.Receive(Record("read", session: "next")));
    }

    [TestMethod]
    public void OpenDetail_UpdatesCountImmediately_ButDefersReorderUntilDetailCloses()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.Receive(Record("older", 0, 3));
        state.Receive(Record("newer", 1, 5));
        state.OpenPanel();
        state.OpenDetail("newer");
        Assert.AreEqual(1, state.UnreadCount);
        Assert.AreEqual(3, state.HighestUrgency);
        Assert.IsTrue(state.SelectedItem!.IsRead);
        Assert.AreEqual("newer", state.Items[0].Id, "읽는 카드가 즉시 아래로 이동하면 안 됩니다.");
        state.CloseDetail();
        Assert.AreEqual("older", state.Items[0].Id);
        Assert.AreEqual("newer", state.Items[1].Id);
        state.OpenDetail("newer");
        Assert.AreEqual("10분 뒤 회의를 시작합니다.", state.SelectedItem!.Body);
        Assert.AreEqual(1, state.UnreadCount, "다시 열어도 개수가 추가 감소하면 안 됩니다.");
    }

    [TestMethod]
    public void SwitchDetail_FinalizesPreviousRead_AndNewArrivalsKeepTheSelectedContent()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.Receive(Record("a", 0));
        state.Receive(Record("b", 1));
        state.Receive(Record("c", 2));
        state.OpenPanel();
        state.OpenDetail("c");
        state.OpenDetail("b");
        CollectionAssert.AreEqual(new[] { "b", "a", "c" }, state.Items.Select(n => n.Id).ToArray());
        state.Receive(Record("d", 3, 5));
        Assert.AreEqual("b", state.SelectedItem!.Id);
        state.ClosePanel();
        CollectionAssert.AreEqual(new[] { "d", "a", "c", "b" }, state.Items.Select(n => n.Id).ToArray());
    }

    [TestMethod]
    public void Badge_CapsOnlyItsText_AndAllReadReturnsToBaseGreenWithoutGlow()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        for (int i = 0; i < 12; i++) state.Receive(Record($"n{i}", i, i == 0 ? 5 : 3));
        Assert.AreEqual("9+", state.BadgeText);
        Assert.AreEqual(12, state.Items.Count);
        Assert.AreEqual(5, state.HighestUrgency);
        state.OpenPanel();
        foreach (string id in state.Items.Select(n => n.Id).ToArray()) state.OpenDetail(id);
        Assert.AreEqual(0, state.UnreadCount);
        Assert.AreEqual(12, state.Items.Count, "읽은 알림은 세션 동안 다시 볼 수 있어야 합니다.");
        Assert.AreEqual(0, state.HighestUrgency);
        var appearance = DockNotificationAppearance.For(state.HighestUrgency);
        Assert.AreEqual(DockNotificationAppearance.BaseGreen, appearance.Color);
        Assert.AreEqual(0, appearance.GlowOpacity);
        Assert.IsTrue(DockNotificationAppearance.For(3).GlowOpacity > 0);
    }

    [TestMethod]
    public void PinnedDrag_HidesTemporarilyAndRestoresDetail_ThenSessionEndResetsEverything()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.Receive(Record("one"));
        state.OpenPanel();
        state.OpenDetail("one");
        state.TogglePin();
        state.BeginDrag();
        Assert.IsFalse(state.IsPanelOpen);
        Assert.IsTrue(state.IsPinned);
        state.OpenPanel();
        Assert.IsFalse(state.IsPanelOpen, "드래그 중에는 패널을 열지 않습니다.");
        state.EndDrag();
        Assert.IsTrue(state.IsPanelOpen);
        Assert.AreEqual("one", state.SelectedItem!.Id);
        state.EndSession();
        Assert.IsFalse(state.IsPinned);
        Assert.IsFalse(state.IsPanelOpen);
        Assert.IsNull(state.SelectedItem);
        Assert.AreEqual(0, state.Items.Count);
        Assert.IsFalse(state.Receive(Record("late")));
        state.BeginSession("next");
        Assert.IsFalse(state.Receive(Record("late")), "이전 세션 결과는 새 세션에서 거부합니다.");
        Assert.IsTrue(state.Receive(Record("next-record", session: "next")));
    }

    [TestMethod]
    public void UnpinnedDragAndExplicitClose_DoNotReopenOrRetainPin()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.OpenPanel();
        state.BeginDrag();
        state.EndDrag();
        Assert.IsFalse(state.IsPanelOpen);
        state.OpenPanel();
        state.TogglePin();
        state.ClosePanel();
        Assert.IsFalse(state.IsPinned);
        state.BeginDrag();
        state.EndDrag();
        Assert.IsFalse(state.IsPanelOpen);
    }

    [TestMethod]
    public void OutOfOrderResults_AreSortedByReceivedTime_AndUnreadAlwaysComesFirst()
    {
        var state = new DockNotificationState();
        state.BeginSession("current");
        state.Receive(Record("new", 5));
        state.OpenPanel();
        state.OpenDetail("new");
        state.CloseDetail();
        state.Receive(Record("old", 1));
        state.Receive(Record("middle", 3));
        CollectionAssert.AreEqual(new[] { "middle", "old", "new" }, state.Items.Select(n => n.Id).ToArray());
        state.BeginSession("current");
        Assert.AreEqual(3, state.Items.Count, "동일 세션의 중복 시작 신호는 목록을 지우지 않습니다.");
    }
}

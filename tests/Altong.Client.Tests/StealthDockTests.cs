using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class StealthDockBehaviorTests
{
    private static readonly StealthDockTiming Timing = new(
        IdleDelay: TimeSpan.FromSeconds(4),
        RevealDwell: TimeSpan.FromMilliseconds(250),
        AwayThreshold: TimeSpan.FromSeconds(30));

    private static readonly DateTime Start = new(2026, 10, 10, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>타이머처럼 1초 간격으로 판단을 반복한다.</summary>
    private static StealthDockMode Advance(StealthDockBehavior behavior, ref DateTime now, int seconds,
        int unread = 0, bool engaged = false, bool active = true)
    {
        var mode = behavior.Mode;
        for (int i = 0; i < seconds; i++)
        {
            now = now.AddSeconds(1);
            mode = behavior.Evaluate(new StealthDockInput(engaged, unread, active), now);
        }
        return mode;
    }

    [TestMethod]
    public void Reveal_StaysExpandedUntilIdleDelay_ThenRestsAsDimDot()
    {
        var behavior = new StealthDockBehavior(Timing);
        var now = Start;
        Assert.AreEqual(StealthDockMode.Resting, behavior.Evaluate(new(false, 0, true), now));
        behavior.Reveal(now);
        Assert.AreEqual(StealthDockMode.Expanded, Advance(behavior, ref now, 3));
        Assert.AreEqual(StealthDockMode.Resting, Advance(behavior, ref now, 2));
    }

    [TestMethod]
    public void Engagement_KeepsExpanded_AndIdleDelayStartsAfterPointerLeaves()
    {
        var behavior = new StealthDockBehavior(Timing);
        var now = Start;
        Assert.AreEqual(StealthDockMode.Expanded, Advance(behavior, ref now, 30, engaged: true));
        Assert.AreEqual(StealthDockMode.Expanded, Advance(behavior, ref now, 3));
        Assert.AreEqual(StealthDockMode.Resting, Advance(behavior, ref now, 2));
    }

    [TestMethod]
    public void NewUnread_StaysStealthed_FlashesOnce_AndLeavesSignalDot()
    {
        var behavior = new StealthDockBehavior(Timing);
        var now = Start;
        Advance(behavior, ref now, 3);
        Assert.IsFalse(behavior.ConsumeFlash());
        Assert.AreEqual(StealthDockMode.Alert, Advance(behavior, ref now, 1, unread: 1),
            "새 중요 알림이 와도 은신을 풀지 않고 신호색 점으로 남아야 합니다.");
        Assert.IsTrue(behavior.ConsumeFlash(), "도착 시 빛을 한 번 내야 합니다.");
        Assert.IsFalse(behavior.ConsumeFlash(), "빛은 한 번만 냅니다.");
        Assert.AreEqual(StealthDockMode.Alert, Advance(behavior, ref now, 600, unread: 1));
        Assert.IsFalse(behavior.ConsumeFlash(), "시간이 지나도 반복하지 않습니다.");
    }

    [TestMethod]
    public void ArrivalWhileAway_FlashesWhenUserComesBack()
    {
        var behavior = new StealthDockBehavior(Timing);
        var now = Start;
        Advance(behavior, ref now, 1, unread: 1, active: false);
        Advance(behavior, ref now, 600, unread: 1, active: false);
        Assert.IsFalse(behavior.ConsumeFlash(), "자리를 비운 동안에는 빛을 내지 않습니다.");
        Advance(behavior, ref now, 1, unread: 1);
        Assert.IsTrue(behavior.ConsumeFlash(), "돌아와 입력을 시작하면 한 번 빛을 냅니다.");
        Advance(behavior, ref now, 10, unread: 1);
        Assert.IsFalse(behavior.ConsumeFlash());
    }

    [TestMethod]
    public void ArrivalWhileAway_ReadElsewhere_DoesNotFlashLater()
    {
        var behavior = new StealthDockBehavior(Timing);
        var now = Start;
        Advance(behavior, ref now, 1, unread: 1, active: false);
        Advance(behavior, ref now, 1, unread: 0, active: false);
        Advance(behavior, ref now, 1);
        Assert.IsFalse(behavior.ConsumeFlash());
    }

    [TestMethod]
    public void AnotherArrival_FlashesAgain_AndReadingAllReturnsToRest()
    {
        var behavior = new StealthDockBehavior(Timing);
        var now = Start;
        Advance(behavior, ref now, 1, unread: 1);
        Assert.IsTrue(behavior.ConsumeFlash());
        Assert.AreEqual(StealthDockMode.Alert, Advance(behavior, ref now, 1, unread: 2));
        Assert.IsTrue(behavior.ConsumeFlash());
        Assert.AreEqual(StealthDockMode.Resting, Advance(behavior, ref now, 1, unread: 0),
            "모두 읽으면 흐린 점으로 돌아갑니다.");
    }
}

[TestClass]
public sealed class StealthDockPaletteTests
{
    [TestMethod]
    public void StatusColor_IsNeutralUntilUnread_ThenSignalByUrgency()
    {
        Assert.AreEqual(StealthDockPalette.Rest, StealthDockPalette.StatusColor(0, 0, expanded: false));
        Assert.AreEqual(StealthDockPalette.Quiet, StealthDockPalette.StatusColor(0, 0, expanded: true));
        Assert.AreEqual(StealthDockPalette.Signal, StealthDockPalette.StatusColor(2, 4, expanded: false));
        Assert.AreEqual(StealthDockPalette.Signal, StealthDockPalette.StatusColor(1, 3, expanded: true));
        Assert.AreEqual(StealthDockPalette.UrgentSignal, StealthDockPalette.StatusColor(1, 5, expanded: true));
    }
}

[TestClass]
public sealed class StealthDockPositioningTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1032);

    [TestMethod]
    public void Clamp_LetsCircleTouchEdgesButNeverLeaveWorkArea()
    {
        var topLeft = StealthDockPositioning.Clamp(new Point(-500, -500), WorkArea);
        Assert.AreEqual(StealthDockPositioning.CollapsedRadius, topLeft.X, "동그라미는 화면 끝에 닿을 수 있어야 합니다.");
        Assert.AreEqual(StealthDockPositioning.CollapsedRadius, topLeft.Y);
        var bottomRight = StealthDockPositioning.Clamp(new Point(5000, 5000), WorkArea);
        Assert.AreEqual(WorkArea.Right - StealthDockPositioning.CollapsedRadius, bottomRight.X);
        Assert.AreEqual(WorkArea.Bottom - StealthDockPositioning.CollapsedRadius, bottomRight.Y);
    }

    [TestMethod]
    public void DefaultAnchor_SitsAboveTaskbarAtRight_LikeHorizontalMiniBar()
    {
        var anchor = StealthDockPositioning.DefaultAnchor(WorkArea);
        Assert.AreEqual(WorkArea.Right - StealthDockPositioning.DefaultRightInset - StealthDockPositioning.RightReach, anchor.X);
        Assert.AreEqual(WorkArea.Bottom - StealthDockPositioning.HalfHeight - StealthDockPositioning.DefaultBottomGap, anchor.Y);
        var origin = StealthDockPositioning.CapsuleOrigin(anchor, 60, WorkArea);
        Assert.AreEqual(anchor.X - StealthDockPositioning.DotCenterX, origin.X, "처음 위치에서는 펼쳐도 상태 점이 제자리입니다.");
        Assert.AreEqual(anchor.Y - StealthDockPositioning.HalfHeight, origin.Y);
        Assert.AreEqual((true, true), StealthDockPositioning.Direction(anchor, WorkArea));
    }

    [TestMethod]
    public void CapsuleOrigin_PushesExpandedCapsuleInsideAtEdges()
    {
        var bounds = StealthDockPositioning.AnchorBounds(WorkArea);
        var rightEdge = StealthDockPositioning.CapsuleOrigin(new Point(bounds.Right, bounds.Bottom), 60, WorkArea);
        Assert.AreEqual(WorkArea.Right - 60, rightEdge.X, "오른쪽 끝에서 펼치면 캡슐이 화면 밖으로 나가지 않아야 합니다.");
        Assert.AreEqual(WorkArea.Bottom - StealthDockPositioning.HalfHeight * 2, rightEdge.Y);
        var leftEdge = StealthDockPositioning.CapsuleOrigin(new Point(bounds.Left, bounds.Top), 60, WorkArea);
        Assert.AreEqual(WorkArea.Left, leftEdge.X);
        Assert.AreEqual(WorkArea.Top, leftEdge.Y);
    }

    [TestMethod]
    public void Snap_PullsNearEdgesOnly()
    {
        var bounds = StealthDockPositioning.AnchorBounds(WorkArea);
        var nearLeftTop = StealthDockPositioning.Snap(new Point(bounds.Left + 10, bounds.Top + 12), WorkArea);
        Assert.AreEqual(new Point(bounds.Left, bounds.Top), nearLeftTop);
        var middle = new Point(900, 500);
        Assert.AreEqual(middle, StealthDockPositioning.Snap(middle, WorkArea));
        Assert.AreEqual((false, false), StealthDockPositioning.Direction(nearLeftTop, WorkArea));
    }

    [TestMethod]
    public void Ratio_RoundTripsAndFollowsResizedWorkArea()
    {
        var anchor = new Point(700, 300);
        var ratio = StealthDockPositioning.ToRatio(anchor, WorkArea);
        var back = StealthDockPositioning.FromRatio(ratio, WorkArea);
        Assert.AreEqual(anchor.X, back.X, .001);
        Assert.AreEqual(anchor.Y, back.Y, .001);

        // 작업표시줄 자동 숨김 등으로 작업 영역이 바뀌면 같은 비율 위치로 옮긴다.
        var bottomRight = StealthDockPositioning.AnchorBounds(WorkArea).BottomRight;
        var taller = new Rect(0, 0, 1920, 1080);
        var moved = StealthDockPositioning.FromRatio(StealthDockPositioning.ToRatio(bottomRight, WorkArea), taller);
        Assert.AreEqual(StealthDockPositioning.AnchorBounds(taller).BottomRight, moved);
    }

    [TestMethod]
    public void Store_SavesMonitorAndRatio_AndIgnoresInvalidFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "altong-stealth-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "position.json");
        try
        {
            var store = new StealthDockPositionStore(path);
            Assert.IsNull(store.Settings);
            store.Save(new StealthDockPositionSettings(@"\\.\DISPLAY2", .25, .75));
            var reloaded = new StealthDockPositionStore(path);
            Assert.AreEqual(new StealthDockPositionSettings(@"\\.\DISPLAY2", .25, .75), reloaded.Settings);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.Save(new(null, 1.5, 0)));

            File.WriteAllText(path, "{\"DeviceName\":null,\"RatioX\":2,\"RatioY\":0}");
            Assert.IsNull(new StealthDockPositionStore(path).Settings);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

[TestClass]
public sealed class StealthDockUiTests
{
    [TestMethod]
    public void SwitchingToStealth_PreservesSession_RevealsThenRestsAsDot()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var now = new DateTime(2026, 10, 10, 5, 0, 0, DateTimeKind.Utc);
            var dock = new NotificationDockWindow(state, TemporaryStore())
            {
                Clock = () => now,
                UserIdleProvider = () => TimeSpan.Zero,
                RoutineStatusProvider = () => new(FocusRoutinePhase.Focus),
            };
            int dashboardCount = 0;
            dock.ShowDashboardRequested += (_, _) => dashboardCount++;
            try
            {
                dock.Show();
                dock.BeginNotificationSession("stealth-test");
                state.OpenPanel();
                state.TogglePin();
                var flyout = dock.OwnedWindows.OfType<NotificationFlyoutWindow>().Single();
                var stealthButton = (Button)flyout.FindName("StealthDesignButton");
                stealthButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual(DockPresentation.Stealth, dock.Presentation);
                Assert.AreEqual("선택됨", System.Windows.Automation.AutomationProperties.GetItemStatus(stealthButton));
                Assert.AreEqual("선택 안 됨", System.Windows.Automation.AutomationProperties.GetItemStatus(
                    (Button)flyout.FindName("NotchDesignButton")));
                Assert.AreEqual("stealth-test", state.SessionId);
                Assert.IsTrue(state.IsPinned && state.IsPanelOpen);
                Assert.AreEqual(0, dashboardCount);
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)dock.FindName("StealthSurface")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("NotchSurface")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("DockSurface")).Visibility);
                AssertPanelBesideCapsule(dock, flyout);

                // 패널을 닫고 유휴 시간이 지나면 흐린 점으로 접히며 캡슐은 클릭을 받지 않는다.
                state.ClosePanel();
                for (int i = 0; i < 9; i++) { now = now.AddSeconds(1); dock.RefreshStealthState(); }
                Assert.AreEqual(StealthDockMode.Resting, dock.StealthMode);
                Assert.IsFalse(((FrameworkElement)dock.FindName("StealthCapsule")).IsHitTestVisible);
                Assert.IsTrue(((FrameworkElement)dock.FindName("StealthDot")).IsHitTestVisible);
                Assert.IsTrue(dock.IsVisible, "은신은 창을 숨기지 않고 작은 점을 남깁니다.");

                var workArea = SystemParameters.WorkArea;
                Assert.AreEqual(StealthDockPositioning.DefaultAnchor(workArea), dock.StealthAnchor,
                    "저장된 위치가 없으면 기존 가로 미니바처럼 오른쪽 아래에 둡니다.");
                // 창은 동그라미를 한가운데에 두고 통째로 움직인다(투명 부분은 화면 밖으로 나갈 수 있다).
                Assert.AreEqual(dock.StealthAnchor.X, dock.Left + dock.Width / 2, 1);
                Assert.AreEqual(dock.StealthAnchor.Y, dock.Top + dock.Height / 2, 1);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void ImportantArrival_StaysStealthed_AsSignalDot_WithCountReadyOnHover()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var now = new DateTime(2026, 10, 10, 5, 0, 0, DateTimeKind.Utc);
            var dock = new NotificationDockWindow(state, TemporaryStore()) { Clock = () => now, UserIdleProvider = () => TimeSpan.Zero };
            try
            {
                dock.SetPresentation(DockPresentation.Stealth);
                dock.Show();
                dock.BeginNotificationSession("stealth-test");
                for (int i = 0; i < 9; i++) { now = now.AddSeconds(1); dock.RefreshStealthState(); }
                Assert.AreEqual(StealthDockMode.Resting, dock.StealthMode);

                dock.ReceiveNotification(Record("urgent", 5));
                Assert.AreEqual(StealthDockMode.Alert, dock.StealthMode, "새 알림이 와도 은신을 풀지 않습니다.");
                Pump(TimeSpan.FromMilliseconds(1300));
                var body = (FrameworkElement)dock.FindName("StealthBody");
                Assert.AreEqual(14d, body.Width, .01, "미확인 알림이 남으면 평소보다 크게 남아야 합니다.");
                Assert.AreEqual(14d, body.Height, .01);
                AssertStatusDotOnAnchor(dock, 6);
                Assert.AreEqual(0d, ((FrameworkElement)dock.FindName("StealthFlash")).Opacity, .01,
                    "빛은 한 번 퍼진 뒤 완전히 사라져야 합니다.");
                Assert.IsFalse(((FrameworkElement)dock.FindName("StealthCapsule")).IsHitTestVisible);
                Assert.IsTrue(((FrameworkElement)dock.FindName("StealthDot")).IsHitTestVisible);
                var count = (TextBlock)dock.FindName("StealthCountText");
                Assert.AreEqual("1", count.Text);
                Assert.AreEqual(Visibility.Visible, count.Visibility);
                Assert.AreEqual("미확인 중요 알림 1개",
                    System.Windows.Automation.AutomationProperties.GetItemStatus((FrameworkElement)dock.FindName("StealthSurface")));

                for (int i = 0; i < 61; i++) { now = now.AddSeconds(1); dock.RefreshStealthState(); }
                Assert.AreEqual(StealthDockMode.Alert, dock.StealthMode);

                state.MarkAllRead();
                Assert.AreEqual(StealthDockMode.Resting, dock.StealthMode);
                Pump(TimeSpan.FromMilliseconds(400));
                Assert.AreEqual(12d, body.Width, .01);
                Assert.AreEqual(12d, body.Height, .01);
                AssertStatusDotOnAnchor(dock, 5);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void StealthReminder_ExpandsCapsule_WithArrowTipOnAnchor()
    {
        RunOnSta(() =>
        {
            var dock = new NotificationDockWindow(new DockNotificationState(), TemporaryStore()) { UserIdleProvider = () => TimeSpan.Zero };
            try
            {
                dock.SetPresentation(DockPresentation.Stealth);
                dock.ShowRoutineReminder("집중 시간이 시작됐어요.", () => "집중 · 179:59 남음", "집중에 필요한 작업을 시작해보세요.");
                dock.UpdateLayout();
                Assert.IsTrue(dock.IsReminderVisible);
                Assert.AreEqual(StealthDockMode.Expanded, dock.StealthMode);

                var arrow = (System.Windows.Shapes.Polygon)dock.FindName("ReminderArrow");
                var card = (FrameworkElement)dock.FindName("ReminderCard");
                var capsule = (FrameworkElement)dock.FindName("StealthCapsule");
                var tip = arrow.TranslatePoint(arrow.Points[2], dock);
                var capsuleTop = capsule.TranslatePoint(new Point(), dock);
                var cardBottom = card.TranslatePoint(new Point(0, card.ActualHeight), dock);
                Assert.AreEqual(dock.StealthAnchor.X - dock.Left, tip.X, 1);
                Assert.IsTrue(cardBottom.Y < tip.Y && tip.Y <= capsuleTop.Y + .5,
                    "기본 위치(화면 아래쪽)에서는 말풍선이 캡슐 위에 있어야 합니다.");
                Assert.AreEqual(dock.StealthAnchor.X - dock.Left,
                    capsuleTop.X + StealthDockPositioning.DotCenterX, .5, "펼친 캡슐의 상태 점은 접힌 점과 같은 자리입니다.");

                // 다른 디자인으로 돌아가면 기존 말풍선 배치를 그대로 쓴다.
                dock.SetPresentation(DockPresentation.RightNotch);
                Assert.AreEqual(System.Windows.HorizontalAlignment.Right, arrow.HorizontalAlignment);
                Assert.AreEqual(VerticalAlignment.Bottom, arrow.VerticalAlignment);
                Assert.AreEqual(new Thickness(0, 0, 38, 0), arrow.Margin);
            }
            finally { dock.Close(); }
        });
    }

    /// <summary>개발자 PC의 실제 저장 위치를 읽거나 덮어쓰지 않도록 존재하지 않는 임시 경로를 쓴다.</summary>
    private static StealthDockPositionStore TemporaryStore() => new(Path.Combine(
        Path.GetTempPath(), "altong-stealth-" + Guid.NewGuid().ToString("N"), "position.json"));

    private static NotificationRecord Record(string id, int urgency) => new(
        id, "KakaoTalk", "테스트 팀", "ALTONG 개발팀", "가상 알림입니다.", DateTime.UtcNow,
        true, urgency, 4, "업무", SessionId: "stealth-test");

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>접힌 점과 펼친 캡슐 모두 상태 점 중심이 같은 기준점에 있어야 한다.</summary>
    private static void AssertStatusDotOnAnchor(NotificationDockWindow dock, double size)
    {
        var dot = (FrameworkElement)dock.FindName("StealthStatusDot");
        Assert.AreEqual(size, dot.Width, .01);
        double centerX = Canvas.GetLeft(dot) + dot.Width / 2;
        double centerY = Canvas.GetTop(dot) + dot.Height / 2;
        Assert.AreEqual(dock.StealthAnchor.X - dock.Left, centerX, .5);
        Assert.AreEqual(dock.StealthAnchor.Y - dock.Top, centerY, .5);
    }

    private static void AssertPanelBesideCapsule(NotificationDockWindow dock, NotificationFlyoutWindow flyout)
    {
        dock.UpdateLayout();
        flyout.UpdateLayout();
        var capsule = (FrameworkElement)dock.FindName("StealthCapsule");
        var origin = capsule.TranslatePoint(new Point(), dock);
        double capsuleTop = dock.Top + origin.Y;
        double capsuleRight = dock.Left + origin.X + capsule.ActualWidth;
        var workArea = SystemParameters.WorkArea;
        Assert.IsTrue(capsule.IsHitTestVisible, "패널이 열려 있으면 캡슐로 펼쳐져 있어야 합니다.");
        Assert.IsTrue(flyout.IsVisible);
        Assert.IsTrue(flyout.Left >= workArea.Left - .01 && flyout.Left + flyout.Width <= workArea.Right + .01);
        Assert.IsTrue(flyout.Top >= workArea.Top - .01);
        if (dock.StealthAnchor.Y >= workArea.Top + workArea.Height / 2)
            Assert.AreEqual(capsuleTop, flyout.Top + flyout.Height, .01, "아래쪽에 있으면 패널이 위로 펼쳐집니다.");
        if (dock.StealthAnchor.X >= workArea.Left + workArea.Width / 2
            && capsuleRight + 8 - flyout.Width >= workArea.Left)
            Assert.AreEqual(capsuleRight + 8, flyout.Left + flyout.Width, .01, "오른쪽에 있으면 캡슐 오른쪽 끝에 맞춥니다.");
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "은신형 UI 검증이 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

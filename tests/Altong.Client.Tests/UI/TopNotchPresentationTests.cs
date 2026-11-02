using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Altong.Client.Data.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// 상단 노치 창. 테스트에서는 화면 맨 위 띠(AppBar)를 예약하지 않는다(<see cref="NotificationDockWindow.ReserveTopBand"/> 기본값).
/// 띠 등록·전체화면 숨김·창 밀어내기는 로컬에서 직접 확인한다.
/// </summary>
[TestClass]
public sealed class TopNotchPresentationTests
{
    [TestMethod]
    public void Panel_SelectsAndRequestsBandStyles()
    {
        RunOnSta(() =>
        {
            var panel = new NotificationPanel(new DockNotificationState());
            var styles = new List<TopBandStyle>();
            panel.BandStyleRequested += (_, value) => styles.Add(value);
            panel.SetBandStyle(TopBandStyle.Wallpaper);
            Assert.AreEqual("선택됨", System.Windows.Automation.AutomationProperties.GetItemStatus(
                (Button)panel.FindName("BandWallpaperButton")));
            Assert.AreEqual("선택 안 됨", System.Windows.Automation.AutomationProperties.GetItemStatus(
                (Button)panel.FindName("BandBlackButton")));
            Click(panel, "BandBlackButton");
            Click(panel, "BandWallpaperButton");
            CollectionAssert.AreEqual(new[] { TopBandStyle.Black, TopBandStyle.Wallpaper }, styles);
            Assert.IsNull(panel.FindName("StealthDesignButton"), "가로·기존·라벤더 디자인 버튼은 없앴습니다.");
        });
    }

    [TestMethod]
    public void Dock_StartsAsAClosedNotchInTheMiddle_WithWallpaperBand()
    {
        RunOnSta(() =>
        {
            var dock = new NotificationDockWindow(new DockNotificationState());
            try
            {
                Assert.IsFalse(dock.ReserveTopBand, "앱만 띠를 예약합니다.");
                Assert.AreEqual(TopBandStyle.Wallpaper, dock.BandStyle);
                Assert.AreEqual(TopNotchLayout.WindowLeftExtent + TopNotchLayout.WindowRightExtent, dock.Width);
                Assert.AreEqual(TopNotchLayout.WindowHeight, dock.Height);
                Assert.AreEqual(0d, dock.Top);
                // 노치 가운데가 화면 가운데에 온다.
                Assert.AreEqual(SystemParameters.PrimaryScreenWidth / 2, dock.Left + TopNotchLayout.WindowLeftExtent, .01);
                Assert.IsFalse(dock.IsPanelShown);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("PanelHost")).Visibility);

                dock.SetTopBandStyle(TopBandStyle.Black);
                Assert.AreEqual(TopBandStyle.Black, dock.BandStyle);
                Assert.AreEqual(TopBandStyle.Black, dock.Panel.BandStyle);
                Assert.IsFalse(dock.IsFullscreenSuppressed);
                Assert.AreEqual(1d, dock.Opacity);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void Dock_GrowsThePanelOutOfTheNotchAndBack_InOneWindow()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state);
            try
            {
                dock.Show();
                double closedLeft = dock.Left, closedWidth = dock.Width;
                dock.BeginNotificationSession("ui");
                dock.ReceiveNotification(Record("first", 5));
                state.OpenPanel();
                // 실제 마우스가 노치 밖에 있으면 0.3초 뒤 자동으로 닫히므로, 마우스 위치와 상관없게 고정해 둔다.
                state.TogglePin();
                Assert.IsTrue(dock.IsPanelShown, "패널을 열면 같은 창 안에 패널을 보인다.");
                Assert.AreEqual(0, dock.OwnedWindows.Count, "패널은 별도 창이 아니다.");
                PumpUntil(() => dock.TopNotchOpenProgress >= 1);
                Assert.AreEqual(1d, dock.TopNotchOpenProgress, .001);
                Assert.IsTrue(dock.Height > TopNotchLayout.WindowHeight, "패널을 담도록 창이 길어진다.");
                Assert.AreEqual(closedLeft, dock.Left, .01, "창은 아래로만 늘어나고 왼쪽 위는 그대로다.");
                Assert.AreEqual(closedWidth, dock.Width, .01);

                state.OpenDetail("first");
                Assert.AreEqual(closedLeft, dock.Left, .01, "상세가 열려도 창 위치·폭은 그대로다.");
                Assert.AreEqual(closedWidth, dock.Width, .01);

                state.ClosePanel();
                PumpUntil(() => !dock.IsPanelShown);
                Assert.IsFalse(dock.IsPanelShown, "패널이 노치 안으로 말려 들어간 뒤 숨긴다.");
                Assert.AreEqual(0d, dock.TopNotchOpenProgress, .001);
                Assert.AreEqual(closedWidth, dock.Width, .01);
                Assert.AreEqual(TopNotchLayout.WindowHeight, dock.Height, .01);
                Assert.AreEqual(closedLeft, dock.Left, .01);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void RoutineReminders_SkipFocusStartAndEndBubbles_ButKeepBreakBubble()
    {
        RunOnSta(() =>
        {
            var phase = FocusRoutinePhase.Focus;
            var now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
            var dock = new NotificationDockWindow(new DockNotificationState())
            {
                RoutineStatusProvider = () => new DockRoutineStatus(phase),
                Clock = () => now,
            };
            try
            {
                // 집중을 켜는 순간에는 말풍선 대신 노치가 나타나며 일렁임을 재생한다.
                dock.ShowRoutineReminder("집중 모드가 켜졌어요.", () => "집중 중", "가상 안내");
                Assert.IsTrue(dock.IsVisible);
                Assert.IsFalse(dock.IsReminderVisible);

                // 휴식 시작 안내는 노치 아래 말풍선으로 보인다.
                now = now.AddMinutes(25);
                phase = FocusRoutinePhase.Break;
                dock.ShowRoutineReminder("휴식 시간이 시작됐어요.", () => "휴식 중", "가상 안내");
                Assert.IsTrue(dock.IsReminderVisible);

                // 집중 종료 안내는 띄우지 않고, 이어서 숨기면 바로 사라진다.
                phase = FocusRoutinePhase.Idle;
                dock.ShowRoutineReminder("집중 모드가 종료됐어요.", () => "세션 종료", "가상 안내");
                Assert.IsFalse(dock.IsReminderVisible);
                dock.HideAfterCurrentReminder();
                Assert.IsFalse(dock.IsVisible);
            }
            finally { dock.Close(); }
        });
    }

    private static NotificationRecord Record(string id, int urgency) => new(
        id, "KakaoTalk", "민준", "배포 확인", "배포 전 설정 확인 부탁해요.",
        new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc), true, urgency, 4, "업무", SessionId: "ui");

    private static void Click(FrameworkElement root, string name) =>
        ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>애니메이션이 끝날 때까지(최대 2초) Dispatcher를 돌린다. 애니메이션 효과가 꺼진 환경에서는 바로 끝난다.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "상단 노치 UI 검증이 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

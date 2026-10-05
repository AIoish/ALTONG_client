using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Altong.Client.Data.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotchPresentationTests
{
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [TestMethod]
    public void SwitchingBetweenNotchAndClassic_PreservesSessionReadDetailAndPin()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state)
            {
                Left = -10000, Top = -10000,
                RoutineStatusProvider = () => new(FocusRoutinePhase.Focus),
            };
            int toggleFocusCount = 0, dashboardCount = 0;
            dock.ToggleFocusRequested += (_, _) => toggleFocusCount++;
            dock.ShowDashboardRequested += (_, _) => dashboardCount++;
            try
            {
                Assert.AreEqual(DockPresentation.RightNotch, dock.Presentation);
                dock.SetPresentation(DockPresentation.Classic);
                dock.Show();
                dock.BeginNotificationSession("notch-test");
                dock.ReceiveNotification(Record("first", 5, "민준", "배포 전 설정 확인 부탁해요."));
                dock.ReceiveNotification(Record("second", 4, "서연", "회의 자료를 공유했어요."));
                SavePreview(dock, "classic-mini-bar.png");
                state.OpenPanel();
                state.TogglePin();
                var flyout = dock.OwnedWindows.OfType<NotificationFlyoutWindow>().Single();
                MoveOffscreen(dock, flyout);
                SavePreview(flyout, "classic-expanded.png");
                state.OpenDetail("first");
                Assert.AreEqual(1, state.UnreadCount);

                var switchButton = (Button)flyout.FindName("NotchDesignButton");
                switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(DockPresentation.RightNotch, dock.Presentation);
                Assert.AreEqual(SystemParameters.WorkArea.Right - 24, dock.Left + dock.Width, .01);
                Assert.AreEqual(SystemParameters.WorkArea.Bottom, dock.Top + dock.Height, .01);
                Assert.AreEqual("notch-test", state.SessionId);
                Assert.IsTrue(state.IsPinned);
                Assert.IsTrue(state.IsPanelOpen);
                Assert.AreEqual("first", state.SelectedItem!.Id);
                Assert.AreEqual(1, state.UnreadCount);
                Assert.AreEqual("선택됨", System.Windows.Automation.AutomationProperties.GetItemStatus(switchButton));
                AssertPanelBesideNotch(dock, flyout);
                state.CloseDetail();
                AssertPanelBesideNotch(dock, flyout);
                state.OpenDetail("first");
                dock.Left -= 20;
                AssertPanelBesideNotch(dock, flyout);
                var workArea = SystemParameters.WorkArea;
                double originalLeft = dock.Left;
                dock.Left = workArea.Left - (dock.Width - 112);
                AssertPanelBesideNotch(dock, flyout);
                Assert.AreEqual(workArea.Left, flyout.Left, .01);
                dock.Left = originalLeft;
                MoveOffscreen(dock, flyout);
                dock.UpdateLayout();
                flyout.UpdateLayout();
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)dock.FindName("NotchSurface")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("DockSurface")).Visibility);
                SavePreview(flyout, "notch-detail.png");

                // 새로운 알림은 동일한 목록에 한 번만 추가되고 읽던 상세는 유지된다.
                dock.ReceiveNotification(Record("third", 3, "지훈", "검토가 끝나면 알려주세요."));
                Assert.AreEqual(3, state.Items.Count);
                Assert.AreEqual("first", state.SelectedItem!.Id);
                ((Button)flyout.FindName("RoutineActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1, toggleFocusCount);

                var classicButton = (Button)flyout.FindName("ClassicDesignButton");
                classicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(DockPresentation.Classic, dock.Presentation);
                Assert.AreEqual(136d, dock.Height);
                Assert.AreEqual(NotificationFlyoutWindow.ListWidth + NotificationFlyoutWindow.DetailWidth, flyout.Width);
                Assert.AreEqual("first", state.SelectedItem!.Id);
                Assert.IsTrue(state.IsPinned);
                Assert.AreEqual(2, state.UnreadCount);
                Assert.AreEqual("선택됨", System.Windows.Automation.AutomationProperties.GetItemStatus(classicButton));
                Assert.AreEqual("선택 안 됨", System.Windows.Automation.AutomationProperties.GetItemStatus(switchButton));
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)flyout.FindName("RoutineBand")).Visibility);
                Assert.AreEqual(0, dashboardCount, "디자인 전환으로 대시보드를 열거나 집중을 토글하면 안 됩니다.");
                dock.EndNotificationSession();
                Assert.AreEqual(0, state.Items.Count);
                Assert.IsFalse(flyout.IsVisible);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void Notch_ReceivesAfterSwitch_PreservesScrollAndFollowsRoutineState()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            DockRoutineStatus routine = new(FocusRoutinePhase.Focus);
            var dock = new NotificationDockWindow(state) { RoutineStatusProvider = () => routine };
            try
            {
                dock.SetPresentation(DockPresentation.RightNotch);
                dock.Left = dock.Top = -10000;
                dock.Show();
                dock.BeginNotificationSession("notch-test");
                SavePreview(dock, "notch-collapsed.png");
                state.OpenPanel();
                state.TogglePin();
                var flyout = dock.OwnedWindows.OfType<NotificationFlyoutWindow>().Single();
                MoveOffscreen(dock, flyout);
                SavePreview(flyout, "notch-empty.png");
                for (int i = 0; i < 12; i++)
                    dock.ReceiveNotification(Record($"row-{i}", i % 3 + 3, "ALTONG 테스트 팀", $"테스트 알림 {i + 1}입니다. 집중이 끝나면 확인해 주세요."));
                Assert.AreEqual("9+", ((TextBlock)dock.FindName("NotchUrgentCountText")).Text);
                MoveOffscreen(dock, flyout);
                SavePreview(dock, "notch-unread.png");
                SavePreview(flyout, "notch-expanded.png");
                var scroll = (ScrollViewer)flyout.FindName("ListScroll");
                scroll.ScrollToVerticalOffset(350);
                flyout.UpdateLayout();
                var items = (ItemsControl)flyout.FindName("NotificationItems");
                var anchorItem = state.Items[3];
                var anchor = (FrameworkElement)items.ItemContainerGenerator.ContainerFromItem(anchorItem);
                double oldY = anchor.TranslatePoint(new System.Windows.Point(), scroll).Y;
                dock.ReceiveNotification(Record("latest", 5, "새 알림", "읽던 위치는 유지되어야 합니다.") with { ReceivedAt = DateTime.UtcNow.AddMinutes(1) });
                flyout.UpdateLayout();
                double newY = anchor.TranslatePoint(new System.Windows.Point(), scroll).Y;
                Assert.AreEqual(oldY, newY, 1);

                routine = new(FocusRoutinePhase.Break);
                dock.ShowRoutineReminder("휴식 시간", () => "휴식 · 05:00", "잠시 쉬어가세요.");
                Assert.AreEqual("휴식 중", ((TextBlock)flyout.FindName("RoutinePhaseText")).Text);
                Assert.IsFalse(dock.IsReminderVisible, "읽는 중인 패널을 루틴 안내가 가리지 않아야 합니다.");
                state.BeginDrag();
                Assert.IsFalse(flyout.IsVisible);
                state.EndDrag();
                Assert.IsTrue(flyout.IsVisible);
                Assert.IsTrue(state.IsPinned);
                state.ClosePanel();
                Assert.IsTrue(dock.IsReminderVisible);
                dock.Hide();
                Assert.IsFalse(flyout.IsVisible);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void DefaultNotch_ReflectsIdleUnreadUrgentAndAllRead()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state);
            try
            {
                Assert.AreEqual(DockPresentation.RightNotch, dock.Presentation);
                dock.Left = dock.Top = -10000;
                dock.Show();
                state.BeginSession("notch-test");
                Assert.IsTrue(((FrameworkElement)dock.FindName("NotchSurface")).IsVisible);
                SavePreview(dock, "notch-idle.png");
                state.Receive(Record("normal", 3, "테스트 팀", "확인할 알림입니다."));
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)dock.FindName("NotchQuietDot")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("NotchUrgentIndicator")).Visibility);
                SavePreview(dock, "notch-normal.png");
                state.Receive(Record("important", 4, "테스트 팀", "중요 알림입니다."));
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("NotchUrgentIndicator")).Visibility);
                state.Receive(Record("urgent", 5, "테스트 팀", "긴급 알림입니다."));
                Assert.AreEqual("3", ((TextBlock)dock.FindName("NotchUrgentCountText")).Text);
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)dock.FindName("NotchUrgentIndicator")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("NotchQuietDot")).Visibility);
                SavePreview(dock, "notch-urgent.png");
                state.MarkAllRead();
                Assert.AreEqual(Visibility.Visible, ((FrameworkElement)dock.FindName("NotchQuietDot")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("NotchUrgentIndicator")).Visibility);
                Assert.AreEqual(0, state.UnreadCount);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void HorizontalReminder_SitsAboveMiniBar_WithArrowPointingDownToItsCenter()
    {
        RunOnSta(() =>
        {
            var dock = new NotificationDockWindow();
            try
            {
                dock.ShowRoutineReminder("집중 시간이 시작됐어요.", () => "집중 · 179:59 남음", "집중에 필요한 작업을 시작해보세요.");
                dock.Left = dock.Top = -10000;
                dock.UpdateLayout();
                var surface = (FrameworkElement)dock.FindName("NotchSurface");
                var card = (FrameworkElement)dock.FindName("ReminderCard");
                var arrow = (System.Windows.Shapes.Polygon)dock.FindName("ReminderArrow");
                var tip = arrow.TranslatePoint(arrow.Points[2], dock);
                var miniTop = surface.TranslatePoint(new System.Windows.Point(), dock);
                var cardBottom = card.TranslatePoint(new System.Windows.Point(0, card.ActualHeight), dock);
                Assert.AreEqual(miniTop.X + surface.ActualWidth / 2, tip.X, 1);
                Assert.IsTrue(cardBottom.Y < tip.Y && tip.Y < miniTop.Y);
                Assert.AreEqual(VerticalAlignment.Bottom, arrow.VerticalAlignment);
                SavePreview(dock, "horizontal-reminder.png");
                dock.SetPresentation(DockPresentation.Classic);
                Assert.AreEqual(VerticalAlignment.Center, arrow.VerticalAlignment);
                Assert.AreEqual(new System.Windows.Point(12, 9), arrow.Points[1]);
                Assert.AreEqual(1, Grid.GetColumnSpan((FrameworkElement)dock.FindName("ReminderBubble")));
            }
            finally { dock.Close(); }
        });
    }

    private static NotificationRecord Record(string id, int urgency, string sender, string body) => new(
        id, "KakaoTalk", sender, "ALTONG 개발팀", body, DateTime.UtcNow,
        true, urgency, 4, "업무", SessionId: "notch-test");

    private static void MoveOffscreen(NotificationDockWindow dock, NotificationFlyoutWindow flyout)
    {
        dock.Left = dock.Top = -10000;
        flyout.Left = flyout.Top = -10000;
    }

    private static void AssertPanelBesideNotch(NotificationDockWindow dock, NotificationFlyoutWindow flyout)
    {
        dock.UpdateLayout();
        flyout.UpdateLayout();
        var notch = (FrameworkElement)dock.FindName("NotchSurface");
        double notchLeft = dock.Left + notch.TranslatePoint(new System.Windows.Point(), dock).X;
        Assert.AreEqual(112d, notch.ActualWidth);
        Assert.AreEqual(24d, notch.ActualHeight);
        double notchTop = dock.Top + notch.TranslatePoint(new System.Windows.Point(), dock).Y;
        var workArea = SystemParameters.WorkArea;
        Assert.AreEqual(notchTop, flyout.Top + flyout.Height, .01);
        Assert.IsTrue(flyout.Left >= workArea.Left);
        Assert.IsTrue(flyout.Left + flyout.Width <= workArea.Right + .01);
        Assert.IsTrue(flyout.Top >= workArea.Top);
        Assert.IsTrue(notch.IsVisible);
        Assert.IsTrue(flyout.IsVisible);
    }

    private static void SavePreview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Pump(TimeSpan.FromMilliseconds(800));
        window.UpdateLayout();
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        // 검은 탭의 윤곽을 비교할 수 있도록 미리보기에서만 중립 배경을 깐다.
        if (window is NotificationDockWindow)
        {
            var backdrop = new DrawingVisual();
            using (var drawing = backdrop.RenderOpen())
                drawing.DrawRectangle(System.Windows.Media.Brushes.LightGray, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            bitmap.Render(backdrop);
        }
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "노치 UI 검증이 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

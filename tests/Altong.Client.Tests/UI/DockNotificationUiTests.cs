using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class DockNotificationUiTests
{
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [TestMethod]
    public void BulkButtons_UpdateCountsCloseDeletedDetailAndShowEmptyState()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            state.BeginSession("ui");
            state.Receive(Record("first", 5));
            state.Receive(Record("second", 4));
            state.OpenPanel();
            state.TogglePin();
            var (flyout, panel) = Host(state);
            state.Changed += (_, _) => panel.Refresh();
            try
            {
                flyout.Show();
                var markAll = (Button)panel.FindName("MarkAllReadButton");
                var deleteRead = (Button)panel.FindName("DeleteReadButton");
                Assert.IsTrue(markAll.IsEnabled);
                Assert.IsFalse(deleteRead.IsEnabled);
                state.OpenDetail("first");
                Assert.IsTrue(deleteRead.IsEnabled);
                deleteRead.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsNull(state.SelectedItem);
                Assert.AreEqual(Visibility.Collapsed, ((Border)panel.FindName("DetailPanel")).Visibility);
                Assert.AreEqual(1, state.UnreadCount);
                markAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual("미확인 0개 · 전체 1개", ((TextBlock)panel.FindName("CountText")).Text);
                Assert.IsFalse(markAll.IsEnabled);
                Assert.IsTrue(deleteRead.IsEnabled);
                SavePreview(flyout, "bulk-actions.png");
                deleteRead.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(0, state.Items.Count);
                Assert.AreEqual(Visibility.Visible, ((StackPanel)panel.FindName("EmptyState")).Visibility);
                Assert.IsFalse(markAll.IsEnabled || deleteRead.IsEnabled);
                Assert.IsTrue(state.IsPanelOpen && state.IsPinned);
                state.Receive(Record("new", 3));
                Assert.IsTrue(markAll.IsEnabled);
                Assert.AreEqual(Visibility.Collapsed, ((StackPanel)panel.FindName("EmptyState")).Visibility);
            }
            finally { flyout.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow("민준", false)]
    [DataRow(" 민준 ", false)]
    [DataRow("", false)]
    [DataRow("캡스톤 개발팀", true)]
    public void Flyout_HidesOnlyDuplicateOrEmptyTitles_InListAndDetail(string title, bool showTitle)
    {
        RunOnSta(() =>
        {
            var record = Record("name", 5) with
            {
                AppName = "KakaoTalk.exe", Sender = "민준", Title = title,
                Body = "지금 배포 오류 확인 부탁해요.\n확인되면 팀 채팅방에 알려주세요.",
            };
            var state = new DockNotificationState();
            state.BeginSession("ui");
            state.Receive(record);
            state.OpenPanel();
            var (flyout, panel) = Host(state);
            state.Changed += (_, _) => panel.Refresh();
            try
            {
                flyout.Show();
                flyout.UpdateLayout();
                var items = (ItemsControl)panel.FindName("NotificationItems");
                var presenter = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0);
                var cardTitle = Descendants<TextBlock>(presenter).Single(text => text.Name == "CardTitle");
                var expected = showTitle ? Visibility.Visible : Visibility.Collapsed;
                Assert.AreEqual(expected, cardTitle.Visibility);
                Assert.AreEqual(title, state.Items[0].Notification.Title, "수집 원문은 표시 정리로 바뀌면 안 됩니다.");
                Assert.AreEqual("KakaoTalk.exe", state.Items[0].AppName);
                Assert.AreEqual("카카오톡", state.Items[0].AppDisplayName);

                Descendants<Button>(presenter).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                flyout.UpdateLayout();
                Assert.AreEqual(expected, ((TextBlock)panel.FindName("DetailTitle")).Visibility);
                Assert.IsTrue(state.Items[0].IsRead);
                if (title == "민준")
                {
                    state.Receive(Record("group", 4) with
                    {
                        AppName = "KakaoTalk.exe", Sender = "서연", Title = "캡스톤 개발팀",
                        Body = "회의 자료 올려뒀어요. 시작 전에 확인해 주세요.", UrgencyScore = 4,
                    });
                    state.Receive(Record("work", 3) with
                    {
                        AppName = "Slack", Sender = "지훈 · 개발팀", Title = "배포 일정 확인",
                        Body = "오늘 배포는 오후 3시에 진행할 예정입니다.", UrgencyScore = 3,
                    });
                    SavePreview(flyout, "dock-notification-refined.png");
                }
            }
            finally { flyout.Close(); }
        });
    }

    [TestMethod]
    public void NotchLens_TurnsOrangeWhileUnread_AndResetsWithoutResizing()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state) { Left = -10000, Top = -10000 };
            try
            {
                dock.Show();
                double width = dock.Width, height = dock.Height;
                dock.BeginNotificationSession("ui");
                Assert.IsFalse(dock.IsLensAlert, "미확인 알림이 없으면 은빛 렌즈입니다.");
                for (int i = 0; i < 12; i++) dock.ReceiveNotification(Record($"n{i}", i));
                // 미확인 알림이 있으면 렌즈가 주황으로 커진다. 개수가 바뀌어도 창 크기는 그대로다.
                Assert.IsTrue(dock.IsLensAlert);
                Assert.AreEqual("미확인 중요 알림 12개",
                    System.Windows.Automation.AutomationProperties.GetItemStatus((FrameworkElement)dock.FindName("NotchBody")));
                Assert.AreEqual(width, dock.Width);
                Assert.AreEqual(height, dock.Height);
                state.MarkAllRead();
                Assert.IsFalse(dock.IsLensAlert, "모두 읽으면 은빛 렌즈로 돌아옵니다.");
                dock.EndNotificationSession();
                Assert.IsFalse(state.IsPanelOpen);
                Assert.AreEqual(0, dock.OwnedWindows.Count, "패널은 별도 창이 아닙니다.");
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void RoutineReminder_DoesNotDismissPinnedNotifications_AndResumeAfterPanelClose()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state) { Left = -10000, Top = -10000 };
            try
            {
                dock.Show();
                dock.BeginNotificationSession("ui");
                state.OpenPanel();
                state.TogglePin();
                dock.ShowRoutineReminder("휴식", () => "휴식 · 05:00", "잠깐 쉬어가세요.");
                // ShowRoutineReminder는 기존 위치 저장 방식으로 위치를 복원한다.
                dock.Left = dock.Top = -10000;
                Assert.IsTrue(state.IsPinned);
                Assert.IsTrue(state.IsPanelOpen);
                Assert.IsFalse(dock.IsReminderVisible);
                var dismissTimer = ReminderDismissTimer(dock);
                Assert.AreEqual(TimeSpan.FromMinutes(1), dismissTimer.Interval);
                Assert.IsFalse(dismissTimer.IsEnabled, "고정한 패널을 읽는 동안 말풍선의 표시 시간을 소모하지 않습니다.");
                state.ClosePanel();
                Assert.IsTrue(dock.IsReminderVisible);
                Assert.IsFalse(state.IsPinned);
                Assert.IsTrue(dismissTimer.IsEnabled, "보류한 말풍선이 실제 표시된 때부터 시간을 셉니다.");
                WaitForReminderDismissal(dismissTimer);
                Assert.IsFalse(dock.IsReminderVisible);
                Assert.IsTrue(state.IsActive);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void RoutineReminder_AutoDismisses_WithoutClearingNotifications_AndCanBeShownAgain()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state) { Left = -10000, Top = -10000 };
            var dismissTimer = ReminderDismissTimer(dock);
            try
            {
                dock.BeginNotificationSession("ui");
                dock.ReceiveNotification(Record("preserved", 1));
                Assert.AreEqual(TimeSpan.FromMinutes(1), dismissTimer.Interval);
                dock.ShowRoutineReminder("집중", () => "집중 진행 중", "가상 안내");
                dock.Left = dock.Top = -10000;
                WaitForReminderDismissal(dismissTimer);
                Assert.IsFalse(dock.IsReminderVisible);
                Assert.IsFalse(dismissTimer.IsEnabled);
                Assert.IsTrue(dock.IsVisible, "집중 중에는 말풍선만 닫고 미니바는 유지합니다.");
                Assert.AreEqual("ui", state.SessionId);
                Assert.AreEqual(1, state.Items.Count);
                Assert.AreEqual(1, state.UnreadCount);

                dock.ShowRoutineReminder("휴식", () => "휴식 진행 중", "새 안내");
                dock.Left = dock.Top = -10000;
                Assert.IsTrue(dock.IsReminderVisible);
                Assert.IsTrue(dismissTimer.IsEnabled, "이전 말풍선이 닫힌 뒤에도 새 안내의 타이머를 시작합니다.");
                Descendants<Button>((FrameworkElement)dock.FindName("ReminderBubble")).Single()
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsFalse(dock.IsReminderVisible);
                Assert.IsFalse(dismissTimer.IsEnabled, "수동 닫기 시에도 타이머를 정지합니다.");

                dock.ShowRoutineReminder("종료", () => "세션 종료", "종료 안내");
                dock.Left = dock.Top = -10000;
                dock.HideAfterCurrentReminder();
                WaitForReminderDismissal(dismissTimer);
                Assert.IsFalse(dock.IsVisible, "종료 안내가 닫히면 미니바도 기존 종료 처리에 따라 숨깁니다.");
                Assert.AreEqual(1, state.UnreadCount);

                dock.ShowRoutineReminder("새 집중", () => "집중 진행 중", "새 안내");
                dock.Left = dock.Top = -10000;
                Assert.IsTrue(dismissTimer.IsEnabled);
            }
            finally { dock.Close(); }
            Assert.IsFalse(dismissTimer.IsEnabled, "창이 닫힌 뒤 타이머가 남지 않아야 합니다.");
        });
    }

    [TestMethod]
    public void Flyout_ButtonsSharePin_DetailMarksRead_AndWindowDoesNotActivateOnClick()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            state.BeginSession("ui");
            state.Receive(Record("one", 1));
            var (flyout, panel) = Host(state);
            state.Changed += (_, _) => panel.Refresh();
            try
            {
                flyout.Show();
                state.OpenPanel();
                var items = (ItemsControl)panel.FindName("NotificationItems");
                flyout.UpdateLayout();
                var presenter = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0);
                var card = Descendants<Button>(presenter).Single();
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(state.Items[0].IsRead);
                Assert.AreEqual(0, state.UnreadCount);
                Assert.AreEqual(NotificationPanel.ListWidth + NotificationPanel.DetailWidth, panel.Width);
                Assert.AreEqual("one", state.SelectedItem!.Id);
                var listPin = (ToggleButton)panel.FindName("ListPinButton");
                var detailPin = (ToggleButton)panel.FindName("DetailPinButton");
                listPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.IsTrue(state.IsPinned);
                Assert.AreEqual(true, detailPin.IsChecked);
                detailPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.IsFalse(state.IsPinned);
                Assert.AreEqual(false, listPin.IsChecked);
                var dock = new NotificationDockWindow(state) { Left = -10000, Top = -10000 };
                try
                {
                    dock.Show();
                    nint response = SendMessage(new WindowInteropHelper(dock).Handle, 0x0021, 0, 0);
                    Assert.AreEqual((nint)3, response, "노치·패널 창은 클릭도 MA_NOACTIVATE로 처리합니다.");
                }
                finally { dock.Close(); }
            }
            finally { flyout.Close(); }
        });
    }

    [TestMethod]
    public void Flyout_NewArrivalPreservesVisibleCardAndDetail_AndRendersLongContent()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            state.BeginSession("ui");
            for (int i = 0; i < 14; i++) state.Receive(Record($"n{i}", i));
            state.OpenPanel();
            var (flyout, panel) = Host(state);
            state.Changed += (_, _) => panel.Refresh();
            try
            {
                flyout.Show();
                flyout.UpdateLayout();
                var scroll = (ScrollViewer)panel.FindName("ListScroll");
                var items = (ItemsControl)panel.FindName("NotificationItems");
                scroll.ScrollToVerticalOffset(450);
                flyout.UpdateLayout();
                var anchor = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(3);
                double before = anchor.TranslatePoint(new Point(), scroll).Y;
                panel.PreserveScrollPosition(() => state.Receive(Record("latest", 20)));
                flyout.UpdateLayout();
                double after = anchor.TranslatePoint(new Point(), scroll).Y;
                Assert.AreEqual(before, after, 1, "삽입 후 보이던 카드의 화면 위치를 유지합니다.");

                state.OpenDetail("n10");
                var detail = (ScrollViewer)panel.FindName("DetailScroll");
                flyout.UpdateLayout();
                detail.ScrollToVerticalOffset(80);
                flyout.UpdateLayout();
                double detailOffset = detail.VerticalOffset;
                panel.PreserveScrollPosition(() => state.Receive(Record("more", 21)));
                flyout.UpdateLayout();
                Assert.AreEqual("n10", state.SelectedItem!.Id);
                Assert.AreEqual(detailOffset, detail.VerticalOffset, 1);
                Assert.IsTrue(detail.ScrollableHeight > 0, "긴 본문은 상세 내부에서 스크롤합니다.");
                SavePreview(flyout, "dock-notification-detail.png");
                state.CloseDetail();
                flyout.UpdateLayout();
                SavePreview(flyout, "dock-notification-list.png");
            }
            finally { flyout.Close(); }
        });
    }

    private static NotificationRecord Record(string id, int minute) => new(
        id, "KakaoTalk", "김민수 · 개발팀", "[긴급] 배포 전 확인 부탁드립니다",
        string.Join("\n\n", Enumerable.Repeat("오늘 배포에 포함되는 알림 기능을 확인하고 있어요.\n미니바에서 내용을 확인한 뒤 의견을 알려주세요. 감사합니다.", 9)),
        new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc).AddMinutes(minute),
        true, minute % 3 + 3, 4, "업무", SessionId: "ui");

    /// <summary>알림 패널 내용을 화면 밖 창에 담아 띄운다. 실제 앱에서는 노치 창 안에 들어간다.</summary>
    private static (Window Host, NotificationPanel Panel) Host(DockNotificationState state)
    {
        var panel = new NotificationPanel(state);
        var host = new Window
        {
            Content = panel, Left = -10000, Top = -10000, Width = 720, Height = 480,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Black,
        };
        return (host, panel);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void SavePreview(Window window, string name)
    {
        // 시각 검증이 필요할 때만 임시 폴더로 렌더링한다. 기본 테스트는 파일을 만들지 않는다.
        var directory = Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        // 등장 애니메이션 이후의 실제 화면을 렌더링한다. 일반 테스트에는 대기하지 않는다.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static DispatcherTimer ReminderDismissTimer(NotificationDockWindow dock) =>
        (DispatcherTimer)typeof(NotificationDockWindow).GetField("_reminderDismissTimer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dock)!;

    private static void WaitForReminderDismissal(DispatcherTimer timer)
    {
        // 운영 값이 1분임은 별도로 확인하고, 실제 Dispatcher Tick은 짧은 간격으로 검증합니다.
        var frame = new DispatcherFrame();
        bool fired = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        EventHandler onDismiss = (_, _) => { fired = true; frame.Continue = false; };
        timeout.Tick += (_, _) => frame.Continue = false;
        timer.Tick += onDismiss;
        timer.Interval = TimeSpan.FromMilliseconds(50);
        try
        {
            timeout.Start();
            Dispatcher.PushFrame(frame);
            Assert.IsTrue(fired, "자동 닫기 타이머의 실제 Tick이 실행되어야 합니다.");
        }
        finally
        {
            timeout.Stop();
            timer.Tick -= onDismiss;
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "미니바 WPF 테스트가 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

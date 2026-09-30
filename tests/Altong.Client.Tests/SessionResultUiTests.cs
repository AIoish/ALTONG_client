using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SessionResultUiTests
{
    [TestMethod]
    public void Dashboard_JournalShowsOnlyTheCurrentActivity_WithoutDeletingCompletedData()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var database = SqliteDatabase.CreateInMemory();
                database.Initialize();
                var activity = new ActivitySessionService(new SqliteActivitySessionRepository(database));
                var windows = new SqliteWindowSessionRepository(database);
                var now = DateTime.UtcNow;
                var previous = activity.StartAsync(now.AddMinutes(-3)).GetAwaiter().GetResult();
                windows.InsertAsync(new WindowSessionRecord(0, "Previous.exe", "Previous",
                    now.AddMinutes(-2), now.AddMinutes(-1), 60, previous.ActivitySessionId))
                    .GetAwaiter().GetResult();
                activity.CompleteAsync(now.AddSeconds(-30)).GetAwaiter().GetResult();

                var results = new SessionResultsService(database, new SqliteFocusSessionRepository(database));
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var focusMode = new FocusModeService();
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, new FocusRoutineService(focusMode, settings),
                    results, tracker, focusModeService: focusMode, activitySession: activity);
                try
                {
                    RefreshDashboardData(window);
                    Assert.AreEqual("기록 대기 중", ((TextBlock)window.FindName("DataPeriodText")).Text);
                    Assert.AreEqual(0, ((ItemsControl)window.FindName("JournalItemsControl")).Items.Count);
                    Assert.AreEqual(0, ((ItemsControl)window.FindName("AppUsageItemsControl")).Items.Count);
                    Assert.AreEqual(1, windows.GetSessionsByDateRangeAsync(now.AddHours(-1), now)
                        .GetAwaiter().GetResult().Count, "완료된 활동의 DB 기록은 보존해야 합니다.");

                    var current = activity.StartAsync(now.AddSeconds(-20)).GetAwaiter().GetResult();
                    activity.SetFocusCaptureAsync(true, now.AddSeconds(-20)).GetAwaiter().GetResult();
                    windows.InsertAsync(new WindowSessionRecord(0, "Current.exe", "Current",
                        now.AddSeconds(-15), now.AddSeconds(-5), 10, current.ActivitySessionId))
                        .GetAwaiter().GetResult();
                    RefreshDashboardData(window);
                    var usage = ((ItemsControl)window.FindName("AppUsageItemsControl")).Items
                        .Cast<SessionAppUsage>().ToArray();
                    Assert.AreEqual(1, usage.Length);
                    Assert.AreEqual("Current.exe", usage[0].AppName);
                    activity.CompleteAsync(DateTime.UtcNow).GetAwaiter().GetResult();
                    Assert.AreEqual(0, ((ItemsControl)window.FindName("AppUsageItemsControl")).Items.Count,
                        "종료 즉시 이전 사용시간을 비워야 합니다.");
                    Assert.AreEqual(0, ((ItemsControl)window.FindName("JournalItemsControl")).Items.Count);
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF check timed out.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    [TestMethod]
    public void Dashboard_OpensNotificationsAndJournalFromHome()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var database = SqliteDatabase.CreateInMemory();
                database.Initialize();
                var results = new SessionResultsService(database, new SqliteFocusSessionRepository(database));
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var focusMode = new FocusModeService();
                var routine = new FocusRoutineService(focusMode, settings);
                var activity = new ActivitySessionService(new SqliteActivitySessionRepository(database));
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, routine, results, tracker,
                    focusModeService: focusMode, activitySession: activity,
                    startActivity: () => Task.CompletedTask, completeActivity: () => Task.CompletedTask);
                try
                {
                    Assert.IsTrue(System.Windows.Media.Fonts.GetFontFamilies(
                        new Uri("pack://application:,,,/Altong.Client;component/"), "./Fonts/")
                        .Any(family => family.FamilyNames.Values.Contains("Pretendard")),
                        "대시보드에 포함한 Pretendard 폰트 리소스를 찾을 수 있어야 합니다.");
                    window.ApplyTemplate();
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(1100, 720));
                    root.Arrange(new Rect(0, 0, 1100, 720));
                    root.UpdateLayout();
                    var tabs = (TabControl)window.FindName("DashboardTabs");
                    tabs.ApplyTemplate();
                    var home = tabs.SelectedContent;
                    var shortcuts = ((StackPanel)((ScrollViewer)home).Content).Children
                        .OfType<UniformGrid>().Single().Children.OfType<Button>().ToArray();
                    CollectionAssert.AreEqual(new[] { "알림 확인", "활동 일지", "설정" },
                        shortcuts.Select(AutomationProperties.GetName).ToArray());
                    for (int i = 0; i < shortcuts.Length; i++)
                    {
                        shortcuts[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(i + 1, tabs.SelectedIndex);
                    }
                    tabs.SelectedIndex = 0;
                    Assert.AreEqual(4, tabs.Items.Count);
                    Assert.AreEqual("알림 확인", ((TabItem)tabs.Items[1]).Header);
                    Assert.AreEqual("활동 일지", ((TabItem)tabs.Items[2]).Header);
                    Assert.IsNotNull(window.FindName("BlockedNotificationItemsControl"));
                    Assert.IsNotNull(window.FindName("NotificationItemsControl"));
                    Assert.IsNotNull(window.FindName("JournalItemsControl"));
                    Assert.IsNotNull(window.FindName("AppUsageItemsControl"));
                    foreach (int index in new[] { 1, 2, 3, 0 })
                    {
                        var navigation = new Button { Tag = index.ToString() };
                        typeof(DashboardWindow).GetMethod("Navigate_Click",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(window, new object[] { navigation, new RoutedEventArgs() });
                        Assert.AreEqual(index, tabs.SelectedIndex);
                        Assert.AreEqual(index == 0 ? Visibility.Collapsed : Visibility.Visible,
                            ((Button)window.FindName("BackButton")).Visibility);
                        root.UpdateLayout();
                        PumpBindings();
                        SavePreview(root, $"page-{index}.png", new Size(1100, 720));
                    }
                    Assert.AreSame(home, tabs.SelectedContent);
                    tabs.SelectedIndex = 0;
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        Assert.IsTrue(((Button)window.FindName("ActivityToggleButton")).ActualWidth > 0);
                        SavePreview(root, $"home-{size.Width}.png", size);
                    }
                    var homeScroll = (ScrollViewer)home;
                    var shortcutBounds = shortcuts[0].TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(shortcuts[0].RenderSize));
                    var focusButton = (Button)window.FindName("FocusModeToggleButton");
                    var focusButtonBounds = focusButton.TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(focusButton.RenderSize));
                    Assert.IsTrue(focusButtonBounds.Bottom <= homeScroll.ViewportHeight,
                        "최소 창 크기에서 집중모드 버튼도 첫 화면에 보여야 합니다.");
                    Assert.IsTrue(shortcutBounds.Top > focusButtonBounds.Bottom,
                        "바로가기는 타이머 아래에 배치되어야 합니다.");
                    homeScroll.ScrollToBottom();
                    root.UpdateLayout();
                    shortcutBounds = shortcuts[0].TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(shortcuts[0].RenderSize));
                    Assert.IsTrue(shortcutBounds.Bottom <= homeScroll.ViewportHeight + 1,
                        "최소 창 크기에서도 스크롤로 바로가기에 접근할 수 있어야 합니다.");
                    homeScroll.ScrollToTop();
                    root.UpdateLayout();

                    activity.StartAsync(DateTime.UtcNow.AddHours(-12)).GetAwaiter().GetResult();
                    focusMode.Start();
                    routine.Refresh();
                    typeof(DashboardWindow).GetMethod("UpdateClock",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, null);
                    root.UpdateLayout();
                    Assert.IsTrue(homeScroll.ScrollableWidth <= 1);
                    SavePreview(root, "home-recording-640.png", new Size(640, 520));
                    homeScroll.ScrollToBottom();
                    root.UpdateLayout();
                    SavePreview(root, "home-recording-bottom.png", new Size(640, 520));

                    var now = DateTime.UtcNow;
                    var longTitle = "분기 보고서 검토 회의 일정이 변경되었습니다. 새 일정과 참석자를 확인해 주세요. ";
                    var blocked = Enumerable.Range(0, 12).Select(i => new NotificationDisplayItem(
                        new NotificationRecord($"blocked-{i}", "Messenger", null, longTitle,
                            longTitle + longTitle, now.AddMinutes(-i), false))).ToArray();
                    var passed = Enumerable.Range(0, 12).Select(i => new NotificationDisplayItem(
                        new NotificationRecord($"passed-{i}", "Calendar", null, longTitle,
                            longTitle, now.AddMinutes(-i), true))).ToArray();
                    var entries = Enumerable.Range(0, 12).Select(i => new ActivityJournalEntry(
                        "Editor.exe", longTitle + longTitle, now.AddMinutes(-i - 2), now.AddMinutes(-i))).ToArray();
                    var usage = Enumerable.Range(0, 12).Select(i => new SessionAppUsage(
                        i % 2 == 0 ? "ApplicationFrameHost.exe" : "Editor.exe", (12 - i) * 80)
                        { Percentage = (12 - i) * 100.0 / 12 }).ToArray();

                    tabs.SelectedIndex = 1;
                    RefreshDashboardData(window);
                    ((ItemsControl)window.FindName("BlockedNotificationItemsControl")).ItemsSource = blocked;
                    ((ItemsControl)window.FindName("NotificationItemsControl")).ItemsSource = passed;
                    root.UpdateLayout();
                    var notificationsScroll = (ScrollViewer)tabs.SelectedContent;
                    Assert.IsTrue(notificationsScroll.ScrollableHeight > 0);
                    Assert.IsTrue(notificationsScroll.ScrollableWidth <= 1,
                        "알림이 많아도 최소 창 크기에서 가로 잘림이 없어야 합니다.");
                    SavePreview(root, "notifications-filled-top.png", new Size(640, 520));
                    notificationsScroll.ScrollToBottom();
                    root.UpdateLayout();
                    Assert.IsTrue(Math.Abs(notificationsScroll.VerticalOffset - notificationsScroll.ScrollableHeight) <= 1);
                    SavePreview(root, "notifications-filled-bottom.png", new Size(640, 520));

                    tabs.SelectedIndex = 2;
                    RefreshDashboardData(window);
                    ((ItemsControl)window.FindName("JournalItemsControl")).ItemsSource = entries;
                    ((ItemsControl)window.FindName("AppUsageItemsControl")).ItemsSource = usage;
                    root.UpdateLayout();
                    var journalScroll = (ScrollViewer)tabs.SelectedContent;
                    Assert.IsTrue(journalScroll.ScrollableHeight > 0);
                    Assert.IsTrue(journalScroll.ScrollableWidth <= 1,
                        "활동 일지와 앱별 사용시간이 최소 창 크기에서 가로로 잘리지 않아야 합니다.");
                    SavePreview(root, "journal-filled-top.png", new Size(640, 520));
                    journalScroll.ScrollToBottom();
                    root.UpdateLayout();
                    Assert.IsTrue(Math.Abs(journalScroll.VerticalOffset - journalScroll.ScrollableHeight) <= 1);
                    SavePreview(root, "journal-filled-bottom.png", new Size(640, 520));

                    tabs.SelectedIndex = 3;
                    PumpBindings();
                    var settingsScroll = (ScrollViewer)tabs.SelectedContent;
                    settingsScroll.ScrollToBottom();
                    root.UpdateLayout();
                    SavePreview(root, "settings-bottom.png", new Size(640, 520));

                    var report = new SessionReportWindow(new SessionResult(now.AddHours(-1), now,
                        blocked.Select(item => item.Record).ToArray(), usage, TimeSpan.FromMinutes(35), true));
                    try
                    {
                        report.ApplyTemplate();
                        Assert.AreEqual(WindowStyle.None, report.WindowStyle);
                        Assert.IsTrue(report.FontFamily.Source.Contains("#Pretendard"));
                        var reportRoot = (Border)report.Content;
                        var reportGrid = (Grid)reportRoot.Child;
                        foreach (var size in new[] { new Size(520, 420), new Size(760, 780) })
                        {
                            reportRoot.Measure(size);
                            reportRoot.Arrange(new Rect(size));
                            reportRoot.UpdateLayout();
                            var scroll = reportGrid.Children.OfType<ScrollViewer>().Single();
                            Assert.IsTrue(scroll.ViewportHeight > 0);
                            Assert.IsTrue(scroll.ScrollableWidth <= 1, "결과 리포트에 가로 잘림이 없어야 합니다.");
                            var closeButton = reportGrid.Children.OfType<Grid>().Single().Children
                                .OfType<StackPanel>().Single(panel => panel.HorizontalAlignment == HorizontalAlignment.Right)
                                .Children.OfType<Button>().Last();
                            Assert.AreEqual("창 닫기", AutomationProperties.GetName(closeButton));
                            SavePreview(reportRoot, $"report-{size.Width}.png", size);
                        }
                    }
                    finally { report.Close(); }
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF check timed out.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    private static void PumpBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void RefreshDashboardData(DashboardWindow window)
    {
        var refresh = (Task)typeof(DashboardWindow).GetMethod("RefreshDashboardDataAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        var frame = new DispatcherFrame();
        var loading = typeof(DashboardWindow).GetField("_dashboardDataLoading",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (refresh.IsCompleted && !(bool)loading.GetValue(window)!) frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        refresh.GetAwaiter().GetResult();
    }

    private static void SavePreview(FrameworkElement root, string filename, Size size)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, filename));
        encoder.Save(stream);
    }

    private sealed class StubActiveWindowTracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

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
    public void Dashboard_JournalShowsTodaysFocusSessions_WithoutMixingLegacyActivityData()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var database = SqliteDatabase.CreateInMemory();
                database.Initialize();
                var windows = new SqliteWindowSessionRepository(database);
                var focusSessions = new SqliteFocusSessionRepository(database);
                var now = DateTime.UtcNow;
                windows.InsertAsync(new WindowSessionRecord(0, "Previous.exe", "Previous",
                    now.AddMinutes(-2), now.AddMinutes(-1), 60, "legacy-activity"))
                    .GetAwaiter().GetResult();

                var results = new SessionResultsService(database, new SqliteFocusSessionRepository(database));
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var focusMode = new FocusModeService();
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, new FocusRoutineService(focusMode, settings),
                    results, tracker, focusModeService: focusMode);
                try
                {
                    RefreshDashboardData(window);
                    Assert.AreEqual("오늘 집중모드가 켜져 있던 동안의 앱 사용 기록입니다.", ((TextBlock)window.FindName("DataPeriodText")).Text);
                    Assert.AreEqual(0, ((ItemsControl)window.FindName("JournalItemsControl")).Items.Count);
                    Assert.AreEqual(0, ((ItemsControl)window.FindName("AppUsageItemsControl")).Items.Count);
                    Assert.AreEqual(1, windows.GetSessionsByDateRangeAsync(now.AddHours(-1), now)
                        .GetAwaiter().GetResult().Count, "이전 활동의 DB 기록은 보존해야 합니다.");

                    focusSessions.StartSessionAsync(new FocusSessionRecord("focus-ui", now.AddSeconds(-20)))
                        .GetAwaiter().GetResult();
                    windows.InsertAsync(new WindowSessionRecord(0, "Current.exe", "Current",
                        now.AddSeconds(-15), now.AddSeconds(-5), 10, "focus-ui"))
                        .GetAwaiter().GetResult();
                    windows.InsertAsync(new WindowSessionRecord(0, "Current.exe", "Current",
                        now.AddSeconds(-12), now.AddSeconds(-8), 4, "focus-ui"))
                        .GetAwaiter().GetResult();
                    RefreshDashboardData(window);
                    var usage = ((ItemsControl)window.FindName("AppUsageItemsControl")).Items
                        .Cast<SessionAppUsage>().ToArray();
                    Assert.AreEqual(1, usage.Length);
                    Assert.AreEqual("Current.exe", usage[0].AppName);
                    Assert.AreEqual(10d, usage[0].Seconds, "겹치는 저장 기록은 한 번만 계산합니다.");
                    var journal = ((ItemsControl)window.FindName("JournalItemsControl")).Items.Cast<ActivityJournalHour>().ToArray();
                    Assert.AreEqual(usage.Sum(app => app.Seconds), journal.Sum(hour => hour.Duration.TotalSeconds));
                    focusSessions.EndSessionAsync("focus-ui", DateTime.UtcNow, true)
                        .GetAwaiter().GetResult();
                    RefreshDashboardData(window);
                    Assert.AreEqual(1, ((ItemsControl)window.FindName("AppUsageItemsControl")).Items.Count,
                        "집중모드 종료 후에도 오늘의 기록은 남아야 합니다.");
                    tracker.CurrentContext = new CurrentContext("Live.exe", "Current", now, 120);
                    results.Begin(now.AddSeconds(-20), 30);
                    focusMode.Start();
                    RefreshDashboardData(window);
                    usage = ((ItemsControl)window.FindName("AppUsageItemsControl")).Items.Cast<SessionAppUsage>().ToArray();
                    Assert.AreEqual(20d, usage.Single(app => app.AppName == "Live.exe").Seconds,
                        "현재 창을 집중모드 전부터 사용했어도 세션 시작 이후의 시간만 계산합니다.");
                    journal = ((ItemsControl)window.FindName("JournalItemsControl")).Items.Cast<ActivityJournalHour>().ToArray();
                    Assert.AreEqual(usage.Sum(app => app.Seconds), journal.Sum(hour => hour.Duration.TotalSeconds));
                    ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 2;
                    RefreshDashboardData(window);
                    Assert.AreEqual("오늘의 집중 활동", ((TextBlock)window.FindName("PageTitleText")).Text);
                    var root = (FrameworkElement)window.Content;
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        SavePreview(root, $"journal-consistent-{size.Width}.png", size);
                    }
                    focusMode.Stop();
                    RefreshDashboardData(window);
                    Assert.AreEqual(1, ((ItemsControl)window.FindName("AppUsageItemsControl")).Items.Count,
                        "집중모드가 꺼지면 현재 창 스냅샷은 추가하지 않습니다.");
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
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, routine, results, tracker,
                    focusModeService: focusMode);
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
                    var shortcuts = new[] { "HomeNotificationsShortcut", "HomeJournalShortcut", "HomeSettingsShortcut", "HomeCalendarShortcut" }
                        .Select(name => (Button)window.FindName(name)).ToArray();
                    CollectionAssert.AreEqual(new[] { "알림 확인", "활동 일지", "설정", "캘린더" },
                        shortcuts.Select(AutomationProperties.GetName).ToArray());
                    for (int i = 0; i < shortcuts.Length; i++)
                    {
                        shortcuts[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(i + 1, tabs.SelectedIndex);
                    }
                    tabs.SelectedIndex = 0;
                    Assert.AreEqual(5, tabs.Items.Count);
                    Assert.AreEqual("알림 확인", ((TabItem)tabs.Items[1]).Header);
                    Assert.AreEqual("활동 일지", ((TabItem)tabs.Items[2]).Header);
                    Assert.AreEqual("캘린더", ((TabItem)tabs.Items[4]).Header);
                    var rail = (StackPanel)window.FindName("NavigationRail");
                    foreach (var button in rail.Children.OfType<Button>())
                    {
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(int.Parse((string)button.Tag), tabs.SelectedIndex);
                        Assert.AreEqual("선택됨", AutomationProperties.GetItemStatus(button));
                        Assert.AreEqual(1, rail.Children.OfType<Button>().Count(item =>
                            AutomationProperties.GetItemStatus(item) == "선택됨"));
                    }
                    tabs.SelectedIndex = 0;
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
                        foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                        {
                            root.Measure(size);
                            root.Arrange(new Rect(size));
                            root.UpdateLayout();
                            PumpBindings();
                            var pageScroll = (ScrollViewer)tabs.SelectedContent;
                            Assert.IsTrue(pageScroll.ScrollableWidth <= 1,
                                "기본 크기와 최소 크기에서 페이지가 가로로 잘리지 않아야 합니다.");
                            SavePreview(root, $"page-{index}-{size.Width}.png", size);
                        }
                    }
                    Assert.AreSame(home, tabs.SelectedContent);
                    tabs.SelectedIndex = 3;
                    var timerOption = (CheckBox)window.FindName("TimerEnabledCheckBox");
                    var timerOptions = (StackPanel)window.FindName("TimerOptionsPanel");
                    var focusInput = (TextBox)window.FindName("FocusSessionDurationSetting");
                    var restInput = (TextBox)window.FindName("BreakDurationSetting");
                    var saveTimer = (Button)window.FindName("SaveTimerSettingsButton");
                    void PreviewTimerSettings(string state)
                    {
                        foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                        {
                            root.Measure(size);
                            root.Arrange(new Rect(size));
                            root.UpdateLayout();
                            PumpBindings();
                            var viewport = (ScrollViewer)tabs.SelectedContent;
                            viewport.ScrollToTop();
                            root.UpdateLayout();
                            Assert.IsTrue(viewport.ScrollableWidth <= 1,
                                "타이머 설정은 기본·최소 창 크기에서 가로로 잘리지 않아야 합니다.");
                            SavePreview(root, $"settings-{state}-{size.Width}.png", size);
                            saveTimer.BringIntoView();
                            PumpBindings();
                            root.UpdateLayout();
                            var bounds = saveTimer.TransformToAncestor(viewport).TransformBounds(new Rect(saveTimer.RenderSize));
                            Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= viewport.ViewportHeight + 1,
                                "타이머 설정이 길어져도 루틴 저장 버튼까지 스크롤할 수 있어야 합니다.");
                            SavePreview(root, $"settings-{state}-save-{size.Width}.png", size);
                        }
                    }
                    Assert.AreEqual(false, timerOption.IsChecked);
                    Assert.AreEqual(Visibility.Collapsed, timerOptions.Visibility);
                    PreviewTimerSettings("disabled");
                    timerOption.IsChecked = true;
                    Assert.AreEqual(Visibility.Visible, timerOptions.Visibility);
                    focusInput.Text = "30";
                    restInput.Text = "";
                    saveTimer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(new FocusTimerSettings(30, 0, true), settings.Current,
                        "휴식시간을 비워두어도 집중 타이머 설정을 저장할 수 있어야 합니다.");
                    PreviewTimerSettings("focus-only");
                    restInput.Text = "5";
                    saveTimer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(new FocusTimerSettings(30, 5, true), settings.Current);
                    PreviewTimerSettings("routine");
                    restInput.Text = "99";
                    saveTimer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(new FocusTimerSettings(30, 5, true), settings.Current,
                        "잘못된 시간 입력은 저장한 설정을 덮어쓰지 않아야 합니다.");
                    PreviewTimerSettings("invalid");
                    timerOption.IsChecked = false;
                    focusInput.Text = "";
                    saveTimer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(new FocusTimerSettings(30, 5, false), settings.Current,
                        "타이머를 끌 때는 시간 입력을 요구하지 않아야 합니다.");
                    Assert.AreEqual(Visibility.Collapsed, timerOptions.Visibility);
                    tabs.SelectedIndex = 0;
                    Assert.IsNull(window.FindName("HomeApplicationText"));
                    Assert.IsNull(window.FindName("HomeTimerStateText"));
                    Assert.IsNull(window.FindName("HomeFocusStateText"));
                    Assert.IsNull(window.FindName("FocusModeStatusText"));
                    var focusStatus = (TextBlock)window.FindName("HomeFocusHintText");
                    var focusStatusBadge = (Border)window.FindName("HomeFocusStatusBadge");
                    var focusStatusDot = (System.Windows.Shapes.Ellipse)window.FindName("HomeFocusStatusDot");
                    Assert.AreEqual("집중모드 꺼짐", focusStatus.Text);
                    var offIndicatorColor = ((System.Windows.Media.SolidColorBrush)focusStatusDot.Fill).Color;
                    Assert.AreEqual("나만의 집중 루틴 설정", ((TextBlock)window.FindName("HomeRoutineTitleText")).Text);
                    Assert.AreEqual("\uE916", ((TextBlock)window.FindName("HomeRoutineIconText")).Text);
                    foreach (var size in new[] { new Size(1100, 780), new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        Assert.IsNull(window.FindName("ActivityToggleButton"));
                        var footer = (Border)window.FindName("FooterBar");
                        Assert.IsNull(window.FindName("FooterExitButton"));
                        Assert.IsFalse(((StackPanel)window.FindName("NavigationRail")).Children.OfType<TextBlock>()
                            .Any(text => text.Text == "\uE700"));
                        foreach (var name in new[] { "FooterHomeButton", "FooterUserIcon" })
                        {
                            var element = (FrameworkElement)window.FindName(name);
                            var bounds = element.TransformToAncestor(footer)
                                .TransformBounds(new Rect(element.RenderSize));
                            Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= footer.ActualHeight,
                                $"{name}: 하단 버튼과 사용자 아이콘이 풋터 밖으로 잘리지 않아야 합니다.");
                        }
                        SavePreview(root, $"home-{size.Width}.png", size);
                        var homeViewport = (ScrollViewer)home;
                        Assert.IsTrue(homeViewport.ScrollableWidth <= 1);
                        var sidePanel = (Grid)window.FindName("HomeSidePanel");
                        Assert.AreEqual(0, Grid.GetRow(sidePanel));
                        Assert.IsTrue(homeViewport.ScrollableHeight <= 1, "홈 전체가 세로 스크롤 없이 보여야 합니다.");
                        var homeLayout = (Grid)window.FindName("HomeLayout");
                        var mainPanel = (StackPanel)window.FindName("HomeMainPanel");
                        var mainBounds = mainPanel.TransformToAncestor(homeLayout)
                            .TransformBounds(new Rect(mainPanel.RenderSize));
                        if (homeViewport.ActualWidth >= 740)
                        {
                            var leftBottom = shortcuts[1].TransformToAncestor(homeLayout)
                                .TransformBounds(new Rect(shortcuts[1].RenderSize)).Bottom;
                            var rightBottom = shortcuts[2].TransformToAncestor(homeLayout)
                                .TransformBounds(new Rect(shortcuts[2].RenderSize)).Bottom;
                            Assert.IsTrue(mainBounds.Top > 0, "두 열 배치에서는 제거한 카드 자리의 상단 여백을 유지해야 합니다.");
                            Assert.IsTrue(Math.Abs(leftBottom - rightBottom) <= 1,
                                "왼쪽 알림·활동 일지와 오른쪽 루틴 카드의 하단을 맞춰야 합니다.");
                        }
                        else
                        {
                            Assert.IsTrue(Math.Abs(mainBounds.Top) <= 1,
                                "좁은 창의 세로 배치에서는 불필요한 상단 공백을 만들지 않아야 합니다.");
                        }
                        foreach (var shortcut in shortcuts)
                        {
                            shortcut.BringIntoView();
                            root.UpdateLayout();
                            var bounds = shortcut.TransformToAncestor(homeViewport)
                                .TransformBounds(new Rect(shortcut.RenderSize));
                            Assert.IsTrue(bounds.Left >= -1 && bounds.Right <= homeViewport.ViewportWidth + 1,
                                "가로 잘림 없이 모든 홈 메뉴를 표시해야 합니다.");
                            Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= homeViewport.ViewportHeight + 1,
                                "작은 창에서도 스크롤로 모든 홈 메뉴에 접근할 수 있어야 합니다.");
                        }
                        homeViewport.ScrollToTop();
                        root.UpdateLayout();
                    }
                    var homeScroll = (ScrollViewer)home;
                    var shortcutBounds = shortcuts[0].TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(shortcuts[0].RenderSize));
                    var focusButton = (Button)window.FindName("FocusModeToggleButton");
                    var focusButtonBounds = focusButton.TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(focusButton.RenderSize));
                    Assert.IsTrue(focusButtonBounds.Bottom <= homeScroll.ViewportHeight,
                        "최소 창 크기에서 집중모드 버튼도 첫 화면에 보여야 합니다.");
                    var statusBounds = focusStatusBadge.TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(focusStatusBadge.RenderSize));
                    Assert.IsTrue(statusBounds.Top >= 0 && statusBounds.Bottom <= focusButtonBounds.Top,
                        "현재 상태 표시는 최소 창 크기에서도 집중모드 버튼 바로 위에 보여야 합니다.");
                    Assert.IsTrue(shortcutBounds.Top > focusButtonBounds.Bottom,
                        "바로가기는 타이머 아래에 배치되어야 합니다.");
                    shortcuts[0].BringIntoView();
                    root.UpdateLayout();
                    shortcutBounds = shortcuts[0].TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(shortcuts[0].RenderSize));
                    Assert.IsTrue(shortcutBounds.Bottom <= homeScroll.ViewportHeight + 1,
                        "최소 창 크기에서도 스크롤로 바로가기에 접근할 수 있어야 합니다.");
                    homeScroll.ScrollToTop();
                    root.UpdateLayout();

                    focusMode.Start();
                    Assert.AreEqual("집중모드 켜짐", focusStatus.Text,
                        "실제 집중 상태가 바뀌면 안내 문구를 상태 표시로 갱신해야 합니다.");
                    Assert.AreNotEqual(offIndicatorColor, ((System.Windows.Media.SolidColorBrush)focusStatusDot.Fill).Color);
                    routine.Refresh();
                    typeof(DashboardWindow).GetMethod("UpdateClock",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, null);
                    root.UpdateLayout();
                    Assert.IsTrue(homeScroll.ScrollableWidth <= 1);
                    focusButtonBounds = focusButton.TransformToAncestor(homeScroll)
                        .TransformBounds(new Rect(focusButton.RenderSize));
                    Assert.IsTrue(focusButtonBounds.Bottom <= homeScroll.ViewportHeight + 1,
                        "집중모드 중에도 집중모드 버튼은 첫 화면에 보여야 합니다.");
                    SavePreview(root, "home-focus-640.png", new Size(640, 520));
                    homeScroll.ScrollToBottom();
                    root.UpdateLayout();
                    SavePreview(root, "home-focus-bottom.png", new Size(640, 520));
                    focusMode.Stop();
                    Assert.AreEqual("집중모드 꺼짐", focusStatus.Text);
                    Assert.AreEqual(offIndicatorColor, ((System.Windows.Media.SolidColorBrush)focusStatusDot.Fill).Color);
                    homeScroll.ScrollToTop();
                    root.UpdateLayout();
                    SavePreview(root, "home-focus-stopped-640.png", new Size(640, 520));

                    var now = DateTime.UtcNow;
                    var longTitle = "분기 보고서 검토 회의 일정이 변경되었습니다. 새 일정과 참석자를 확인해 주세요. ";
                    var blocked = Enumerable.Range(0, 12).Select(i => new NotificationHistoryItem(
                        new NotificationRecord($"blocked-{i}", "Messenger", null, longTitle,
                            longTitle + longTitle, now.AddMinutes(-i), false))).ToArray();
                    var passed = Enumerable.Range(0, 12).Select(i => new NotificationHistoryItem(
                        new NotificationRecord($"passed-{i}", "Calendar", null, longTitle,
                            longTitle, now.AddMinutes(-i), true))).ToArray();
                    var entries = Enumerable.Range(0, 12).SelectMany(i =>
                    {
                        var hour = DateTime.Today.AddHours(9 + i).ToUniversalTime();
                        return new[] {
                            new ActivityJournalEntry("Editor.exe", longTitle + longTitle, hour.AddMinutes(5), hour.AddMinutes(30)),
                            new ActivityJournalEntry("Browser.exe", "참고 문서", hour.AddMinutes(30), hour.AddMinutes(50)),
                            new ActivityJournalEntry("Editor.exe", "보고서 초안", hour.AddMinutes(54), hour.AddMinutes(59))
                        };
                    }).ToArray();
                    var usage = Enumerable.Range(0, 12).Select(i => new SessionAppUsage(
                        i % 2 == 0 ? "ApplicationFrameHost.exe" : "Editor.exe", (12 - i) * 80)
                        { Percentage = (12 - i) * 100.0 / 12 }).ToArray();

                    tabs.SelectedIndex = 1;
                    RefreshDashboardData(window);
                    var blockedGroups = NotificationAppGroup.Group(blocked, []);
                    var passedGroups = NotificationAppGroup.Group(passed, []);
                    foreach (var group in blockedGroups.Concat(passedGroups)) group.IsExpanded = true;
                    ((ItemsControl)window.FindName("BlockedNotificationItemsControl")).ItemsSource = blockedGroups;
                    ((ItemsControl)window.FindName("NotificationItemsControl")).ItemsSource = passedGroups;
                    ((TextBlock)window.FindName("BlockedNotificationCountText")).Text = blocked.Length.ToString();
                    ((TextBlock)window.FindName("PassedNotificationCountText")).Text = passed.Length.ToString();
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
                    var hourly = ActivityJournalHour.Group(entries);
                    ((ItemsControl)window.FindName("JournalItemsControl")).ItemsSource = hourly;
                    Assert.AreEqual(12, hourly.Count);
                    Assert.AreEqual("19시", hourly[^2].HourText);
                    Assert.AreEqual("20시", hourly[^1].HourText);
                    Assert.AreEqual(2, hourly[^1].Apps.Count);
                    ((ItemsControl)window.FindName("AppUsageItemsControl")).ItemsSource = usage;
                    ((TextBlock)window.FindName("ActivityJournalEmptyText")).Visibility = Visibility.Collapsed;
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        SavePreview(root, $"journal-hourly-{size.Width}.png", size);
                        var viewport = (ScrollViewer)tabs.SelectedContent;
                        var journal = (ItemsControl)window.FindName("JournalItemsControl");
                        var evening = (FrameworkElement)journal.ItemContainerGenerator.ContainerFromIndex(hourly.Count - 2);
                        var position = evening.TransformToAncestor(viewport).TransformBounds(new Rect(evening.RenderSize));
                        viewport.ScrollToVerticalOffset(viewport.VerticalOffset + position.Top);
                        root.UpdateLayout();
                        position = evening.TransformToAncestor(viewport).TransformBounds(new Rect(evening.RenderSize));
                        Assert.IsTrue(position.Top >= -1 && position.Bottom <= viewport.ViewportHeight + 1,
                            "기록이 많아도 스크롤로 19시 시간대의 모든 앱 기록을 볼 수 있어야 합니다.");
                        SavePreview(root, $"journal-evening-{size.Width}.png", size);
                        viewport.ScrollToTop();
                        root.UpdateLayout();
                    }
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
                        blocked.Concat(passed.Take(2)).Select(item => item.Record).ToArray(), usage,
                        IsFocusSessionReport: true) { SessionId = "ui-report-preview" }, results.Reports);
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
                            Assert.AreEqual(3, ((ItemsControl)report.FindName("QuickReplyItemsControl")).Items.Count);
                            foreach (var name in new[] { "SaveReportButton", "SkipSaveButton" })
                            {
                                var action = (Button)report.FindName(name);
                                Assert.AreEqual(Visibility.Visible, action.Visibility);
                                var bounds = action.TransformToAncestor(reportRoot).TransformBounds(new Rect(action.RenderSize));
                                Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= size.Width &&
                                    bounds.Top >= 0 && bounds.Bottom <= size.Height,
                                    "긴 리포트와 최소 창 크기에서도 저장·닫기 버튼은 화면 안에 보여야 합니다.");
                            }
                            var closeButton = reportGrid.Children.OfType<Grid>().Single().Children
                                .OfType<StackPanel>().Single(panel => panel.HorizontalAlignment == HorizontalAlignment.Right)
                                .Children.OfType<Button>().Last();
                            Assert.AreEqual("창 닫기", AutomationProperties.GetName(closeButton));
                            SavePreview(reportRoot, $"report-{size.Width}.png", size);
                            var replies = (ItemsControl)report.FindName("QuickReplyItemsControl");
                            var lastReply = replies.ItemContainerGenerator.ContainerFromIndex(replies.Items.Count - 1);
                            var copy = Descendants(lastReply).OfType<Button>().Single();
                            copy.BringIntoView();
                            PumpBindings();
                            reportRoot.UpdateLayout();
                            var copyBounds = copy.TransformToAncestor(scroll).TransformBounds(new Rect(copy.RenderSize));
                            Assert.IsTrue(copyBounds.Top >= -1 && copyBounds.Bottom <= scroll.ViewportHeight + 1,
                                "작은 창에서도 마지막 퀵 답변의 복사 버튼까지 스크롤할 수 있어야 합니다.");
                            SavePreview(reportRoot, $"report-replies-{size.Width}.png", size);
                            scroll.ScrollToBottom();
                            reportRoot.UpdateLayout();
                            Assert.IsTrue(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1,
                                "알림이 많아도 리포트 끝까지 스크롤할 수 있어야 합니다.");
                            SavePreview(reportRoot, $"report-bottom-{size.Width}.png", size);
                            scroll.ScrollToTop();
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

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
        public CurrentContext CurrentContext { get; set; } = CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

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
using Altong.Client.Services.Calendar;
using Altong.Client.Services.Focus;
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
                    Assert.AreEqual("오늘 · 집중 중 앱 사용 기록", ((TextBlock)window.FindName("DataPeriodText")).Text);
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
                    Assert.AreEqual("활동 일지", ((TextBlock)window.FindName("PageTitleText")).Text);
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
                string autoDndFile = Path.Combine(Path.GetTempPath(), "altong-accordion-" + Guid.NewGuid() + ".json");
                var autoDndSettings = new AutoDndSettingsStore(autoDndFile);
                var window = new DashboardWindow(settings, routine, results, tracker,
                    focusModeService: focusMode, autoDndSettings: autoDndSettings);
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
                    // 벤토 칸은 누르면 그 탭으로 간다(리포트가 없으면 [최근 리포트]는 캘린더로).
                    var tiles = new[] { "HomeNotificationsShortcut", "HomeJournalShortcut", "HomeStreakShortcut", "HomeCalendarShortcut", "HomeReportShortcut" }
                        .Select(name => (Button)window.FindName(name)).ToArray();
                    CollectionAssert.AreEqual(new[] { "집중 중 받은 알림", "오늘 집중", "연속 집중", "다음 일정", "최근 리포트" },
                        tiles.Select(AutomationProperties.GetName).ToArray());
                    var expectedTabs = new[] { 1, 2, 5, 4, 4 };
                    for (int i = 0; i < tiles.Length; i++)
                    {
                        tabs.SelectedIndex = 0;
                        tiles[i].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual(expectedTabs[i], tabs.SelectedIndex);
                    }
                    var shortcuts = new[] { "HomeNotificationsShortcut", "HomeCalendarShortcut", "HomeReportShortcut" }
                        .Select(name => (Button)window.FindName(name)).ToArray();
                    tabs.SelectedIndex = 0;
                    Assert.AreEqual(6, tabs.Items.Count);
                    Assert.AreEqual("알림 확인", ((TabItem)tabs.Items[1]).Header);
                    Assert.AreEqual("활동 일지", ((TabItem)tabs.Items[2]).Header);
                    Assert.AreEqual("캘린더", ((TabItem)tabs.Items[4]).Header);
                    Assert.AreEqual("인사이트", ((TabItem)tabs.Items[5]).Header);
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
                    Assert.IsInstanceOfType<DashboardNotificationsView>(((ContentControl)window.FindName("NotificationsHost")).Content);
                    Assert.IsNotNull(window.FindName("JournalItemsControl"));
                    Assert.IsNotNull(window.FindName("AppUsageItemsControl"));
                    foreach (int index in new[] { 1, 2, 3, 0 })
                    {
                        var navigation = new Button { Tag = index.ToString() };
                        typeof(DashboardWindow).GetMethod("Navigate_Click",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(window, new object[] { navigation, new RoutedEventArgs() });
                        Assert.AreEqual(index, tabs.SelectedIndex);
                        Assert.IsNull(window.FindName("BackButton"), "도안대로 탭 머리에는 뒤로 가기 버튼이 없습니다.");
                        Assert.AreEqual(index == 0 ? Visibility.Collapsed : Visibility.Visible,
                            ((Button)window.FindName("PageMenuButton")).Visibility, "홈 밖의 탭에는 ⋯ 메뉴가 있습니다.");
                        foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                        {
                            root.Measure(size);
                            root.Arrange(new Rect(size));
                            root.UpdateLayout();
                            PumpBindings();
                            if (tabs.SelectedContent is ScrollViewer pageScroll)
                                Assert.IsTrue(pageScroll.ScrollableWidth <= 1,
                                    "기본 크기와 최소 크기에서 페이지가 가로로 잘리지 않아야 합니다.");
                            SavePreview(root, $"page-{index}-{size.Width}.png", size);
                        }
                    }
                    Assert.AreSame(home, tabs.SelectedContent);
                    tabs.SelectedIndex = 3;
                    foreach (var removed in new[] { "SettingsDetailPanel", "TimerSettingsExpander", "AutoDndSettingsExpander", "SystemSettingsExpander", "NotificationSettingsExpander", "SaveTimerSettingsButton" })
                        Assert.IsNull(window.FindName(removed), $"세부 설정 화면은 없앴습니다: {removed}");
                    var timerCard = (Button)window.FindName("QuickTimerCard");
                    var autoCard = (Button)window.FindName("QuickAutoDndCard");
                    Assert.AreEqual("타이머 꺼짐", AutomationProperties.GetItemStatus(timerCard));
                    Assert.AreEqual("설정 켜짐", AutomationProperties.GetItemStatus(autoCard));
                    Assert.AreEqual("자동 실행 확인 불가", AutomationProperties.GetItemStatus((Button)window.FindName("QuickStartupCard")));
                    Assert.AreEqual("연결 안 됨", AutomationProperties.GetItemStatus((Border)window.FindName("BlockedAppsPane")));

                    // 자동 집중 카드는 누르는 즉시 저장한다.
                    autoCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsFalse(autoDndSettings.Current.Enabled);
                    Assert.AreEqual("꺼짐", AutomationProperties.GetItemStatus(autoCard));
                    autoCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(autoDndSettings.Current.Enabled);
                    Assert.AreEqual("설정 켜짐", AutomationProperties.GetItemStatus(autoCard));

                    // 타이머가 꺼져 있으면 루틴 입력은 숨고, 켜면 바로 보인다.
                    var routineOptions = (FrameworkElement)window.FindName("RoutineOptions");
                    Assert.AreEqual(Visibility.Collapsed, routineOptions.Visibility);
                    timerCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(settings.Current.TimerEnabled);
                    Assert.AreEqual(Visibility.Visible, routineOptions.Visibility);

                    // 루틴 고르기·직접 입력은 저장 버튼 없이 바로 저장한다. 잘못된 값은 저장하지 않는다.
                    var presets = (UniformGrid)window.FindName("RoutinePresetGrid");
                    ((Button)presets.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(new FocusTimerSettings(50, 10, true), settings.Current);
                    Assert.AreEqual("선택됨", AutomationProperties.GetItemStatus(presets.Children[1]));
                    var focusInput = (TextBox)window.FindName("RoutineFocusInput");
                    var restInput = (TextBox)window.FindName("RoutineBreakInput");
                    focusInput.Text = "30";
                    restInput.Text = "";
                    window.CommitRoutineInputs();
                    Assert.AreEqual(new FocusTimerSettings(30, 0, true), settings.Current, "휴식을 비우면 휴식 없이 저장합니다.");
                    Assert.AreEqual("집중 30분 · 휴식 없음", AutomationProperties.GetItemStatus(timerCard));
                    restInput.Text = "99";
                    window.CommitRoutineInputs();
                    Assert.AreEqual(new FocusTimerSettings(30, 0, true), settings.Current, "잘못된 입력은 저장한 설정을 덮어쓰지 않습니다.");
                    StringAssert.Contains(((TextBlock)window.FindName("RoutineFeedbackText")).Text, "입력해 주세요");
                    restInput.Text = "0";
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        PumpBindings();
                        var viewport = (ScrollViewer)tabs.SelectedContent;
                        Assert.IsTrue(viewport.ScrollableWidth <= 1, "설정은 기본·최소 창 크기에서 가로로 잘리지 않아야 합니다.");
                        SavePreview(root, $"settings-routine-{size.Width}.png", size);
                    }
                    timerCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(new FocusTimerSettings(30, 0, false), settings.Current, "타이머를 꺼도 루틴 값은 남깁니다.");
                    Assert.AreEqual(Visibility.Collapsed, routineOptions.Visibility);
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
                    Assert.IsNotNull(window.FindName("HomeJournalShortcut"), "홈에는 오늘 집중 칸이 있어야 합니다.");
                    foreach (var size in new[] { new Size(1100, 780), new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        Assert.IsNull(window.FindName("ActivityToggleButton"));
                        Assert.IsNull(window.FindName("FooterBar"));
                        Assert.IsNull(window.FindName("FooterExitButton"));
                        Assert.IsFalse(((StackPanel)window.FindName("NavigationRail")).Children.OfType<TextBlock>()
                            .Any(text => text.Text == "\uE700"));
                        foreach (var name in new[] { "FooterHomeButton", "FooterUserIcon" })
                            Assert.IsNull(window.FindName(name));
                        Assert.IsFalse(Descendants(root).OfType<TextBlock>().Any(text => text.Text == "User"));
                        var brand = (TextBlock)window.FindName("DashboardBrandText");
                        Assert.AreEqual("Altong", brand.Text);
                        Assert.IsTrue(brand.ActualWidth > 0 && brand.ActualHeight > 0);
                        SavePreview(root, $"home-{size.Width}.png", size);
                        var homeViewport = (ScrollViewer)home;
                        Assert.IsTrue(homeViewport.ScrollableWidth <= 1);
                        if (homeViewport.ActualWidth >= 740) Assert.IsTrue(homeViewport.ScrollableHeight <= 1, "넓은 홈은 한 화면에 표시합니다.");
                        var homeLayout = (Grid)window.FindName("HomeLayout");
                        var rowBounds = shortcuts.Select(tile => tile.TransformToAncestor(homeLayout).TransformBounds(new Rect(tile.RenderSize))).ToArray();
                        var sameRow = homeViewport.ActualWidth >= 600 ? rowBounds : rowBounds.Skip(1).ToArray();
                        Assert.IsTrue(sameRow.All(tile => Math.Abs(tile.Top - sameRow[0].Top) <= 1 && Math.Abs(tile.Bottom - sameRow[0].Bottom) <= 1),
                            "아래 줄 칸은 같은 높이로 나란히 둡니다(좁으면 알림 칸 아래에 일정·리포트가 나란히).");
                        var focusCard = (FrameworkElement)window.FindName("HomeFocusCard");
                        var flowCard = (FrameworkElement)window.FindName("HomeJournalShortcut");
                        var focusBounds = focusCard.TransformToAncestor(homeLayout).TransformBounds(new Rect(focusCard.RenderSize));
                        var flowBounds = flowCard.TransformToAncestor(homeLayout).TransformBounds(new Rect(flowCard.RenderSize));
                        if (homeViewport.ActualWidth >= 600)
                            Assert.IsTrue(Math.Abs(focusBounds.Top - flowBounds.Top) <= 1 && flowBounds.Left > focusBounds.Right,
                                "넓은 홈에서는 타이머 옆에 오늘의 집중 시간을 둡니다.");
                        else
                            Assert.IsTrue(flowBounds.Top >= focusBounds.Bottom, "좁은 홈에서는 오늘의 집중 시간을 타이머 아래로 내립니다.");
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
                    var rawNotifications = new SqliteNotificationRepository(database);
                    foreach (var item in blocked.Concat(passed)) rawNotifications.InsertAsync(item.Record).GetAwaiter().GetResult();
                    RefreshDashboardData(window);
                    root.UpdateLayout();
                    var notificationsView = (DashboardNotificationsView)((ContentControl)window.FindName("NotificationsHost")).Content;
                    var notificationsList = (ListBox)notificationsView.FindName("NotificationsList");
                    var notificationsScroll = Descendants(notificationsList).OfType<ScrollViewer>().Single();
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
                    window.ShowJournalDetails(true);
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
                        Assert.AreSame(window.FontFamily, report.FontFamily);
                        Assert.IsTrue(new System.Windows.Media.Typeface(report.FontFamily, FontStyles.Normal,
                            FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var reportFont));
                        StringAssert.Contains(reportFont.FontUri.ToString().ToLowerInvariant(), "pretendard-regular.otf");
                        Assert.AreEqual(600, report.Width);
                        Assert.AreEqual(660, report.Height);
                        Assert.AreEqual("집중 완료 · 집중 리포트", report.Title);
                        Assert.AreEqual("집중 리포트", ((TextBlock)report.FindName("ReportHeadingText")).Text);
                        Assert.AreEqual("집중 완료", ((TextBlock)report.FindName("ReportBadgeText")).Text);
                        Assert.IsFalse(((Expander)report.FindName("AppUsageExpander")).IsExpanded);
                        Assert.IsFalse(((Expander)report.FindName("NotificationsExpander")).IsExpanded);
                        var surface = (System.Windows.Media.SolidColorBrush)((Border)report.Content).Background;
                        var dashboardSurface = (System.Windows.Media.SolidColorBrush)((Border)window.Content).Background;
                        Assert.IsTrue(surface.Color.R <= dashboardSurface.Color.R,
                            "리포트는 대시보드와 같거나 더 깊은 흑연 바탕을 씁니다.");
                        Assert.IsTrue(surface.Color.R >= surface.Color.B && surface.Color.R - surface.Color.B <= 16,
                            "리포트 바탕도 채도 낮은 따뜻한 흑연입니다.");
                        var reportRoot = (Border)report.Content;
                        var reportGrid = (Grid)reportRoot.Child;
                        foreach (var size in new[] { new Size(480, 380), new Size(600, 660) })
                        {
                            reportRoot.Measure(size);
                            reportRoot.Arrange(new Rect(size));
                            reportRoot.UpdateLayout();
                            var scroll = reportGrid.Children.OfType<ScrollViewer>().Single();
                            Assert.IsTrue(scroll.ViewportHeight > 0);
                            Assert.IsTrue(scroll.ScrollableWidth <= 1, "결과 리포트에 가로 잘림이 없어야 합니다.");
                            var notifications = (ItemsControl)report.FindName("NotificationItemsControl");
                            foreach (var name in new[] { "SaveReportButton", "SkipSaveButton" })
                            {
                                var action = (Button)report.FindName(name);
                                Assert.AreEqual(Visibility.Visible, action.Visibility);
                                var bounds = action.TransformToAncestor(reportRoot).TransformBounds(new Rect(action.RenderSize));
                                Assert.IsTrue(bounds.Left >= 0 && bounds.Right <= size.Width &&
                                    bounds.Top >= 0 && bounds.Bottom <= size.Height,
                                    "긴 리포트와 최소 창 크기에서도 저장·닫기 버튼은 화면 안에 보여야 합니다.");
                            }
                            var closeButton = (Button)report.FindName("ReportCloseButton");
                            Assert.AreEqual("창 닫기", AutomationProperties.GetName(closeButton));
                            SavePreview(reportRoot, $"report-{size.Width}.png", size);
                            var notificationsExpander = (Expander)report.FindName("NotificationsExpander");
                            notificationsExpander.IsExpanded = true;
                            Assert.AreEqual(blocked.Count() + 2, notifications.Items.Count);
                            reportRoot.UpdateLayout();
                            var replies = Descendants(notifications).OfType<Expander>()
                                .Where(expander => Equals(expander.Header, "답변하기") && expander.Visibility == Visibility.Visible)
                                .ToArray();
                            Assert.AreEqual(blocked.Count(), replies.Length,
                                "차단 알림의 답변하기가 해당 원본 알림 안에 표시되어야 합니다.");
                            var lastReply = replies.Last();
                            Assert.IsFalse(lastReply.IsExpanded);
                            lastReply.IsExpanded = true;
                            reportRoot.UpdateLayout();
                            var copies = Descendants(lastReply).OfType<Button>().Where(button => Equals(button.Content, "복사")).ToArray();
                            CollectionAssert.AreEqual(QuickReplyTemplateStore.Defaults.ToArray(), copies.Select(button => (string)button.Tag).ToArray(),
                                "기본 답장 문구를 모두 골라 복사할 수 있어야 합니다.");
                            var copy = copies[0];
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
                            lastReply.IsExpanded = false;
                            notificationsExpander.IsExpanded = false;
                            scroll.ScrollToTop();
                        }
                    }
                    finally { report.Close(); }
                }
                finally { window.Close(); if (File.Exists(autoDndFile)) File.Delete(autoDndFile); }
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
    public void Dashboard_FocusOn_WarmsTheWindow_AndCoolsWhenOff()
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
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, new FocusRoutineService(focusMode, settings), results, tracker, focusModeService: focusMode);
                try
                {
                    var rim = (Border)window.FindName("FocusRim");
                    var backdrop = (GlassBackdrop)window.FindName("Backdrop");
                    var card = (Border)window.FindName("HomeFocusCard");
                    Assert.AreEqual(0, rim.Opacity, "집중이 꺼져 있으면 테두리는 데워지지 않습니다.");
                    Assert.AreEqual(0, backdrop.EmberLevel);
                    focusMode.Start();
                    Assert.AreEqual(1, rim.Opacity, "집중을 켜면 창 테두리가 호박빛으로 데워집니다.");
                    Assert.AreEqual(1, backdrop.EmberLevel, "창 뒤 불씨가 켜집니다.");
                    Assert.AreSame(window.FindResource("FocusRimBrush"), card.BorderBrush, "타이머 카드 테두리도 같은 빛을 씁니다.");
                    var root = (FrameworkElement)window.Content;
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        root.UpdateLayout();
                        SavePreview(root, $"focus-on-{size.Width}.png", size);
                    }
                    focusMode.Stop();
                    Assert.AreEqual(0, rim.Opacity, "집중을 끄면 다시 식습니다.");
                    Assert.AreEqual(0, backdrop.EmberLevel);
                    Assert.AreSame(window.FindResource("GlassRim"), card.BorderBrush);
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
    public void Dashboard_TabsFollowChosenMockups_WithRealisticDay()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string calendarPath = Path.Combine(Path.GetTempPath(), "altong-mock-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using var database = SqliteDatabase.CreateInMemory();
                database.Initialize();
                // 어제 하루를 도안과 같은 모양으로: 집중 세 번, 저녁 집중에 알림 다섯 개, 점심 약속 하나. 모두 가상 데이터다.
                var day = DateTime.Today.AddDays(-1);
                DateTime At(int hour, int minute = 0) => day.AddHours(hour).AddMinutes(minute).ToUniversalTime();
                var sessions = new SqliteFocusSessionRepository(database);
                var windows = new SqliteWindowSessionRepository(database);
                var raw = new SqliteNotificationRepository(database);
                var plan = new (string Id, DateTime Start, DateTime End)[]
                {
                    ("mock-morning", At(10), At(11, 30)), ("mock-afternoon", At(15, 10), At(15, 52)), ("mock-evening", At(18, 27), At(18, 56))
                };
                foreach (var (id, start, end) in plan)
                {
                    sessions.StartSessionAsync(new FocusSessionRecord(id, start, end, IsCompleted: true)).GetAwaiter().GetResult();
                    double minutes = (end - start).TotalMinutes;
                    var parts = new[] { ("Code.exe", "DashboardWindow.xaml - ALTONG_client - Visual Studio Code", .65), ("chrome.exe", "Pull request #69 · GitHub - Chrome", .2), ("WindowsTerminal.exe", "dotnet test", .15) };
                    var cursor = start;
                    foreach (var (app, title, share) in parts)
                    {
                        var next = cursor.AddMinutes(minutes * share);
                        windows.InsertAsync(new WindowSessionRecord(0, app, title, cursor, next, (int)(next - cursor).TotalSeconds, id)).GetAwaiter().GetResult();
                        cursor = next;
                    }
                }
                var notifications = new[]
                {
                    new NotificationRecord("mock-1", "Slack", "김팀장", "주간 기획 회의", "내일 오후 3시에 회의실 A에서 주간 기획 회의 진행합니다. 자료 미리 확인 부탁드려요.", At(18, 31), false, SessionId: "mock-evening"),
                    new NotificationRecord("mock-2", "KakaoTalk", "개발팀 단톡방", "QA", "QA는 금요일 오전 10시에 같이 봐요", At(18, 36), false, SessionId: "mock-evening"),
                    new NotificationRecord("mock-3", "Discord", "민준", "리뷰 요청", "PR #68 리뷰 부탁해요. 리포트 말풍선 쪽이에요", At(18, 41), false, SessionId: "mock-evening"),
                    new NotificationRecord("mock-4", "Slack", "운영팀장", "[긴급] 운영 서버", "[긴급] 운영 서버 다운됐습니다. 지금 확인 가능한 분 계신가요?", At(18, 44), true, SessionId: "mock-evening"),
                    new NotificationRecord("mock-5", "Outlook", "인사팀", "연말정산", "12월 5일까지 연말정산 서류를 제출해 주세요.", At(18, 50), false, SessionId: "mock-evening"),
                    new NotificationRecord("mock-6", "Slack", "박대리", "기획서", "기획서 v2 올렸습니다", At(15, 24), false, SessionId: "mock-afternoon")
                };
                foreach (var record in notifications) raw.InsertAsync(record).GetAwaiter().GetResult();
                var results = new SessionResultsService(database, sessions);
                foreach (var (id, start, end) in plan)
                    results.Reports.SaveAsync(new SessionResult(start, end, notifications.Where(item => item.SessionId == id).ToArray(),
                        [new SessionAppUsage("Code.exe", (end - start).TotalSeconds)], true) { SessionId = id }).GetAwaiter().GetResult();
                var calendar = new DashboardCalendarStore(calendarPath);
                var lunch = new DateTimeOffset(DateTime.SpecifyKind(day.AddHours(12.5), DateTimeKind.Unspecified), TimeSpan.FromHours(9));
                calendar.Save(new DashboardCalendarEntry("mock-morning", "lunch", "Slack",
                    new CalendarSchedule("lunch", "점심 약속", lunch, lunch.AddHours(1), "Asia/Seoul", "판교역", null)));

                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                settings.Save(new FocusTimerSettings(25, 5, true));
                var focusMode = new FocusModeService();
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, new FocusRoutineService(focusMode, settings), results, tracker,
                    focusModeService: focusMode, calendar: calendar);
                try
                {
                    var root = (FrameworkElement)window.Content;
                    var tabs = (TabControl)window.FindName("DashboardTabs");
                    var subtitle = (TextBlock)window.FindName("DashboardDateText");
                    void Preview(string name)
                    {
                        foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                        {
                            root.Measure(size);
                            root.Arrange(new Rect(size));
                            root.UpdateLayout();
                            PumpBindings();
                            root.UpdateLayout();
                            SavePreview(root, $"mock-{name}-{size.Width}.png", size);
                        }
                    }

                    // 알림 확인 1: 세션 머리글, 고른 알림의 본문·찾은 일정·답장 문구
                    tabs.SelectedIndex = 1;
                    RefreshDashboardData(window);
                    var inbox = (DashboardNotificationsView)((ContentControl)window.FindName("NotificationsHost")).Content;
                    Assert.AreEqual("어제 18:27 – 18:56 집중 · 5개", inbox.VisibleItems[0].GroupHeaderText);
                    Assert.AreEqual("어제 15:10 – 15:52 집중 · 1개", inbox.VisibleItems[5].GroupHeaderText);
                    Assert.IsTrue(inbox.VisibleItems.Skip(1).Take(4).All(row => row.GroupHeaderText is null), "같은 집중의 알림에는 머리글을 한 번만 붙입니다.");
                    Assert.AreEqual("김팀장 · Slack", inbox.VisibleItems.Single(row => row.Record.Id == "mock-1").RowTitleText);
                    ((ListBox)inbox.FindName("NotificationsList")).SelectedItem = inbox.VisibleItems.Single(row => row.Record.Id == "mock-1");
                    Assert.AreEqual(Visibility.Visible, ((FrameworkElement)inbox.FindName("FoundSchedulePanel")).Visibility, "본문의 약속을 찾은 일정으로 보여야 합니다.");
                    StringAssert.Contains(((TextBlock)inbox.FindName("FoundScheduleText")).Text, "15:00");
                    StringAssert.Contains(((TextBlock)inbox.FindName("CopyReplyText")).Text, "· 복사");
                    Assert.IsTrue(((Button)inbox.FindName("OpenInReportButton")).IsEnabled);
                    Preview("notifications");

                    // 활동 일지 2: 앱 순위 + 시간대 열지도. 어제가 들어가는 기간(이번 달, 1일이면 이번 주)으로 본다.
                    tabs.SelectedIndex = 2;
                    if (DateTime.Today.Day > 1 || DateTime.Today.DayOfWeek != DayOfWeek.Sunday)
                    {
                        window.SelectJournalRange(DateTime.Today.Day > 1 ? 2 : 1);
                        RefreshDashboardData(window);
                        StringAssert.StartsWith(subtitle.Text, "집중 중 쓴 앱 · ");
                        var ranking = (StackPanel)window.FindName("JournalRankingList");
                        Assert.AreEqual(3, ranking.Children.Count, "집중 중 쓴 앱 세 개를 오래 쓴 순서로 세웁니다.");
                        StringAssert.StartsWith(AutomationProperties.GetName(((Border)ranking.Children[0]).Child), "1위 Code");
                        Assert.AreEqual("10시~11시", ((System.Windows.Documents.Run)window.FindName("JournalInsightHour")).Text);
                        Assert.AreEqual(Visibility.Visible, ((Border)window.FindName("JournalInsight")).Visibility);
                        Preview("journal");
                        Assert.AreEqual(3, ((WrapPanel)window.FindName("JournalLegend")).Children.Count, "앱 세 개면 기타 없이 범례 세 개입니다.");
                        Assert.IsTrue(((Canvas)window.FindName("JournalHourChart")).Children.Count > 0, "시각별 막대를 그려야 합니다.");
                    }

                    // 캘린더 1: 월 + 그날 시간표
                    tabs.SelectedIndex = 4;
                    var calendarView = (ReportCalendarView)((ContentControl)window.FindName("ReportCalendarHost")).Content;
                    var selecting = calendarView.SelectDateAsync(day);
                    var frame = new DispatcherFrame();
                    var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                    poll.Tick += (_, _) => { if (selecting.IsCompleted) frame.Continue = false; };
                    poll.Start(); Dispatcher.PushFrame(frame); poll.Stop();
                    StringAssert.Contains(subtitle.Text, "집중 리포트 3개");
                    Assert.AreEqual("18:27 리포트 열기", ((Button)calendarView.FindName("DayReportButton")).Content);
                    Assert.AreEqual(Visibility.Visible, ((Button)calendarView.FindName("DayGoogleButton")).Visibility);
                    Preview("calendar");

                    // 설정 2: 빠른 토글 + 두 칸
                    tabs.SelectedIndex = 3;
                    Assert.AreEqual("자주 바꾸는 것은 위에서 바로", subtitle.Text);
                    Assert.AreEqual("집중 25분 · 휴식 5분", ((TextBlock)window.FindName("QuickTimerText")).Text);
                    Assert.AreEqual("선택됨", AutomationProperties.GetItemStatus(((UniformGrid)window.FindName("RoutinePresetGrid")).Children[0]));
                    Assert.AreEqual(Visibility.Visible, ((FrameworkElement)window.FindName("RoutineOptions")).Visibility);
                    Preview("settings");
                    ((Button)window.FindName("QuickTimerCard")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsFalse(settings.Current.TimerEnabled);
                    Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)window.FindName("RoutineOptions")).Visibility, "타이머를 끄면 루틴 입력을 숨깁니다.");
                    Assert.AreEqual(Visibility.Visible, ((FrameworkElement)window.FindName("RoutineTimerOffText")).Visibility);
                    Preview("settings-timer-off");
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (File.Exists(calendarPath)) File.Delete(calendarPath);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF check timed out.");
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
        var refresh = ((TabControl)window.FindName("DashboardTabs")).SelectedIndex == 1
            ? ((DashboardNotificationsView)((ContentControl)window.FindName("NotificationsHost")).Content).RefreshAsync()
            : (Task)typeof(DashboardWindow).GetMethod("RefreshDashboardDataAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
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

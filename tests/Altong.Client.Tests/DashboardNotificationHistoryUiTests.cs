using System.Diagnostics;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class DashboardNotificationHistoryUiTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [TestMethod]
    public void History_AccumulatesAcrossDays_IndividualAndBatchRemoval_PreserveRawRecordsAndFocusEndHistory()
    {
        OnDashboard((db, window, focus, root) =>
        {
            var raw = new SqliteNotificationRepository(db);
            var start = DateTime.Today.ToUniversalTime();
            Insert(raw, "a", start.AddMinutes(1), false, "session-a");
            Insert(raw, "b", start.AddMinutes(2), false, "session-b");
            Insert(raw, "c", start.AddMinutes(3), true, "session-b");
            Insert(raw, "old-blocked", start.AddDays(-30).AddMinutes(1), false);
            Insert(raw, "old-passed", start.AddDays(-30).AddMinutes(2), true);
            var tabs = (TabControl)window.FindName("DashboardTabs");
            tabs.SelectedIndex = 1;
            Refresh(window);
            Assert.AreEqual(3, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(2, Rows(window, "NotificationItemsControl").Length);
            Assert.IsNull(window.FindName("NotificationDatePicker"));
            Assert.IsNull(window.FindName("NotificationTodayButton"));
            Assert.AreEqual("b", Rows(window, "BlockedNotificationItemsControl")[0].Record.Id);
            Assert.AreEqual("old-blocked", Rows(window, "BlockedNotificationItemsControl")[2].Record.Id);
            Assert.IsTrue(Rows(window, "BlockedNotificationItemsControl")[2].TimeText
                .StartsWith(DateTime.Today.AddDays(-30).ToString("yyyy.MM.dd")));
            focus.Start();
            focus.Stop();
            Refresh(window);
            Assert.AreEqual(3, Rows(window, "BlockedNotificationItemsControl").Length,
                "집중모드를 종료해도 과거 날짜를 포함한 차단 알림은 유지합니다.");

            root.UpdateLayout();
            var blocked = ExpandedRows(window, "BlockedNotificationItemsControl");
            var first = (FrameworkElement)blocked.ItemContainerGenerator.ContainerFromIndex(0);
            string removedId = ((NotificationHistoryItem)first.DataContext).Record.Id;
            Descendants<Button>(first).Single(button => Equals(button.Content, "목록에서 제거"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(2, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.IsNotNull(raw.GetByIdAsync(removedId).GetAwaiter().GetResult());

            Rows(window, "BlockedNotificationItemsControl").Single(row => row.Record.Id == "a").IsSelected = true;
            Rows(window, "NotificationItemsControl").Single(row => row.Record.Id == "c").IsSelected = true;
            Insert(raw, "new-arrival", start.AddMinutes(4), false);
            Refresh(window);
            AssertSelected(window, 1, 1);
            Assert.IsFalse(Rows(window, "BlockedNotificationItemsControl").Single(row => row.Record.Id == "new-arrival").IsSelected);
            ((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            AssertSelected(window, 0, 1);
            Assert.AreEqual(2, Rows(window, "NotificationItemsControl").Length,
                "차단 알림 제거는 선택한 통과 알림까지 제거하지 않습니다.");
            Assert.IsTrue(Rows(window, "NotificationItemsControl").Single(row => row.Record.Id == "c").IsSelected);
            ((Button)window.FindName("DismissSelectedPassedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            CollectionAssert.AreEqual(new[] { "new-arrival", "old-blocked" },
                Rows(window, "BlockedNotificationItemsControl").Select(row => row.Record.Id).ToArray());
            Assert.AreEqual("old-passed", Rows(window, "NotificationItemsControl").Single().Record.Id);
            AssertSelected(window, 0, 0);
            SavePreview(root, "notification-history-managed.png");
            var selectBlocked = (Button)window.FindName("SelectAllBlockedNotificationsButton");
            var selectPassed = (Button)window.FindName("SelectAllPassedNotificationsButton");
            Assert.IsNull(window.FindName("NotificationManagementPanel"));
            Assert.IsNull(window.FindName("DismissSelectedNotificationsButton"));
            Assert.IsNull(window.FindName("NotificationListSummaryText"));
            selectBlocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertSelected(window, 2, 0);
            Assert.IsTrue(Rows(window, "NotificationItemsControl").All(row => !row.IsSelected));
            Assert.AreEqual("선택 해제", selectBlocked.Content);
            Assert.AreEqual("표시된 항목 모두 선택", selectPassed.Content);
            selectPassed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertSelected(window, 2, 1);
            selectBlocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertSelected(window, 0, 1);
            Assert.IsTrue(Rows(window, "NotificationItemsControl").All(row => row.IsSelected));
            selectPassed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertSelected(window, 0, 0);
            selectBlocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            selectPassed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(0, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(1, Rows(window, "NotificationItemsControl").Length);
            AssertSelected(window, 0, 1);
            ((Button)window.FindName("DismissSelectedPassedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(0, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(0, Rows(window, "NotificationItemsControl").Length);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)window.FindName("NotificationsEmptyStateText")).Visibility);
            Assert.IsFalse(((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).IsEnabled);
            Assert.IsFalse(((Button)window.FindName("DismissSelectedPassedNotificationsButton")).IsEnabled);
            Assert.IsFalse(selectBlocked.IsEnabled);
            Assert.IsFalse(selectPassed.IsEnabled);
            Assert.AreEqual(Visibility.Collapsed, ((Button)window.FindName("LoadMoreNotificationsButton")).Visibility);
            Assert.AreEqual(4, window.Results.ReadNotificationsAsync(start, start.AddDays(1)).GetAwaiter().GetResult().Count);
            Assert.AreEqual(2, window.Results.ReadNotificationsAsync(start.AddDays(-30), start.AddDays(-29)).GetAwaiter().GetResult().Count);
            SavePreview(root, "notification-history-empty.png");
        });
    }

    [TestMethod]
    public void History_LoadMore_RetainsSelectionAtPageBoundary_AndOnlyRemovesSelectedRows()
    {
        OnDashboard((db, window, _, _) =>
        {
            var raw = new SqliteNotificationRepository(db);
            var start = DateTime.Today.ToUniversalTime().AddDays(-10);
            for (int i = 0; i < 60; i++) Insert(raw, "item-" + i, start.AddMinutes(i), false);
            ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 1;
            Refresh(window);
            var more = (Button)window.FindName("LoadMoreNotificationsButton");
            Assert.AreEqual(50, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(Visibility.Visible, more.Visibility);
            Assert.IsTrue(more.IsEnabled);
            Rows(window, "BlockedNotificationItemsControl").Last().IsSelected = true;
            Insert(raw, "new-arrival", start.AddDays(10).AddMinutes(1), false);
            Refresh(window);
            Assert.AreEqual(51, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.IsTrue(Rows(window, "BlockedNotificationItemsControl").Single(row => row.Record.Id == "item-10").IsSelected);
            Assert.IsFalse(Rows(window, "BlockedNotificationItemsControl").First().IsSelected);
            ((Button)window.FindName("SelectAllBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertSelected(window, 51, 0);
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(61, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(Visibility.Collapsed, more.Visibility);
            AssertSelected(window, 51, 0);
            Assert.IsFalse(Rows(window, "BlockedNotificationItemsControl").Single(row => row.Record.Id == "item-0").IsSelected);
            ((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(10, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.IsTrue(Rows(window, "BlockedNotificationItemsControl").All(row => int.Parse(row.Record.Id[5..]) < 10));
            Assert.AreEqual(61, window.Results.ReadNotificationsAsync(start, start.AddDays(11)).GetAwaiter().GetResult().Count);
        });
    }

    [TestMethod]
    public void History_FailedBatchRemoval_KeepsRowsAndSelection_AndAllowsRetry()
    {
        OnDashboard((db, window, _, _) =>
        {
            var raw = new SqliteNotificationRepository(db);
            var start = DateTime.Today.ToUniversalTime();
            Insert(raw, "one", start.AddMinutes(1), false);
            Insert(raw, "two", start.AddMinutes(2), true);
            ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 1;
            Refresh(window);
            ((Button)window.FindName("SelectAllBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)window.FindName("SelectAllPassedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (var connection = db.CreateOpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TRIGGER fake_dismiss_failure BEFORE INSERT ON dashboard_notification_dismissals
                    BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;
                    """;
                command.ExecuteNonQuery();
            }
            ((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(1, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(1, Rows(window, "NotificationItemsControl").Length);
            Assert.IsTrue(Rows(window, "BlockedNotificationItemsControl").Single().IsSelected);
            Assert.IsTrue(Rows(window, "NotificationItemsControl").Single().IsSelected);
            AssertSelected(window, 1, 1);
            Assert.IsTrue(((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).IsEnabled);
            Assert.IsTrue(((Button)window.FindName("DismissSelectedPassedNotificationsButton")).IsEnabled);
            Assert.IsTrue(((Button)window.FindName("SelectAllBlockedNotificationsButton")).IsEnabled);
            Assert.IsTrue(((Button)window.FindName("SelectAllPassedNotificationsButton")).IsEnabled);
            Assert.IsTrue(((TextBlock)window.FindName("NotificationHistoryStatusText")).Text.Contains("제거하지 못했습니다"));
            using (var connection = db.CreateOpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER fake_dismiss_failure;";
                command.ExecuteNonQuery();
            }
            ((Button)window.FindName("DismissSelectedBlockedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            AssertSelected(window, 0, 1);
            ((Button)window.FindName("DismissSelectedPassedNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(0, Rows(window, "BlockedNotificationItemsControl").Length);
            Assert.AreEqual(0, Rows(window, "NotificationItemsControl").Length);
            Assert.AreEqual(2, window.Results.ReadNotificationsAsync(start, start.AddDays(1)).GetAwaiter().GetResult().Count);
        });
    }

    [TestMethod]
    public void History_ManyLongNotifications_RemainScrollableAtMinimumWindowSize()
    {
        OnDashboard((db, window, _, root) =>
        {
            var raw = new SqliteNotificationRepository(db);
            var start = DateTime.Today.ToUniversalTime();
            for (int i = 0; i < 60; i++) Insert(raw, "long-" + i, start.AddSeconds(i), false,
                title: string.Join(" ", Enumerable.Repeat("긴 알림 내용과 일정 변경 사항을 확인해 주세요.", 6)));
            var tabs = (TabControl)window.FindName("DashboardTabs");
            tabs.SelectedIndex = 1;
            Refresh(window);
            var scroll = (ScrollViewer)tabs.SelectedContent;
            var more = (Button)window.FindName("LoadMoreNotificationsButton");
            more.BringIntoView();
            root.UpdateLayout();
            var moreBounds = more.TransformToAncestor(scroll).TransformBounds(new Rect(more.RenderSize));
            Assert.IsTrue(moreBounds.Left >= -1 && moreBounds.Right <= scroll.ViewportWidth + 1);
            Assert.IsTrue(moreBounds.Top >= -1 && moreBounds.Bottom <= scroll.ViewportHeight + 1,
                "더 보기 버튼까지 스크롤로 접근할 수 있어야 합니다.");
            SavePreview(root, "notification-history-more-bottom.png");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitReady(window);
            Assert.AreEqual(60, Rows(window, "BlockedNotificationItemsControl").Length);
            var expandedRows = ExpandedRows(window, "BlockedNotificationItemsControl");
            root.Measure(new Size(640, 520));
            root.Arrange(new Rect(0, 0, 640, 520));
            root.UpdateLayout();
            scroll.ScrollToTop();
            root.UpdateLayout();
            Assert.IsTrue(scroll.ScrollableHeight > 0);
            Assert.IsTrue(scroll.ScrollableWidth <= 1, "좁은 창에서 수신 날짜·시각과 제거 버튼이 가로로 잘리지 않습니다.");
            SavePreview(root, "notification-history-many-top.png");
            scroll.ScrollToBottom();
            root.UpdateLayout();
            var items = expandedRows;
            var last = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(59);
            var remove = Descendants<Button>(last).Single(button => Equals(button.Content, "목록에서 제거"));
            remove.BringIntoView();
            root.UpdateLayout();
            var bounds = remove.TransformToAncestor(scroll).TransformBounds(new Rect(remove.RenderSize));
            Assert.IsTrue(bounds.Left >= -1 && bounds.Right <= scroll.ViewportWidth + 1);
            Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                "마지막 알림의 제거 버튼까지 스크롤로 접근할 수 있어야 합니다.");
            SavePreview(root, "notification-history-many-bottom.png");
        });
    }

    private static void Insert(SqliteNotificationRepository repo, string id, DateTime at, bool passed,
        string session = "ui-session", string title = "가상 알림") => repo.InsertAsync(new NotificationRecord(
            id, "Messenger", "가상 동료", title, "가상 알림 본문입니다.", at, passed, SessionId: session)).GetAwaiter().GetResult();

    private static NotificationHistoryItem[] Rows(DashboardWindow window, string name) =>
        ((ItemsControl)window.FindName(name)).Items.Cast<NotificationAppGroup>().SelectMany(group => group.Rows).ToArray();

    private static ItemsControl ExpandedRows(DashboardWindow window, string name)
    {
        var groups = (ItemsControl)window.FindName(name);
        ((NotificationAppGroup)groups.Items[0]).IsExpanded = true;
        ((FrameworkElement)window.Content).UpdateLayout();
        return Descendants<ItemsControl>((FrameworkElement)groups.ItemContainerGenerator.ContainerFromIndex(0)).Single();
    }

    [TestMethod]
    public void History_AppGroups_StartCollapsed_AndKeepExpansionAndSelectionAcrossRefresh()
    {
        OnDashboard((db, window, _, root) =>
        {
            var raw = new SqliteNotificationRepository(db);
            var at = DateTime.Today.ToUniversalTime();
            foreach (var (id, app) in new[] { ("one", "Messenger"), ("two", "messenger"), ("mail", "Mail") })
                raw.InsertAsync(new NotificationRecord(id, app, "가상 발신자", "가상 알림", "본문", at, false)).GetAwaiter().GetResult();
            ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 1;
            Refresh(window);
            var list = (ItemsControl)window.FindName("BlockedNotificationItemsControl");
            var groups = list.Items.Cast<NotificationAppGroup>().ToArray();
            Assert.AreEqual(2, groups.Length);
            Assert.IsTrue(groups.All(group => !group.IsExpanded));
            var passedCard = (Border)window.FindName("PassedNotificationsCard");
            var blockedCard = (Border)window.FindName("BlockedNotificationsCard");
            Assert.IsTrue(passedCard.TransformToAncestor(root).Transform(new Point()).Y
                < blockedCard.TransformToAncestor(root).Transform(new Point()).Y, "통과 알림을 차단 알림 위에 표시합니다.");
            SavePreview(root, "notification-groups-collapsed.png");
            var messenger = groups.Single(group => group.Rows.Length == 2);
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(Array.IndexOf(groups, messenger));
            var header = Descendants<System.Windows.Controls.Primitives.ToggleButton>(container)
                .Single(button => button.Name == "NotificationHeaderButton");
            ((IToggleProvider)new ToggleButtonAutomationPeer(header).GetPattern(PatternInterface.Toggle)).Toggle();
            root.UpdateLayout();
            Assert.IsTrue(messenger.IsExpanded, "카드 헤더 클릭은 화면 이동 대신 원본 알림을 펼칩니다.");
            var icons = Descendants<System.Windows.Controls.Image>(container)
                .Where(image => image.Name == "NotificationAppIcon").ToArray();
            Assert.AreEqual(2, icons.Length, "펼친 원본 알림 각각에 앱 아이콘이 표시됩니다.");
            Assert.IsTrue(icons.All(image => image.Source is not null && image.ActualWidth > 0 && image.Visibility == Visibility.Visible));
            var checkbox = Descendants<CheckBox>(container).First();
            ((IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle)).Toggle();
            root.UpdateLayout();
            Assert.IsTrue(messenger.Rows[0].IsSelected);
            Assert.AreSame(((CheckBox)window.FindName("TimerEnabledCheckBox")).Template, checkbox.Template);
            checkbox.BringIntoView();
            root.UpdateLayout();
            SavePreview(root, "notification-checkbox-selected.png");
            var summaryButton = Descendants<Button>(container).Single(button => button.Name == "AppSummaryButton");
            summaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            root.UpdateLayout();
            Assert.IsTrue(messenger.IsSummaryOpen);
            Assert.IsInstanceOfType<DashboardNotificationSummariesView>(((ContentControl)summaryButton.Tag).Content);
            Assert.AreEqual(Visibility.Visible, ((StackPanel)window.FindName("NotificationGroupsPanel")).Visibility);
            SavePreview(root, "notification-inline-summary-empty.png");
            raw.InsertAsync(new NotificationRecord("new", "MESSENGER", "다른 발신자", "새 가상 알림", "본문", at.AddMinutes(1), false)).GetAwaiter().GetResult();
            Refresh(window);
            Assert.AreSame(messenger, list.Items.Cast<NotificationAppGroup>().Single(group => group.Rows.Length == 3));
            Assert.IsTrue(messenger.IsExpanded);
            Assert.IsTrue(messenger.IsSummaryOpen, "목록 갱신 후에도 카드 안의 요약을 유지합니다.");
            root.UpdateLayout();
            container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(list.Items.IndexOf(messenger));
            summaryButton = Descendants<Button>(container).Single(button => button.Name == "AppSummaryButton");
            Assert.IsInstanceOfType<DashboardNotificationSummariesView>(((ContentControl)summaryButton.Tag).Content);
            Assert.AreEqual(1, messenger.Rows.Count(row => row.IsSelected));
            AssertSelected(window, 1, 0);
            SavePreview(root, "notification-groups-expanded.png");
            summaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(messenger.IsSummaryOpen);
            Assert.IsTrue(messenger.IsExpanded, "요약을 닫아도 펼쳐 둔 원본 알림은 유지합니다.");
        });
    }

    private static void AssertSelected(DashboardWindow window, int blocked, int passed)
    {
        Assert.AreEqual($"선택 {blocked}개", ((TextBlock)window.FindName("SelectedBlockedNotificationCountText")).Text);
        Assert.AreEqual($"선택 {passed}개", ((TextBlock)window.FindName("SelectedPassedNotificationCountText")).Text);
    }

    private static void Refresh(DashboardWindow window)
    {
        _ = (Task)typeof(DashboardWindow).GetMethod("RefreshDashboardDataAsync", PrivateInstance)!.Invoke(window, null)!;
        WaitReady(window);
    }

    private static void WaitReady(DashboardWindow window)
    {
        var frame = new DispatcherFrame();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        bool ready = false;
        timer.Tick += (_, _) =>
        {
            ready = new[] { "_dashboardDataLoading", "_dashboardDataRefreshPending", "_removingNotifications" }
                .All(name => !(bool)typeof(DashboardWindow).GetField(name, PrivateInstance)!.GetValue(window)!);
            if (ready || stopwatch.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        try { timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(ready, "알림 목록 갱신이 완료되어야 합니다."); }
        finally { timer.Stop(); }
        ((FrameworkElement)window.Content).UpdateLayout();
    }

    private static void OnDashboard(Action<SqliteDatabase, DashboardWindow, FocusModeService, FrameworkElement> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var db = SqliteDatabase.CreateInMemory();
                db.Initialize();
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var focus = new FocusModeService();
                var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings),
                    new SessionResultsService(db, new SqliteFocusSessionRepository(db)), new Tracker(), focus);
                try
                {
                    window.ApplyTemplate();
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(640, 520));
                    root.Arrange(new Rect(0, 0, 640, 520));
                    root.UpdateLayout();
                    action(db, window, focus, root);
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void SavePreview(FrameworkElement root, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(640, 520, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name));
        encoder.Save(file);
    }

    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext { get; } = new("", "", DateTime.UtcNow);
        public CurrentContext CaptureNow() => CurrentContext;
#pragma warning disable CS0067
        public event EventHandler<CurrentContext>? ContextChanged;
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;
#pragma warning restore CS0067
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

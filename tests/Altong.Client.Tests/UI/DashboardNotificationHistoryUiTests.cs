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
    [TestMethod]
    public void SearchPending_KeepsNotificationListBackgroundTransparent() => RunSta(() =>
    {
        using var db = Database();
        var raw = new SqliteNotificationRepository(db);
        Insert(raw, 1, DateTime.UtcNow);
        var view = new DashboardNotificationsView(new(db));
        Await(view.RefreshAsync());
        Layout(view, new Size(800, 620));

        var list = (ListBox)view.FindName("NotificationsList");
        var search = (TextBox)view.FindName("SearchBox");
        search.Text = "발신자";
        Assert.IsFalse(list.IsEnabled, "검색을 처리하는 동안 기존 목록은 조작할 수 없어야 합니다.");
        list.ApplyTemplate();
        var surface = (Border)list.Template.FindName("ListSurface", list);
        Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)surface.Background).Color,
            "검색 중에도 알림 목록 배경이 밝아지면 안 됩니다.");

        Await(view.RefreshAsync());
        Assert.IsTrue(list.IsEnabled);
        Assert.AreEqual(1, view.VisibleItems.Count);
    });

    [TestMethod]
    public void ThousandItems_KeepPageSelectionAndReadingPosition_WhenNewNotificationsArrive() => RunSta(() =>
    {
        using var db = Database();
        var raw = new SqliteNotificationRepository(db);
        var now = DateTime.UtcNow;
        for (int i = 0; i < 1000; i++) Insert(raw, i, now.AddMinutes(-i));
        var view = new DashboardNotificationsView(new(db));
        Await(view.RefreshAsync());
        Assert.AreEqual(1000, view.TotalCount);
        Assert.AreEqual(30, view.VisibleItems.Count);
        Await(view.GoToPageAsync(10));
        Layout(view, new Size(800, 620));
        var expectedIds = view.VisibleItems.Select(row => row.Record.Id).ToArray();
        var selected = view.VisibleItems[8];
        selected.IsSelected = true;
        var list = (ListBox)view.FindName("NotificationsList");
        list.SelectedItem = selected;
        var listScroll = Descendants(list).OfType<ScrollViewer>().Single();
        listScroll.ScrollToVerticalOffset(120);
        Layout(view, new Size(800, 620));
        double offset = listScroll.VerticalOffset;
        Insert(raw, 1001, now.AddMinutes(1));
        Await(view.CheckForUpdatesAsync());
        Assert.AreEqual(10, view.PageIndex);
        CollectionAssert.AreEqual(expectedIds, view.VisibleItems.Select(row => row.Record.Id).ToArray());
        Assert.AreSame(selected, list.SelectedItem);
        Assert.AreEqual(offset, listScroll.VerticalOffset, .1);
        Assert.AreEqual(Visibility.Visible, ((Button)view.FindName("NewArrivalsButton")).Visibility);
        StringAssert.Contains(((Button)view.FindName("NewArrivalsButton")).Content.ToString()!, "1개");
        SavePreview(view, "inbox-new-arrivals-wide.png", new Size(800, 620));

        Await(view.GoToPageAsync(11));
        Assert.AreEqual("item-330", view.VisibleItems[0].Record.Id, "새 알림이 페이지 경계를 밀어내지 않아야 합니다.");
        Assert.IsFalse(view.VisibleItems.Any(row => expectedIds.Contains(row.Record.Id)));
        Await(view.GoToPageAsync(10));
        Assert.IsTrue(view.VisibleItems.Single(row => row.Record.Id == selected.Record.Id).IsSelected);
        Await(view.RefreshAsync());
        Assert.AreEqual(0, view.PageIndex);
        Assert.AreEqual(1001, view.TotalCount);
        Assert.AreEqual("item-1001", view.VisibleItems[0].Record.Id);
        Assert.AreEqual(Visibility.Collapsed, ((Button)view.FindName("NewArrivalsButton")).Visibility);
    });

    [TestMethod]
    public void SearchAndFilters_IncludeOlderPages_UseLiteralText_AndDiscardStaleQueries() => RunSta(() =>
    {
        using var db = Database();
        var raw = new SqliteNotificationRepository(db);
        var now = DateTime.UtcNow;
        for (int i = 0; i < 300; i++) Insert(raw, i, now.AddMinutes(-i), i == 244 ? "100%_자료 · 회의실" : null);
        var view = new DashboardNotificationsView(new(db));
        Await(view.RefreshAsync());
        var search = (TextBox)view.FindName("SearchBox");
        search.Text = "발신자244";
        Await(view.RefreshAsync());
        Assert.AreEqual("item-244", view.VisibleItems.Single().Record.Id);
        search.Text = "제목299";
        Await(view.RefreshAsync());
        Assert.AreEqual("item-299", view.VisibleItems.Single().Record.Id);
        search.Text = "%_";
        Await(view.RefreshAsync());
        Assert.AreEqual("item-244", view.VisibleItems.Single().Record.Id, "검색의 %와 _는 와일드카드가 아닙니다.");
        search.Text = "";
        Await(view.RefreshAsync());
        var apps = (ComboBox)view.FindName("AppFilter");
        apps.SelectedItem = apps.Items.Cast<NotificationAppOption>().Single(option => option.AppName == "Mail");
        ((RadioButton)view.FindName("PassedTab")).IsChecked = true;
        Await(view.RefreshAsync());
        Assert.AreEqual(75, view.TotalCount);
        Assert.IsTrue(view.VisibleItems.All(row => row.Record.IsPassed == true && row.AppName == "Mail"));
        ((RadioButton)view.FindName("AllTab")).IsChecked = true;
        apps.SelectedIndex = 0;
        search.Text = "발신자244";
        var first = view.RefreshAsync();
        search.Text = "제목299";
        var last = view.RefreshAsync();
        Await(Task.WhenAll(first, last));
        Assert.AreEqual("item-299", view.VisibleItems.Single().Record.Id);
        search.Text = "결과가 없는 검색어";
        Await(view.RefreshAsync());
        Assert.AreEqual(0, view.TotalCount);
        Assert.AreEqual("조건에 맞는 알림 없음", ((TextBlock)view.FindName("EmptyStateText")).Text);
        Assert.AreEqual(Visibility.Visible, ((Button)view.FindName("ResetFiltersButton")).Visibility);
        Layout(view, new Size(510, 390));
        SavePreview(view, "inbox-search-empty.png", new Size(510, 390));
    });

    [TestMethod]
    public void PageAndIndividualRemoval_PreserveRawData_AndCorrectTheLastPage() => RunSta(() =>
    {
        using var db = Database();
        var raw = new SqliteNotificationRepository(db);
        var now = DateTime.UtcNow;
        for (int i = 0; i < 75; i++) Insert(raw, i, now.AddDays(-30).AddMinutes(-i));
        var view = new DashboardNotificationsView(new(db));
        Await(view.RefreshAsync());
        view.VisibleItems[0].IsSelected = true;
        string firstId = view.VisibleItems[0].Record.Id;
        Await(view.GoToPageAsync(1));
        view.VisibleItems[1].IsSelected = true;
        string secondId = view.VisibleItems[1].Record.Id;
        Assert.AreEqual("선택 2개", ((TextBlock)view.FindName("SelectedCountText")).Text);
        ((Button)view.FindName("DismissSelectedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((TextBlock)view.FindName("StatusText")).Text == "알림 2개 제거됨");
        Assert.AreEqual(73, view.TotalCount);
        Assert.IsNotNull(raw.GetByIdAsync(firstId).GetAwaiter().GetResult());
        Assert.IsNotNull(raw.GetByIdAsync(secondId).GetAwaiter().GetResult(), "리포트 원문은 보존합니다.");
        Await(view.GoToPageAsync(2));
        ((Button)view.FindName("SelectPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("선택 13개", ((TextBlock)view.FindName("SelectedCountText")).Text);
        ((Button)view.FindName("DismissSelectedButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((TextBlock)view.FindName("StatusText")).Text == "알림 13개 제거됨");
        Assert.AreEqual(60, view.TotalCount);
        Assert.AreEqual(1, view.PageIndex);
        Assert.IsFalse(((Button)view.FindName("NextPageButton")).IsEnabled);
        var list = (ListBox)view.FindName("NotificationsList");
        list.SelectedItem = view.VisibleItems[0];
        string detailId = view.VisibleItems[0].Record.Id;
        ((Button)view.FindName("DismissNotificationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => ((TextBlock)view.FindName("StatusText")).Text == "알림 1개 제거됨");
        Assert.AreEqual(59, view.TotalCount);
        Assert.IsNotNull(raw.GetByIdAsync(detailId).GetAwaiter().GetResult());
        view.VisibleItems[0].IsSelected = true;
        ((RadioButton)view.FindName("BlockedTab")).IsChecked = true;
        Await(view.RefreshAsync());
        Assert.IsTrue(view.VisibleItems.All(row => !row.IsSelected), "필터를 바꾸면 숨겨진 이전 선택을 제거 대상으로 남기지 않습니다.");
    });

    [TestMethod]
    public void ListAndDetail_KeepFullContentAndFixedActions_AtWideAndCompactSizes() => RunSta(() =>
    {
        using var db = Database();
        var raw = new SqliteNotificationRepository(db);
        string body = string.Join("\n", Enumerable.Repeat("가상의 긴 알림입니다. 한국어와 English, 🙂, https://example.invalid/경로를 모두 유지합니다.", 100));
        string title = string.Concat(Enumerable.Repeat("긴 제목 · 회의 일정 확인 ", 8));
        var now = DateTime.UtcNow;
        for (int i = 0; i < 45; i++) raw.InsertAsync(new("item-" + i, "가상 Messenger", "가상 발신자 · " + title, title, body, now.AddMinutes(-i), i % 2 == 0)).GetAwaiter().GetResult();
        var view = new DashboardNotificationsView(new(db));
        Await(view.RefreshAsync());
        var list = (ListBox)view.FindName("NotificationsList");
        foreach (var size in new[] { new Size(800, 620), new Size(510, 390) })
        {
            Layout(view, size);
            list.SelectedItem = view.VisibleItems[2];
            Layout(view, size);
            var listScroll = Descendants(list).OfType<ScrollViewer>().Single();
            listScroll.ScrollToVerticalOffset(160);
            Layout(view, size);
            Assert.IsTrue(ScrollWheelBehavior.GetIsEnabled(listScroll));
            var itemContainer = (UIElement)list.ItemContainerGenerator.ContainerFromIndex(2);
            double wheelStart = listScroll.VerticalOffset;
            itemContainer.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent });
            Layout(view, size);
            Assert.AreEqual(wheelStart + 24, listScroll.VerticalOffset, 1, "가상화 목록도 휠 한 칸에 24픽셀만 이동해야 합니다.");
            double savedOffset = listScroll.VerticalOffset;
            SavePreview(view, $"inbox-list-{size.Width}.png", size);
            if (size.Width < 680) view.OpenCompactDetail();
            Layout(view, size);
            Assert.AreEqual(body, ((TextBlock)view.FindName("DetailBodyText")).Text);
            Assert.AreEqual(title, ((NotificationHistoryItem)((Grid)view.FindName("DetailContent")).DataContext).Record.Title);
            var detailScroll = (ScrollViewer)view.FindName("DetailScroll");
            Assert.IsTrue(detailScroll.ScrollableHeight > 0 && detailScroll.ScrollableWidth <= 1);
            detailScroll.ScrollToBottom();
            Layout(view, size);
            foreach (string name in new[] { "CopyNotificationButton", "DismissNotificationButton" })
            {
                var button = (Button)view.FindName(name);
                var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= size.Height && bounds.Right <= size.Width,
                    "긴 본문이 고정 동작 버튼을 가리지 않아야 합니다.");
            }
            Assert.IsTrue(Descendants(view).OfType<ScrollViewer>().Where(scroll => scroll.ScrollableHeight > 0 && Ancestors(scroll, view).OfType<UIElement>().All(element => element.Visibility == Visibility.Visible))
                .All(scroll => !Ancestors(scroll, view).OfType<ScrollViewer>().Any()), "본문과 목록 스크롤은 중첩되지 않습니다.");
            detailScroll.ScrollToTop();
            Layout(view, size);
            SavePreview(view, $"inbox-detail-top-{size.Width}.png", size);
            SavePreview(view, $"inbox-detail-{size.Width}.png", size);
            if (size.Width < 680)
            {
                Assert.AreEqual(Visibility.Collapsed, ((Border)view.FindName("ListPane")).Visibility);
                ((Button)view.FindName("BackToListButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(view, size);
                Assert.AreEqual(savedOffset, listScroll.VerticalOffset, 1, "목록으로 돌아와도 읽던 위치를 유지합니다.");
                Assert.AreSame(view.VisibleItems[2], list.SelectedItem);
            }
        }
    });

    [TestMethod]
    public void AppDropdown_ShowsReadableLabels_AndNativeSelectionWorks() => RunSta(() =>
    {
        using var db = Database();
        var raw = new SqliteNotificationRepository(db);
        Insert(raw, 0, DateTime.UtcNow);
        Insert(raw, 1, DateTime.UtcNow.AddMinutes(-1));
        var view = new DashboardNotificationsView(new(db));
        var window = new Window { Content = view, Width = 820, Height = 650, ShowInTaskbar = false };
        try
        {
            window.Show();
            Await(view.RefreshAsync());
            var apps = (ComboBox)view.FindName("AppFilter");
            window.UpdateLayout();
            var label = (TextBlock)apps.Template.FindName("AppFilterLabel", apps);
            Assert.AreEqual("모든 앱", label.Text);
            var list = (ListBox)view.FindName("NotificationsList");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var itemName = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(item)!.GetName();
            StringAssert.Contains(itemName, "제목0");
            StringAssert.Contains(itemName, "발신자0");
            apps.IsDropDownOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var popup = (System.Windows.Controls.Primitives.Popup)apps.Template.FindName("PART_Popup", apps);
            Assert.IsTrue(popup.IsOpen);
            Assert.IsTrue(Descendants(popup.Child).OfType<TextBlock>().Any(text => text.Text == "Mail (1)"));
            apps.SelectedItem = apps.Items.Cast<NotificationAppOption>().Single(option => option.AppName == "Mail");
            apps.IsDropDownOpen = false;
            Await(view.RefreshAsync());
            window.UpdateLayout();
            Assert.AreEqual("Mail (1)", label.Text);
            Assert.AreEqual("Mail", view.VisibleItems.Single().AppName);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ReadAndRemovalFailures_ShowRecovery_WithoutReportingEmptyOrLosingSelection() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        var view = new DashboardNotificationsView(new(db));
        Await(view.RefreshAsync());
        Assert.AreEqual(Visibility.Visible, ((Button)view.FindName("RetryButton")).Visibility);
        Assert.AreEqual("알림을 불러오지 못했습니다.", ((TextBlock)view.FindName("EmptyStateText")).Text);
        db.Initialize();
        Await(view.RefreshAsync());
        Assert.AreEqual("알림 없음", ((TextBlock)view.FindName("EmptyStateText")).Text);
        Assert.AreEqual(Visibility.Collapsed, ((Button)view.FindName("RetryButton")).Visibility);
        var raw = new SqliteNotificationRepository(db);
        Insert(raw, 1, DateTime.UtcNow);
        Await(view.RefreshAsync());
        view.VisibleItems[0].IsSelected = true;
        using (var connection = db.CreateOpenConnection())
        using (var command = connection.CreateCommand()) { command.CommandText = "DROP TABLE dashboard_notification_dismissals;"; command.ExecuteNonQuery(); }
        Await(view.DismissAsync(["item-1"]));
        Assert.IsTrue(view.VisibleItems[0].IsSelected);
        StringAssert.Contains(((TextBlock)view.FindName("StatusText")).Text, "제거 실패");
        Assert.IsTrue(((Button)view.FindName("DismissSelectedButton")).IsEnabled);
        Layout(view, new Size(510, 390));
        SavePreview(view, "inbox-remove-error.png", new Size(510, 390));
        db.Initialize();
        Await(view.DismissAsync(["item-1"]));
        Assert.AreEqual(0, view.TotalCount);
        Assert.IsNotNull(raw.GetByIdAsync("item-1").GetAwaiter().GetResult());
    });

    private static SqliteDatabase Database() { var db = SqliteDatabase.CreateInMemory(); db.Initialize(); return db; }
    private static void Insert(SqliteNotificationRepository raw, int index, DateTime at, string? body = null) => raw.InsertAsync(new(
        "item-" + index, index % 2 == 0 ? "Mail" : "Chat", "발신자" + index, "제목" + index, body ?? "가상의 본문 " + index, at, index % 4 == 0)).GetAwaiter().GetResult();
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert.IsTrue(completed(), "알림 화면 작업 대기 시간 초과");
    }
    private static void Layout(FrameworkElement view, Size size)
    {
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child, DependencyObject root)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null && parent != root; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
    }
    private static void SavePreview(FrameworkElement view, string name, Size size)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path);
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(path, name)); encoder.Save(file);
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }
}

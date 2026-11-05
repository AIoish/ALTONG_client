using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ScrollHardeningUiTests
{
    [TestMethod]
    public void Wheel_UsesSmallPixelSteps_AndHandsOffAtNestedBoundary() => RunSta(() =>
    {
        var target = new Border { Height = 1600, Background = Brushes.Transparent };
        var inner = new ScrollViewer { Content = target, Height = 120 };
        var stack = new StackPanel();
        stack.Children.Add(inner);
        stack.Children.Add(new Border { Height = 1600 });
        var outer = new ScrollViewer { Content = stack };
        ScrollWheelBehavior.SetIsEnabled(inner, true);
        ScrollWheelBehavior.SetIsEnabled(outer, true);
        Layout(outer, new Size(400, 240));
        inner.ScrollToVerticalOffset(100);
        Layout(outer, new Size(400, 240));

        Wheel(target, -120);
        Layout(outer, new Size(400, 240));
        Assert.AreEqual(124, inner.VerticalOffset, .1);
        Assert.AreEqual(0, outer.VerticalOffset, .1, "같은 입력을 부모와 자식 양쪽에 적용하지 않아야 합니다.");
        Wheel(target, -60);
        Layout(outer, new Size(400, 240));
        Assert.AreEqual(136, inner.VerticalOffset, .1, "작은 휠 델타도 비례해 이동해야 합니다.");
        Wheel(target, 120);
        Layout(outer, new Size(400, 240));
        Assert.AreEqual(112, inner.VerticalOffset, .1);

        inner.ScrollToBottom();
        Layout(outer, new Size(400, 240));
        Wheel(target, -120);
        Layout(outer, new Size(400, 240));
        Assert.AreEqual(24, outer.VerticalOffset, .1);
        Assert.AreEqual(inner.ScrollableHeight, inner.VerticalOffset, .1);
        outer.ScrollToBottom();
        Layout(outer, new Size(400, 240));
        Wheel(target, -120);
        Layout(outer, new Size(400, 240));
        Assert.AreEqual(outer.ScrollableHeight, outer.VerticalOffset, .1);
    });

    [TestMethod]
    public void ThousandNotifications_KeepFullData_AndFooterReachableWithOneScroll() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = new SessionResultsService(db, new SqliteFocusSessionRepository(db));
        string longText = string.Concat(Enumerable.Repeat("긴알림문구·한글·English🙂", 35));
        var now = DateTime.UtcNow;
        var notifications = Enumerable.Range(0, 1000).Select(i => new NotificationRecord(
            "large-" + i, longText, null, longText, longText, now.AddSeconds(-i), false)).ToArray();
        var report = new SessionReportWindow(new SessionResult(now.AddHours(-1), now, notifications,
            Enumerable.Range(0, 110).Select(i => new SessionAppUsage("App-" + i + longText, 90)).ToArray(), true)
            { SessionId = "scroll-hardening" }, results.Reports);
        try
        {
            var root = (FrameworkElement)report.Content;
            var scroll = (ScrollViewer)report.FindName("ReportScroll");
            Assert.IsTrue(ScrollWheelBehavior.GetIsEnabled(scroll));
            var items = (ItemsControl)report.FindName("NotificationItemsControl");
            var apps = (ItemsControl)report.FindName("AppItemsControl");
            Assert.AreEqual(1000, ((SessionResult)report.DataContext).NotificationCount);
            Assert.AreEqual(50, items.Items.Count);
            Assert.AreEqual(50, apps.Items.Count);
            ((Button)report.FindName("MoreAppsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(100, apps.Items.Count);
            ((Button)report.FindName("MoreNotificationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(100, items.Items.Count);
            ((Expander)report.FindName("NotificationsExpander")).IsExpanded = true;
            foreach (var size in new[] { new Size(480, 380), new Size(600, 660) })
            {
                Layout(root, size);
                Assert.AreEqual(1, Descendants(root).OfType<ScrollViewer>().Count());
                Assert.IsTrue(scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0);
                Assert.IsTrue(scroll.ScrollableWidth <= 1, "긴 한글·영문·이모지가 가로로 넘치지 않아야 합니다.");
                foreach (string name in new[] { "SaveReportButton", "SkipSaveButton" })
                {
                    var button = (Button)report.FindName(name);
                    var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    Assert.IsTrue(bounds.Bottom <= size.Height && bounds.Right <= size.Width);
                    var bodyBounds = scroll.TransformToAncestor(root).TransformBounds(new Rect(scroll.RenderSize));
                    Assert.IsTrue(bodyBounds.Bottom <= bounds.Top, "본문이 고정 버튼 영역을 덮지 않아야 합니다.");
                }
                var more = (Button)report.FindName("MoreNotificationsButton");
                more.BringIntoView();
                Layout(root, size);
                var moreBounds = more.TransformToAncestor(scroll).TransformBounds(new Rect(more.RenderSize));
                Assert.IsTrue(moreBounds.Top >= -1 && moreBounds.Bottom <= scroll.ViewportHeight + 1);
                SavePreview(root, $"report-many-bottom-{size.Width}.png", size);
                scroll.ScrollToTop();
                Layout(root, size);
                SavePreview(root, $"report-long-text-{size.Width}.png", size);
            }
        }
        finally { report.Close(); }
    });

    [TestMethod]
    public void ThousandSummaries_RevealInBatches_WithoutLosingEditableRows() => RunSta(() =>
    {
        var view = new NotificationSummaryCalendarView(Enumerable.Range(0, 1000)
            .Select(i => new NotificationSummaryItem("summary-" + i, "긴 요약 본문", i == 0, null)).ToArray());
        var items = (ItemsControl)view.FindName("SummaryItemsControl");
        Assert.AreEqual(1000, view.Items.Length);
        Assert.AreEqual(25, items.Items.Count);
        var first = view.Items[0];
        first.IsEditing = true;
        first.Title = "편집 중인 일정";
        var firstGroup = (ReportSummaryGroupViewModel)items.Items[0];
        Assert.AreSame(first, firstGroup.Schedules.Single());
        ((Button)view.FindName("MoreSummariesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(50, items.Items.Count);
        Assert.AreSame(firstGroup, items.Items[0]);
        Assert.AreSame(first, view.Items[0]);
        Assert.IsTrue(first.IsEditing);
        Assert.AreEqual("편집 중인 일정", first.Title);
    });

    [TestMethod]
    public void SummaryLoadingAndFailure_KeepCloseAndRetryUsable() => RunSta(() =>
    {
        var provider = new ControlledSummaryProvider();
        var report = new SessionReportWindow(new SessionResult(DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow, [], [], true)
            { SessionId = "pending-summary" }, summaryProvider: provider);
        try
        {
            var root = (FrameworkElement)report.Content;
            var size = new Size(480, 380);
            var status = (TextBlock)report.FindName("SummaryLoadStatusText");
            var reload = (Button)report.FindName("ReloadSummaryButton");
            Assert.IsFalse(reload.IsEnabled);
            StringAssert.Contains(status.Text, "불러오는 중");
            Layout(root, size);
            status.BringIntoView();
            Layout(root, size);
            SavePreview(root, "report-loading-480.png", size);

            provider.Pending.SetException(new IOException("test failure"));
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!reload.IsEnabled && DateTime.UtcNow < deadline)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(reload.IsEnabled);
            StringAssert.Contains(status.Text, "실패");
            Assert.AreEqual(Visibility.Visible, status.Visibility);
            Assert.IsTrue(((Button)report.FindName("SkipSaveButton")).IsEnabled);
            Assert.IsTrue(((Button)report.FindName("ReportCloseButton")).IsEnabled);
            status.BringIntoView();
            Layout(root, size);
            SavePreview(root, "report-error-480.png", size);

            provider.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(reload.IsEnabled);
            provider.Pending.SetResult(new ReportSummarySupply([]));
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (!reload.IsEnabled && DateTime.UtcNow < deadline)
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(reload.IsEnabled);
            StringAssert.Contains(status.Text, "새 요약 없음");
        }
        finally { report.Close(); }
    });

    private sealed class ControlledSummaryProvider : IReportSummaryProvider
    {
        public TaskCompletionSource<ReportSummarySupply> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken) => Pending.Task;
    }

    private static void Wheel(UIElement target, int delta) => target.RaiseEvent(
        new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });

    private static void Layout(FrameworkElement root, Size size)
    {
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SavePreview(FrameworkElement root, string filename, Size size)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path);
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(path, filename));
        encoder.Save(file);
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }
}

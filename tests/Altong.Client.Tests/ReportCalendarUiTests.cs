using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Altong.Client.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ReportCalendarUiTests
{
    [TestMethod]
    public void Dashboard_CalendarPageRendersWithinWindow_AndOpensExistingReport() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = new SessionResultsService(db, new SqliteFocusSessionRepository(db));
        var start = DateTime.UtcNow.AddMinutes(-5);
        results.Begin(start, 0);
        var pending = results.CompleteFocusSessionAsync(DateTime.UtcNow);
        Await(pending);
        Await(results.Reports.SaveAsync(pending.Result!));
        var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        var mode = new FocusModeService();
        using var tracker = new StubTracker();
        var window = new DashboardWindow(settings, new FocusRoutineService(mode, settings), results, tracker);
        var dock = new NotificationDockWindow();
        try
        {
            var tabs = (TabControl)window.FindName("DashboardTabs");
            tabs.SelectedIndex = 4;
            var view = (ReportCalendarView)((ContentControl)window.FindName("ReportCalendarHost")).Content;
            Await(view.SelectDateAsync(DateTime.Today));
            var root = (FrameworkElement)window.Content;
            foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                Pump();
                var scroll = (ScrollViewer)view.FindName("PageScroll");
                Assert.IsTrue(scroll.ViewportHeight > 0);
                Assert.IsTrue(scroll.ScrollableWidth < 1);
                SavePreview(root, $"dashboard-calendar-{size.Width}.png", size);
                scroll.ScrollToBottom();
                root.UpdateLayout();
                SavePreview(root, $"dashboard-calendar-bottom-{size.Width}.png", size);
                scroll.ScrollToTop();
            }
            // Exercise the real report-opening path while the end-of-focus reminder is visible.
            bool opened = false;
            Exception? inputFailure = null;
            Button? openButton = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var report = window.OwnedWindows.OfType<SessionReportWindow>().FirstOrDefault();
                if (report is null) return;
                timer.Stop();
                opened = true;
                try
                {
                    Assert.AreEqual("저장된 리포트", ((SessionResult)report.DataContext).ReportSource);
                    Assert.IsTrue(IsWindowEnabled(new WindowInteropHelper(dock).Handle),
                        "리포트가 열려 있어도 알림 독은 사용자 입력을 받아야 합니다.");
                    Assert.IsTrue(IsWindowEnabled(new WindowInteropHelper(window).Handle));
                    Assert.IsFalse(openButton!.IsEnabled, "같은 목록 항목은 리포트를 닫을 때까지 중복으로 열지 않습니다.");
                    var closeReminder = (Button)dock.FindName("ReminderDismissButton");
                    closeReminder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsFalse(dock.IsReminderVisible);
                    Assert.IsFalse(dock.IsVisible, "종료 안내를 닫으면 독도 숨겨져야 합니다.");
                    Assert.IsTrue(report.IsVisible, "안내를 닫아도 리포트는 열려 있어야 합니다.");
                }
                catch (Exception ex) { inputFailure = ex; }
                finally { report.Close(); }
            };
            window.Show();
            dock.ShowRoutineReminder("집중 모드가 꺼졌어요.", () => "집중 종료", "가상 종료 안내");
            dock.HideAfterCurrentReminder();
            Pump();
            Until(() => ((ListBox)view.FindName("ReportList")).Items.Count == 1);
            view.UpdateLayout();
            var button = Descendants((ListBox)view.FindName("ReportList")).OfType<Button>().First(b => Equals(b.Content, "리포트 열기"));
            openButton = button;
            timer.Start();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => opened && button.IsEnabled);
            timer.Stop();
            if (inputFailure is not null) throw inputFailure;
        }
        finally { dock.Close(); window.Close(); }
    });

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr handle);

    private sealed class StubTracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public CurrentContext CaptureNow() => CurrentContext;
    }
    [TestMethod]
    public void Calendar_DateSelectionPaginationDeletionAndSmallWindowRemainUsable() => RunSta(() =>
    {
        var calendarPath = Path.Combine(Path.GetTempPath(), "altong-calendar-many-" + Guid.NewGuid() + ".json");
        try
        {
            using var db = SqliteDatabase.CreateInMemory();
            db.Initialize();
            var sessions = new SqliteFocusSessionRepository(db);
            var results = new SessionResultsService(db, sessions);
            var localDay = new DateTime(2026, 10, 2);
            var start = DateTime.SpecifyKind(localDay.AddHours(10), DateTimeKind.Local).ToUniversalTime();
            var calendar = new DashboardCalendarStore(calendarPath);
            for (int i = 0; i < 65; i++)
            {
                var id = "calendar-ui-" + i.ToString("D3");
                var from = start.AddMinutes(i * 6);
                var scheduleStart = new DateTimeOffset(from).ToOffset(TimeSpan.FromHours(9));
                calendar.Save(new(id, "summary", "KakaoTalk", new("event-" + i,
                    "가상의 긴 일정 제목입니다. 작은 창에서 일정과 Google Calendar 버튼을 확인합니다.",
                    scheduleStart, scheduleStart.AddMinutes(5), "Asia/Seoul", "가상 회의실", "세부사항")));
                Await(sessions.StartSessionAsync(new(id, from, from.AddMinutes(5), IsCompleted: true)));
                Await(results.Reports.SaveAsync(new SessionResult(from, from.AddMinutes(5),
                    new[] { new NotificationRecord("fake-" + i, "Messenger", null, "가상 알림", "가상 본문", from.AddSeconds(1), false) },
                    new[] { new SessionAppUsage("VeryLongApplicationNameForWrappingAndSmallWindowVerification.exe", 120) }, true) { SessionId = id }));
            }
            var view = new ReportCalendarView(results, calendar);
            Await(view.SelectDateAsync(localDay));
            var list = (ListBox)view.FindName("ReportList");
            Assert.AreEqual(30, list.Items.Count);
            var scheduleList = (ListBox)view.FindName("ScheduleList");
            Assert.AreEqual(65, scheduleList.Items.Count);
            Assert.IsTrue(((TextBlock)view.FindName("ReportCountText")).Text.Contains("65개"));
            Assert.IsTrue(((System.Windows.Controls.Primitives.UniformGrid)view.FindName("DaysPanel")).Children
                .OfType<Button>().Any(button => button.ToolTip.ToString()!.Contains("65개")));
            Await(view.LoadMoreAsync());
            Await(view.LoadMoreAsync());
            Assert.AreEqual(65, list.Items.Count);
            Assert.AreEqual(Visibility.Collapsed, ((Button)view.FindName("LoadMoreButton")).Visibility);
            foreach (var size in new[] { new Size(848, 580), new Size(568, 330) })
            {
                view.Measure(size);
                view.Arrange(new Rect(size));
                view.UpdateLayout();
                Pump();
                var pageScroll = (ScrollViewer)view.FindName("PageScroll");
                Assert.IsTrue(pageScroll.ScrollableWidth < 1, "캘린더 페이지에 가로 잘림이 없어야 합니다.");
                var listScroll = Descendants(list).OfType<ScrollViewer>().First();
                Assert.IsTrue(listScroll.ViewportHeight > 0);
                Assert.IsTrue(listScroll.ScrollableHeight > 0, "많은 리포트에 스크롤로 접근할 수 있어야 합니다.");
                Assert.IsTrue(listScroll.ScrollableWidth < 1);
                var scheduleScroll = Descendants(scheduleList).OfType<ScrollViewer>().First();
                Assert.IsTrue(scheduleScroll.ViewportHeight > 0 && scheduleScroll.ScrollableHeight > 0,
                    "저장한 일정이 많아도 작은 창에서 목록을 스크롤할 수 있어야 합니다.");
                Assert.IsTrue(scheduleScroll.ScrollableWidth < 1);
                SavePreview(view, $"calendar-{size.Width}.png", size);
                pageScroll.ScrollToBottom();
                listScroll.ScrollToEnd();
                view.UpdateLayout();
                Pump();
                Assert.IsTrue(Math.Abs(listScroll.VerticalOffset - listScroll.ScrollableHeight) < 1);
                SavePreview(view, $"calendar-bottom-{size.Width}.png", size);
                listScroll.ScrollToTop();
                pageScroll.ScrollToTop();
            }
            var lastId = ((ReportListEntry)list.Items[0]).SessionId;
            view.UpdateLayout();
            var deleteButton = Descendants(list).OfType<Button>()
                .First(button => Equals(button.Content, "삭제") && button.Tag is ReportListEntry entry && entry.SessionId == lastId);
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => ((TextBlock)view.FindName("HistoryStatusText")).Text == "리포트를 삭제했습니다.");
            Assert.IsNull(results.OpenReportAsync(lastId).GetAwaiter().GetResult());
            Assert.IsTrue(((TextBlock)view.FindName("ReportCountText")).Text.Contains("64개"));
            Await(view.SelectDateAsync(localDay.AddDays(1)));
            Assert.AreEqual(0, list.Items.Count);
            Assert.AreEqual(0, scheduleList.Items.Count);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)view.FindName("ScheduleEmptyStateText")).Visibility);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)view.FindName("EmptyStateText")).Visibility);
            view.UpdateLayout();
            SavePreview(view, "calendar-empty.png", new Size(568, 330));
            Await(view.SelectDateAsync(new DateTime(2026, 9, 1)));
            Assert.AreEqual("2026년 9월", ((TextBlock)view.FindName("MonthText")).Text);
            Assert.AreEqual(0, list.Items.Count);
            Await(view.SelectDateAsync(localDay));
            Assert.AreEqual(30, list.Items.Count);
            Assert.AreEqual(65, scheduleList.Items.Count);
        }
        finally { if (File.Exists(calendarPath)) File.Delete(calendarPath); }
    });

    [TestMethod]
    public void ReportWindow_SaveAndSkipButtonsPersistOnlyExplicitlySavedReport() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var results = new SessionResultsService(db, sessions);
        var start = DateTime.UtcNow.AddMinutes(-5);
        Await(sessions.StartSessionAsync(new("ui-save", start, start.AddMinutes(5), IsCompleted: true)));
        var result = new SessionResult(start, start.AddMinutes(5), Array.Empty<NotificationRecord>(), Array.Empty<SessionAppUsage>(), true) { SessionId = "ui-save" };
        var report = new SessionReportWindow(result, results.Reports);
        report.Show();
        ((Button)report.FindName("SkipSaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(report.IsVisible);
        Assert.IsNull(results.Reports.GetSnapshotAsync("ui-save").GetAwaiter().GetResult());
        report = new SessionReportWindow(result, results.Reports);
        report.Show();
        ((Button)report.FindName("SaveReportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => !report.IsVisible);
        Assert.IsNotNull(results.Reports.GetSnapshotAsync("ui-save").GetAwaiter().GetResult());
        var saved = new SessionReportWindow(results.Reports.GetSnapshotAsync("ui-save").GetAwaiter().GetResult()!, results.Reports, isSaved: true);
        try
        {
            Assert.AreEqual(Visibility.Collapsed, ((Button)saved.FindName("SaveReportButton")).Visibility);
            Assert.AreEqual("확인", ((Button)saved.FindName("SkipSaveButton")).Content);
        }
        finally { saved.Close(); }
    });

    [TestMethod]
    public void Calendar_ReadFailureShowsRetryInsteadOfEmptyState() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var view = new ReportCalendarView(new SessionResultsService(db, new SqliteFocusSessionRepository(db)));
        using var connection = db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE focus_session_reports;";
        command.ExecuteNonQuery();
        Await(view.RefreshAsync());
        Assert.AreEqual(Visibility.Visible, ((Button)view.FindName("RetryButton")).Visibility);
        Assert.AreEqual(Visibility.Collapsed, ((TextBlock)view.FindName("EmptyStateText")).Visibility);
        db.Initialize();
        Await(view.RefreshAsync());
        Assert.AreEqual(Visibility.Collapsed, ((Button)view.FindName("RetryButton")).Visibility);
        Assert.AreEqual(Visibility.Visible, ((TextBlock)view.FindName("EmptyStateText")).Visibility);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "UI verification timed out.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Until(Func<bool> completed)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.IsTrue(completed(), "Dispatcher operation timed out.");
    }

    private static void SavePreview(FrameworkElement view, string name, Size size)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, name));
        encoder.Save(output);
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class GoogleCalendarUiTests
{
    [TestMethod]
    public void Dashboard_JsonArrival_CorrectionsAndRemovalPersist_ReportIsReadOnly_AndSmallCardsRemainAccessible()
    {
        Exception? error = null;
        var previousDemo = Environment.GetEnvironmentVariable("ALTONG_CALENDAR_DEMO");
        Environment.SetEnvironmentVariable("ALTONG_CALENDAR_DEMO", null);
        var folder = Path.Combine(Path.GetTempPath(), "altong-summary-ui-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                Directory.CreateDirectory(folder);
                using var db = SqliteDatabase.CreateInMemory();
                db.Initialize();
                var start = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
                var sessions = new SqliteFocusSessionRepository(db);
                foreach (var id in new[] { "first", "second" })
                {
                    var at = id == "first" ? start : start.AddHours(1);
                    sessions.StartSessionAsync(new FocusSessionRecord(id, at)).GetAwaiter().GetResult();
                    sessions.EndSessionAsync(id, at.AddMinutes(25), true).GetAwaiter().GetResult();
                }
                var results = new SessionResultsService(db, sessions);
                var supplied = new FakeReportSummaryProvider().ReadAsync("first", CancellationToken.None).GetAwaiter().GetResult().Items;
                var secondItems = supplied.Concat(Enumerable.Range(0, 22).Select(i => new NotificationSummaryItem(
                    "normal-" + i, "가상의 긴 알림 요약입니다. 자료 확인과 후속 작업 내용을 읽고 정리할 수 있습니다.", false, null) { AppName = "KakaoTalk" }))
                    .Concat(new[] {
                        new NotificationSummaryItem("mail-summary", "메일 앱의 요약입니다.", false, null) { AppName = "Mail" },
                        new NotificationSummaryItem("legacy-summary", "앱 정보가 없는 과거 요약입니다.", false, null)
                    }).ToArray();
                var settings = new FocusSettingsStore(Path.Combine(folder, "settings.json"));
                var focus = new FocusModeService();
                var links = new List<string>();
                Button? reenter = null;
                DashboardWindow CreateWindow() => new(settings, new FocusRoutineService(focus, settings), results,
                    new Tracker(), focus, summaryProvider: new JsonReportSummaryProvider(folder),
                    summaryStatePath: Path.Combine(folder, "state.json"), openCalendar: link =>
                    { links.Add(link); reenter?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
                var window = CreateWindow();
                try
                {
                    var root = (FrameworkElement)window.Content;
                    ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 1;
                    Refresh(window);
                    var summary = SummaryView(window);
                    var cards = Cards(summary);
                    Assert.AreEqual(Visibility.Visible, ((Border)summary.FindName("SummaryEmptyState")).Visibility);
                    Assert.AreEqual(Visibility.Collapsed, ((ContentControl)window.FindName("NotificationSummariesHost")).Visibility);
                    var saved = new SessionResult(start, start.AddMinutes(25), [], [], true)
                    {
                        SessionId = "first", SummaryItems = supplied,
                        StoredNotificationSummary = "일부 알림 처리가 실패했습니다. 저장된 알림 기준입니다."
                    };
                    results.Reports.SaveAsync(saved).GetAwaiter().GetResult();
                    Refresh(window);
                    OpenApp(window, "KakaoTalk");
                    Assert.AreEqual(3, cards.Items.Length, "JSON 파일 없이도 기존 저장 요약을 표시합니다.");
                    var report = new SessionReportWindow(saved, results.Reports, isSaved: true);
                    try
                    {
                        Layout((FrameworkElement)report.Content, new Size(760, 780));
                        Assert.IsNull(report.FindName("CalendarSummaryHost"));
                        Assert.IsNull(report.FindName("ReloadSummaryButton"));
                        Assert.AreEqual(3, ((ItemsControl)report.FindName("ReportSummaryItemsControl")).Items.Count);
                        Assert.IsTrue(Descendants<TextBlock>((FrameworkElement)report.Content)
                            .Any(text => text.Text == saved.NotificationSummary && text.ActualHeight > 0),
                            "AI 요약을 표시해도 일부 알림 처리 실패 안내를 숨기지 않습니다.");
                    }
                    finally { report.Close(); }
                    WriteBatch("first", supplied);
                    WriteBatch("second", secondItems);
                    Refresh(window);
                    Assert.AreEqual(20, cards.Items.Length);
                    Assert.AreEqual(2, summary.AppCounts.Count);
                    Assert.AreEqual(1, summary.UnassignedCount);
                    ((Button)summary.FindName("LoadMoreSummariesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(28, cards.Items.Length);
                    Layout(root, new Size(920, 780));
                    var list = (ItemsControl)cards.FindName("SummaryItemsControl");
                    FrameworkElement Row(int index) => (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
                    var ordinary = CalendarButton(Row(1));
                    Assert.IsFalse(ordinary.IsVisible);
                    ordinary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, links.Count);
                    var saveButton = CalendarButton(Row(0));
                    saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, links.Count, "알림 확인에서는 브라우저를 열지 않고 로컬 캘린더에 저장합니다.");
                    Assert.IsTrue(cards.Items[0].IsSaved);
                    Assert.IsFalse(saveButton.IsEnabled);
                    StringAssert.Contains(cards.Items[0].Status, "저장");
                    Assert.IsFalse(cards.Items[0].Status.Contains("등록 완료"));
                    CalendarButton(Row(2)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, links.Count);
                    var editing = cards.Items[2];
                    Assert.IsTrue(editing.IsEditing);
                    Refresh(window);
                    Assert.AreSame(editing, cards.Items[2], "갱신 중에도 편집 중인 행을 보존합니다.");
                    editing.StartDate = editing.EndDate = new DateTime(2026, 10, 6);
                    editing.StartTime = "12:00"; editing.EndTime = "13:00";
                    editing.TimeZone = "Asia/Seoul"; editing.Location = "사용자가 정한 장소";
                    CalendarButton(Row(2)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, links.Count);
                    Assert.IsTrue(editing.IsSaved);
                    Assert.IsFalse(editing.IsEditing);
                    var scroll = (ScrollViewer)((TabControl)window.FindName("DashboardTabs")).SelectedContent;
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        Layout(root, size);
                        scroll.ScrollToTop(); root.UpdateLayout();
                        SavePreview(root, $"dashboard-summary-{size.Width}.png", size);
                        var button = CalendarButton(Row(0));
                        var bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + bounds.Top - 8); root.UpdateLayout();
                        bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                        Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1);
                        Assert.IsTrue(scroll.ScrollableWidth <= 1);
                        SavePreview(root, $"dashboard-summary-action-{size.Width}.png", size);
                        scroll.ScrollToBottom(); root.UpdateLayout();
                        Assert.IsTrue(scroll.ScrollableHeight > 0);
                    }
                    File.WriteAllText(Path.Combine(folder, "second.json"), "{invalid-json");
                    Refresh(window);
                    Assert.AreEqual(28, cards.Items.Length);
                    StringAssert.Contains(((TextBlock)summary.FindName("SummaryLoadStatusText")).Text, "일부 요약");
                    WriteBatch("second", secondItems);
                    Refresh(window); Layout(root, new Size(920, 780));
                    Descendants<Button>(Row(0)).Single(button => Equals(button.Content, "목록에서 제거"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsFalse(cards.Items.Any(row => row.SessionId == "second" && row.Summary.SummaryId == "demo-meeting"));
                    Assert.IsTrue(cards.Items.Any(row => row.SessionId == "first" && row.Summary.SummaryId == "demo-meeting"));
                    Assert.AreEqual(3, results.Reports.GetSnapshotAsync("first").GetAwaiter().GetResult()!.SummaryItems.Count);
                    var localCalendar = new DashboardCalendarStore(Path.Combine(folder, "dashboard-calendar-schedules.json"));
                    Assert.AreEqual(1, localCalendar.GetDay(supplied[0].Schedule!.Start!.Value.ToLocalTime().Date).Count,
                        "같은 요약의 중복 클릭은 일정을 추가하지 않으며, 요약 제거 후에도 저장한 일정은 유지됩니다.");
                    var tabs = (TabControl)window.FindName("DashboardTabs");
                    tabs.SelectedIndex = 4;
                    var calendar = (ReportCalendarView)((ContentControl)window.FindName("ReportCalendarHost")).Content;
                    Await(calendar.SelectDateAsync(editing.Draft.Start!.Value.ToLocalTime().Date));
                    var schedules = (ListBox)calendar.FindName("ScheduleList");
                    Assert.AreEqual(1, schedules.Items.Count);
                    foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                    {
                        Layout(root, size);
                        var page = (ScrollViewer)calendar.FindName("PageScroll");
                        var google = Descendants<Button>(schedules).Single(button => Equals(button.Content, "Google Calendar에서 작성"));
                        google.BringIntoView(); root.UpdateLayout();
                        var bounds = google.TransformToAncestor(page).TransformBounds(new Rect(google.RenderSize));
                        Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= page.ViewportHeight + 1);
                        Assert.IsTrue(page.ScrollableWidth <= 1);
                        SavePreview(root, $"dashboard-saved-schedule-{size.Width}.png", size);
                    }
                    reenter = Descendants<Button>(schedules).Single(button => Equals(button.Content, "Google Calendar에서 작성"));
                    reenter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    reenter = null;
                    Assert.AreEqual(1, links.Count, "브라우저를 여는 동안 재진입하지 않습니다.");
                    Assert.AreEqual(GoogleCalendarLink.Build(editing.Draft), links[0]);
                }
                finally { window.Close(); }
                var restarted = CreateWindow();
                try
                {
                    ((TabControl)restarted.FindName("DashboardTabs")).SelectedIndex = 1;
                    Refresh(restarted);
                    Assert.AreEqual(Visibility.Collapsed, ((ContentControl)restarted.FindName("NotificationSummariesHost")).Visibility);
                    OpenApp(restarted, "KakaoTalk");
                    var summary = SummaryView(restarted);
                    ((Button)summary.FindName("LoadMoreSummariesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var rows = Cards(summary).Items;
                    Assert.AreEqual(27, rows.Length);
                    Assert.IsFalse(rows.Any(row => row.SessionId == "second" && row.Summary.SummaryId == "demo-meeting"));
                    Assert.AreEqual("사용자가 정한 장소", rows.Single(row => row.SessionId == "second" && row.Summary.SummaryId == "demo-uncertain").Draft.Location);
                    Assert.IsTrue(rows.Single(row => row.SessionId == "second" && row.Summary.SummaryId == "demo-uncertain").IsSaved);
                    Assert.IsNull(rows.Single(row => row.SessionId == "first" && row.Summary.SummaryId == "demo-uncertain").Draft.Start);
                    Layout((FrameworkElement)restarted.Content, new Size(640, 520));
                    CloseApp(restarted);
                    Assert.AreEqual(Visibility.Collapsed, ((ContentControl)restarted.FindName("NotificationSummariesHost")).Visibility);
                    OpenApp(restarted, "Mail");
                    Assert.AreEqual(1, Cards(summary).Items.Length);
                    Assert.AreEqual("mail-summary", Cards(summary).Items[0].Summary.SummaryId);
                    CloseApp(restarted);
                    ((Button)restarted.FindName("UnassignedSummariesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(1, Cards(summary).Items.Length);
                    Assert.AreEqual("legacy-summary", Cards(summary).Items[0].Summary.SummaryId);
                    ((TabControl)restarted.FindName("DashboardTabs")).SelectedIndex = 4;
                    var calendar = (ReportCalendarView)((ContentControl)restarted.FindName("ReportCalendarHost")).Content;
                    var savedRow = rows.Single(row => row.SessionId == "second" && row.Summary.SummaryId == "demo-uncertain");
                    Await(calendar.SelectDateAsync(savedRow.Draft.Start!.Value.ToLocalTime().Date));
                    Layout((FrameworkElement)restarted.Content, new Size(640, 520));
                    var schedules = (ListBox)calendar.FindName("ScheduleList");
                    Assert.AreEqual(1, schedules.Items.Count, "재실행 후 저장한 일정을 다시 조회합니다.");
                    Descendants<Button>(schedules).Single(button => Equals(button.Content, "일정 삭제"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Await(calendar.RefreshAsync());
                    Assert.AreEqual(0, schedules.Items.Count);
                    Assert.AreEqual(Visibility.Visible, ((TextBlock)calendar.FindName("ScheduleEmptyStateText")).Visibility);
                    Assert.IsFalse(savedRow.IsSaved, "일정을 삭제하면 해당 알림에서 다시 저장할 수 있습니다.");
                    Assert.AreEqual(3, results.Reports.GetSnapshotAsync("first").GetAwaiter().GetResult()!.SummaryItems.Count);
                }
                finally { restarted.Close(); }
                void WriteBatch(string id, IReadOnlyList<NotificationSummaryItem> items) => File.WriteAllText(
                    Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(new NotificationSummaryBatch(1, id, items)));
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start(); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)));
            if (error is not null) throw new AssertFailedException(error.ToString(), error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALTONG_CALENDAR_DEMO", previousDemo);
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
    }

    private static DashboardNotificationSummariesView SummaryView(DashboardWindow window) =>
        (DashboardNotificationSummariesView)typeof(DashboardWindow).GetField("_notificationSummaries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static NotificationSummaryCalendarView Cards(DashboardNotificationSummariesView summary) =>
        (NotificationSummaryCalendarView)((ContentControl)summary.FindName("SummaryCardsHost")).Content;
    private static void OpenApp(DashboardWindow window, string app)
    {
        var root = (FrameworkElement)window.Content;
        Layout(root, new Size(920, 780));
        var groups = (ItemsControl)window.FindName("SummaryAppItemsControl");
        int index = groups.Items.Cast<NotificationAppGroup>().ToList().FindIndex(group => group.AppName == app);
        var container = (FrameworkElement)groups.ItemContainerGenerator.ContainerFromIndex(index);
        var button = Descendants<Button>(container).Single(button => button.Name == "AppSummaryButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        root.UpdateLayout();
        Assert.AreEqual(Visibility.Visible, ((StackPanel)window.FindName("NotificationGroupsPanel")).Visibility);
        Assert.AreEqual(1, ((TabControl)window.FindName("DashboardTabs")).SelectedIndex);
        Assert.IsTrue(((NotificationAppGroup)container.DataContext).IsSummaryOpen);
        Assert.AreSame(SummaryView(window), ((ContentControl)button.Tag).Content,
            "요약은 목록을 대체하지 않고 해당 앱 카드 안에서 열립니다.");
    }
    private static void CloseApp(DashboardWindow window)
    {
        Descendants<Button>((FrameworkElement)window.Content).Single(button => button.Name == "AppSummaryButton"
            && button.DataContext is NotificationAppGroup { IsSummaryOpen: true }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Refresh(DashboardWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _ = (Task)typeof(DashboardWindow).GetMethod("RefreshDashboardDataAsync", flags)!.Invoke(window, null)!;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        bool ready = false;
        timer.Tick += (_, _) =>
        {
            ready = new[] { "_dashboardDataLoading", "_dashboardDataRefreshPending" }
                .All(name => !(bool)typeof(DashboardWindow).GetField(name, flags)!.GetValue(window)!);
            if (ready || watch.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        try { timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(ready); }
        finally { timer.Stop(); }
        ((FrameworkElement)window.Content).UpdateLayout();
    }
    private static Button CalendarButton(DependencyObject root) => Descendants<Button>(root)
        .Single(button => button.Content is string text && text is "캘린더에 저장" or "캘린더에 저장됨" or "저장 중…");
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static void Layout(FrameworkElement root, Size size)
    { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void SavePreview(FrameworkElement root, string name, Size size)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
    }
    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext { get; } = new("", "", DateTime.UtcNow);
        public CurrentContext CaptureNow() => CurrentContext;
#pragma warning disable CS0067
        public event EventHandler<CurrentContext>? ContextChanged;
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;
#pragma warning restore CS0067
        public void Start() { } public void Stop() { } public void Dispose() { }
    }
}

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
using Altong.Client.Services.AI.Briefing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class GoogleCalendarUiTests
{
    [TestMethod]
    public void Report_SavesRestoredGroupIdsInSnapshot() => RunSta(folder =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        const string session = "restored-group";
        var start = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var sessions = new SqliteFocusSessionRepository(db);
        Await(sessions.StartSessionAsync(new FocusSessionRecord(session, start)));
        Await(sessions.EndSessionAsync(session, start.AddMinutes(25), true));
        var reports = new SessionResultsService(db, sessions).Reports;
        var notifications = new[]
        {
            new NotificationRecord("n1", "Slack", "사용자", "프로젝트", "프로젝트 진행", start.AddMinutes(1), false, SessionId: session),
            new NotificationRecord("n2", "Slack", "사용자", "프로젝트", "프로젝트 공유", start.AddMinutes(2), false, SessionId: session)
        };
        var summaries = notifications.Select(notification => new NotificationSummaryItem(
            LlamaReportSummaryProvider.ComputeDeterministicId("sum", session,
                SummaryGroupResolver.GroupKey(notification), [notification.Id]),
            notification.Body, false, null) { AppName = "Slack" }).ToArray();
        var result = new SessionResult(start, start.AddMinutes(25), notifications, [], true)
            { SessionId = session, SummaryItems = summaries };
        var report = new SessionReportWindow(result, reports, calendar: new DashboardCalendarStore(Path.Combine(folder, "calendar.json")),
            summaryStatePath: Path.Combine(folder, "state.json"));
        try
        {
            report.Show();
            ((Button)report.FindName("SaveReportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !report.IsVisible);
            var loaded = reports.GetSnapshotAsync(session);
            Await(loaded);
            var saved = loaded.Result!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(saved.SummaryItems[0].GroupId));
            Assert.AreEqual(saved.SummaryItems[0].GroupId, saved.SummaryItems[1].GroupId);
        }
        finally { report.Close(); }
    });

    [TestMethod]
    public void Report_GroupsSplitSummaries_WhileKeepingEachSchedule() => RunSta(folder =>
    {
        var supplied = new FakeReportSummaryProvider().ReadAsync("grouped", CancellationToken.None).GetAwaiter().GetResult().Items;
        var first = supplied[0] with { GroupId = "group-1" };
        var second = supplied[2] with { GroupId = "group-1" };
        var separate = supplied[1] with { GroupId = "group-2" };
        var cards = new NotificationSummaryCalendarView([first, second, separate]);
        Layout(cards, new Size(600, 660));

        var list = (ItemsControl)cards.FindName("SummaryItemsControl");
        Assert.AreEqual(2, list.Items.Count);
        var group = (ReportSummaryGroupViewModel)list.Items[0];
        StringAssert.Contains(group.Text, first.Text);
        StringAssert.Contains(group.Text, second.Text);
        CollectionAssert.AreEqual(new[] { first.SummaryId, second.SummaryId },
            group.Schedules.Select(row => row.Summary.SummaryId).ToArray());
        Assert.AreEqual(separate.Text, ((ReportSummaryGroupViewModel)list.Items[1]).Text);
    });

    [TestMethod]
    public void Report_SavesAndCorrectsSchedules_PersistsAcrossReopen_AndSynchronizesWithDashboard() => RunSta(folder =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var start = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        var sessions = new SqliteFocusSessionRepository(db);
        Await(sessions.StartSessionAsync(new FocusSessionRecord("first", start)));
        Await(sessions.EndSessionAsync("first", start.AddMinutes(25), true));
        var results = new SessionResultsService(db, sessions);
        var supplied = new FakeReportSummaryProvider().ReadAsync("first", CancellationToken.None).GetAwaiter().GetResult().Items;
        var saved = new SessionResult(start, start.AddMinutes(25), [], [], true)
        {
            SessionId = "first", SummaryItems = supplied,
            StoredNotificationSummary = "일부 알림 처리가 실패했습니다. 저장된 알림 기준입니다."
        };
        Await(results.Reports.SaveAsync(saved));
        var calendarPath = Path.Combine(folder, "dashboard-calendar-schedules.json");
        var calendar = new DashboardCalendarStore(calendarPath);
        var statePath = Path.Combine(folder, "state.json");
        var provider = new CountingProvider(new JsonReportSummaryProvider(folder));
        var settings = new FocusSettingsStore(Path.Combine(folder, "settings.json"));
        var focus = new FocusModeService();
        var links = new List<string>();
        Button? reenter = null;
        var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings), results,
            new Tracker(), focus, summaryProvider: provider, summaryStatePath: statePath,
            calendar: calendar, openCalendar: link =>
            { links.Add(link); reenter?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
        try
        {
            ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 1;
            Await((Task)typeof(DashboardWindow).GetMethod("RefreshDashboardDataAsync", PrivateInstance)!.Invoke(window, null)!);
            Assert.AreEqual(0, provider.Reads, "알림 확인에서는 AI 요약을 조회하지 않습니다.");
            Assert.IsNull(window.FindName("NotificationSummariesHost"));
            Assert.IsNull(window.FindName("SummaryAppItemsControl"));
            Assert.IsNull(window.FindName("UnassignedSummariesButton"));
            var report = new SessionReportWindow(saved, results.Reports, isSaved: true,
                summaryProvider: provider, calendar: calendar, summaryStatePath: statePath);
            CalendarSchedule corrected;
            try
            {
                var root = (FrameworkElement)report.Content;
                Layout(root, new Size(600, 660));
                var cards = Cards(report);
                Assert.AreEqual(3, cards.Items.Length);
                Assert.AreSame(report.FontFamily, cards.FontFamily);
                Assert.AreSame(report.FontFamily, Descendants<TextBlock>(cards).Single(text => text.Text == supplied[0].Text).FontFamily,
                    "AI 요약에도 같은 Pretendard를 적용합니다.");
                Assert.AreEqual("KakaoTalk", cards.Items[0].SourceText);
                Assert.IsTrue(Descendants<TextBlock>(root).Any(text => text.Text == saved.NotificationSummary && text.ActualHeight > 0),
                    "AI 요약이 있어도 일부 알림 처리 실패 안내를 유지합니다.");
                var list = (ItemsControl)cards.FindName("SummaryItemsControl");
                FrameworkElement Row(int index) => (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
                Assert.IsFalse(Descendants<Button>(Row(1)).Any(button => button.Content is string text
                    && text is "캘린더에 저장" or "캘린더에 저장됨" or "저장 중…"),
                    "일반 요약에는 일정 등록 버튼이 없어야 합니다.");
                Assert.IsNull(calendar.Get("first", supplied[1].SummaryId), "일반 알림에는 일정 저장을 허용하지 않습니다.");
                var save = CalendarButton(Row(0));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(cards.Items[0].IsSaved);
                Assert.IsFalse(save.IsEnabled);
                Assert.AreEqual(1, calendar.GetDay(supplied[0].Schedule!.Start!.Value.ToLocalTime().Date).Count);
                Assert.AreEqual(0, links.Count, "리포트에서는 로컬 캘린더에 저장합니다.");
                CalendarButton(Row(2)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editing = cards.Items[2];
                Assert.IsTrue(editing.IsEditing, "날짜·시간이 미정이면 수정 화면을 엽니다.");
                Layout(root, new Size(480, 380));
                var input = Descendants<TextBox>(Row(2)).First();
                input.BringIntoView(); root.UpdateLayout();
                SavePreview(root, "report-schedule-edit-480.png", new Size(480, 380));
                var dateInput = Descendants<DatePicker>(Row(2)).First();
                dateInput.BringIntoView(); root.UpdateLayout();
                var editorScroll = (ScrollViewer)report.FindName("ReportScroll");
                var inputBounds = dateInput.TransformToAncestor(editorScroll).TransformBounds(new Rect(dateInput.RenderSize));
                Assert.IsTrue(inputBounds.Left >= -1 && inputBounds.Right <= editorScroll.ViewportWidth + 1);
                Assert.IsTrue(inputBounds.Top >= -1 && inputBounds.Bottom <= editorScroll.ViewportHeight + 1);
                SavePreview(root, "report-schedule-edit-dates-480.png", new Size(480, 380));
                Layout(root, new Size(600, 660));
                editing.StartDate = editing.EndDate = new DateTime(2026, 10, 6);
                editing.StartTime = "12:00"; editing.EndTime = "13:00";
                editing.TimeZone = "Asia/Seoul"; editing.Location = "사용자가 정한 장소";
                WriteBatch(folder, "first", supplied);
                Reload(report);
                Assert.AreSame(editing, cards.Items[2], "새로고침해도 수정 중인 입력은 유지합니다.");
                File.WriteAllText(Path.Combine(folder, "first.json"), "{invalid-json");
                Reload(report);
                Assert.AreSame(editing, cards.Items[2]);
                StringAssert.Contains(((TextBlock)report.FindName("SummaryLoadStatusText")).Text, "불러오지 못했습니다");
                CalendarButton(Row(2)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(editing.IsSaved);
                Assert.IsFalse(editing.IsEditing);
                corrected = editing.Draft;
                Assert.IsNull(results.Reports.GetSnapshotAsync("first").GetAwaiter().GetResult()!.SummaryItems[2].Schedule!.Start,
                    "일정 수정은 리포트 원본을 덮어쓰지 않습니다.");
                foreach (var size in new[] { new Size(600, 660), new Size(480, 380) })
                {
                    Layout(root, size);
                    var scroll = Descendants<ScrollViewer>(root).First();
                    save.BringIntoView(); root.UpdateLayout();
                    var bounds = save.TransformToAncestor(scroll).TransformBounds(new Rect(save.RenderSize));
                    Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1);
                    Assert.IsTrue(scroll.ScrollableWidth <= 1);
                    SavePreview(root, $"report-schedule-{size.Width}.png", size);
                }
                ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 4;
                var calendarView = (ReportCalendarView)((ContentControl)window.FindName("ReportCalendarHost")).Content;
                Await(calendarView.SelectDateAsync(corrected.Start!.Value.ToLocalTime().Date));
                var schedules = (ListBox)calendarView.FindName("ScheduleList");
                Assert.AreEqual(1, schedules.Items.Count);
                foreach (var size in new[] { new Size(920, 780), new Size(640, 520) })
                {
                    var dashboardRoot = (FrameworkElement)window.Content;
                    Layout(dashboardRoot, size);
                    var page = (ScrollViewer)calendarView.FindName("PageScroll");
                    var google = Descendants<Button>(schedules).Single(button => Equals(button.Content, "Google Calendar에서 작성"));
                    google.BringIntoView(); dashboardRoot.UpdateLayout();
                    var bounds = google.TransformToAncestor(page).TransformBounds(new Rect(google.RenderSize));
                    Assert.IsTrue(bounds.Top >= -1 && bounds.Bottom <= page.ViewportHeight + 1);
                    Assert.IsTrue(page.ScrollableWidth <= 1);
                    SavePreview(dashboardRoot, $"dashboard-saved-schedule-{size.Width}.png", size);
                }
                reenter = Descendants<Button>(schedules).Single(button => Equals(button.Content, "Google Calendar에서 작성"));
                reenter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                reenter = null;
                Assert.AreEqual(1, links.Count, "브라우저를 여는 동안 재진입하지 않습니다.");
                Assert.AreEqual(GoogleCalendarLink.Build(corrected), links[0]);
                calendar.Delete("first", editing.Summary.SummaryId);
                Assert.IsFalse(editing.IsSaved, "캘린더 삭제를 리포트에 즉시 반영합니다.");
                CalendarButton(Row(2)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(editing.IsSaved);
            }
            finally { report.Close(); }
            var reopened = new SessionReportWindow(saved, results.Reports, isSaved: true,
                calendar: new DashboardCalendarStore(calendarPath), summaryStatePath: statePath);
            try
            {
                var rows = Cards(reopened).Items;
                Assert.IsTrue(rows[0].IsSaved);
                Assert.IsTrue(rows[2].IsSaved);
                Assert.AreEqual(corrected, rows[2].Draft);
                Assert.AreEqual("사용자가 정한 장소", rows[2].Draft.Location);
            }
            finally { reopened.Close(); }
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void Report_EmptySavedReport_CanLoadLateSummary_AndRetryAfterFailure() => RunSta(folder =>
    {
        var start = DateTime.UtcNow.AddMinutes(-25);
        var result = new SessionResult(start, start.AddMinutes(25), [], [], true) { SessionId = "late" };
        var report = new SessionReportWindow(result, isSaved: true, summaryProvider: new JsonReportSummaryProvider(folder),
            calendar: new DashboardCalendarStore(Path.Combine(folder, "calendar.json")), summaryStatePath: Path.Combine(folder, "state.json"));
        try
        {
            Reload(report);
            Assert.AreEqual(0, Cards(report).Items.Length);
            Assert.AreEqual(Visibility.Visible, ((Button)report.FindName("ReloadSummaryButton")).Visibility);
            var supplied = new FakeReportSummaryProvider().ReadAsync("late", CancellationToken.None).GetAwaiter().GetResult().Items;
            WriteBatch(folder, "late", supplied);
            Reload(report);
            Assert.AreEqual(3, Cards(report).Items.Length);
            Assert.AreEqual(Visibility.Collapsed, ((StackPanel)report.FindName("LegacySummaryPanel")).Visibility);
            File.WriteAllText(Path.Combine(folder, "late.json"), "{invalid-json");
            Reload(report);
            Assert.AreEqual(3, Cards(report).Items.Length);
            Assert.IsTrue(((Button)report.FindName("ReloadSummaryButton")).IsEnabled);
            Assert.IsFalse(((TextBlock)report.FindName("SummaryLoadStatusText")).Text.Contains("알림 확인"));
        }
        finally { report.Close(); }
    });

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static NotificationSummaryCalendarView Cards(SessionReportWindow report) =>
        (NotificationSummaryCalendarView)((ContentControl)report.FindName("CalendarSummaryHost")).Content;
    private static void Reload(SessionReportWindow report)
    {
        Until(() => !(bool)typeof(SessionReportWindow).GetField("_loadingSummaries", PrivateInstance)!.GetValue(report)!);
        Await((Task)typeof(SessionReportWindow).GetMethod("LoadSummaryItemsAsync", PrivateInstance)!.Invoke(report, null)!);
    }
    private static void WriteBatch(string folder, string id, IReadOnlyList<NotificationSummaryItem> items) => File.WriteAllText(
        Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(new NotificationSummaryBatch(1, id, items)));
    private static Button CalendarButton(DependencyObject root) => Descendants<Button>(root)
        .Single(button => button.Content is string text && text is "캘린더에 저장" or "캘린더에 저장됨" or "저장 중…");
    private static void Await(Task task)
    { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> ready)
    {
        if (ready()) return;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(8)) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); Assert.IsTrue(ready()); }
        finally { timer.Stop(); }
    }
    private static void RunSta(Action<string> action)
    {
        Exception? error = null;
        var folder = Path.Combine(Path.GetTempPath(), "altong-report-ui-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { Directory.CreateDirectory(folder); action(folder); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start(); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
            if (error is not null) throw new AssertFailedException(error.ToString(), error);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
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
    private sealed class CountingProvider(IReportSummaryProvider inner) : IReportSummaryProvider
    {
        public int Reads { get; private set; }
        public Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken)
        { Reads++; return inner.ReadAsync(sessionId, cancellationToken); }
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

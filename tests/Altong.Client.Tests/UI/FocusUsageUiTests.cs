using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
public sealed class FocusUsageUiTests
{
    [TestMethod]
    public void HomeChart_ReadsSessions_RefreshesAfterFocus_AndFitsExpandedCard() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var sessions = new SqliteFocusSessionRepository(db);
        var results = new SessionResultsService(db, sessions);
        var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var focus = new FocusModeService();
        using var tracker = new StubTracker();
        var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings), results, tracker, focus);
        try
        {
            var root = (FrameworkElement)window.Content;
            window.ApplyTemplate();
            Refresh(window);
            var bars = (ItemsControl)window.FindName("HomeFocusUsageItems");
            Assert.AreEqual(24, bars.Items.Count);
            Assert.IsTrue(bars.Items.Cast<FocusUsageHour>().All(hour => hour.Duration == TimeSpan.Zero));
            Assert.IsTrue(((TextBlock)window.FindName("HomeFocusUsageStatusText")).Text.Contains("아직 집중 기록"));

            var start = DateTime.UtcNow.AddMinutes(-10);
            results.Begin(start, 0);
            Await(results.PendingSave);
            Refresh(window);
            var expected = SqliteFocusUsageRepository.Aggregate(DateTime.Today, DateTime.UtcNow, [], results.ActiveSession);
            Assert.AreEqual(expected.Total.TotalSeconds,
                bars.Items.Cast<FocusUsageHour>().Sum(hour => hour.Duration.TotalSeconds), 2);
            var ended = DateTime.UtcNow.AddMinutes(-2);
            Await(results.CompleteFocusSessionAsync(ended));
            Refresh(window);
            expected = SqliteFocusUsageRepository.Aggregate(DateTime.Today, DateTime.UtcNow,
                [new FocusSessionRecord("expected", start, ended)]);
            Assert.AreEqual(expected.Total.TotalSeconds,
                bars.Items.Cast<FocusUsageHour>().Sum(hour => hour.Duration.TotalSeconds), .01);

            // Fixed sample sessions exercise a full-height bar, partial hours and an empty gap.
            var day = DateTime.Today;
            DateTime At(int hour, int minute = 0) => day.AddHours(hour).AddMinutes(minute).ToUniversalTime();
            var sample = SqliteFocusUsageRepository.Aggregate(day, At(20, 35),
                [new("morning", At(8, 15), At(10, 20)), new("afternoon", At(13), At(14, 40)),
                    new("evening", At(18, 10), At(19, 30))], new("live", At(20)));
            Invoke(window, "ShowHomeFocusUsage", sample);
            foreach (var size in new[] { new Size(1100, 780), new Size(920, 780), new Size(640, 520) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("HomeScroll");
                scroll.ScrollToTop();
                root.UpdateLayout();
                Assert.IsTrue(scroll.ScrollableWidth <= 1);
                Assert.IsTrue(scroll.ScrollableHeight <= 1, "홈 전체가 세로 스크롤 없이 보여야 합니다.");
                var toggle = (Button)window.FindName("FocusModeToggleButton");
                var toggleBounds = toggle.TransformToAncestor(scroll).TransformBounds(new Rect(toggle.RenderSize));
                Assert.IsTrue(toggleBounds.Top >= 0 && toggleBounds.Bottom <= scroll.ViewportHeight + 1,
                    "카드가 커져도 집중모드 조작은 첫 화면에 보여야 합니다.");
                SavePreview(root, $"focus-chart-home-{size.Width}.png", size);
                var chart = (FrameworkElement)window.FindName("HomeFocusUsageChart");
                chart.BringIntoView();
                root.UpdateLayout();
                var chartBounds = chart.TransformToAncestor(scroll).TransformBounds(new Rect(chart.RenderSize));
                Assert.IsTrue(chartBounds.Top >= -1 && chartBounds.Bottom <= scroll.ViewportHeight + 1);
                Assert.IsTrue(chartBounds.Left >= -1 && chartBounds.Right <= scroll.ViewportWidth + 1);
                Assert.IsTrue(bars.Items.Cast<FocusUsageHour>().All(hour => hour.BarHeight is >= 0 and <= 88));
                SavePreview(root, $"focus-chart-visible-{size.Width}.png", size);
            }
            using (var connection = db.CreateOpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TABLE focus_sessions;";
                command.ExecuteNonQuery();
            }
            Refresh(window);
            Assert.IsTrue(((TextBlock)window.FindName("HomeFocusUsageStatusText")).Text.Contains("불러오지 못했어요"),
                "조회 실패를 기록 없는 날로 표시하면 안 됩니다.");
            db.Initialize();
            Refresh(window);
            Assert.IsTrue(bars.Items.Cast<FocusUsageHour>().All(hour => hour.Duration == TimeSpan.Zero));
            Assert.IsTrue(((TextBlock)window.FindName("HomeFocusUsageStatusText")).Text.Contains("아직 집중 기록"));
        }
        finally { window.Close(); }
    });

    private static object? Invoke(DashboardWindow window, string name, params object[] args) =>
        typeof(DashboardWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static void Refresh(DashboardWindow window) => Await((Task)Invoke(window, "RefreshHomeFocusUsageAsync")!);

    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.IsTrue(task.IsCompleted, "Chart refresh timed out.");
        task.GetAwaiter().GetResult();
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF chart validation timed out.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    private static void SavePreview(FrameworkElement root, string name, Size size)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private sealed class StubTracker : IActiveWindowTracker
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

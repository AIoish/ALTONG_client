using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ReportSummaryLoadingUiTests
{
    [DataTestMethod]
    [DataRow("success")]
    [DataRow("empty")]
    [DataRow("failure")]
    public void SaveAvailabilityHint_FollowsLoadingAndFitsSmallWindow(string outcome) => RunSta(() =>
    {
        using var db = Altong.Client.Data.SqliteDatabase.CreateInMemory();
        db.Initialize();
        var provider = new ControlledProvider();
        var now = DateTime.UtcNow;
        var result = new SessionResult(now.AddMinutes(-25), now, [], [], true) { SessionId = "save-hint" };
        var report = new SessionReportWindow(result, new Altong.Client.Data.Repositories.SqliteSessionReportRepository(db),
            summaryProvider: provider);
        try
        {
            report.Show();
            report.Width = 480;
            report.Height = 380;
            report.UpdateLayout();
            var hint = (TextBlock)report.FindName("SaveAvailabilityText");
            var save = (Button)report.FindName("SaveReportButton");
            Assert.AreEqual(Visibility.Visible, hint.Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((TextBlock)report.FindName("SaveStatusText")).Visibility);
            Assert.IsFalse(save.IsEnabled);
            var hintBounds = hint.TransformToAncestor(report).TransformBounds(new Rect(hint.RenderSize));
            var buttonBounds = save.TransformToAncestor(report).TransformBounds(new Rect(save.RenderSize));
            Assert.IsTrue(hintBounds.Bottom <= buttonBounds.Top && hintBounds.Right <= report.ActualWidth);
            Assert.IsTrue(buttonBounds.Bottom <= report.ActualHeight);
            if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(480, 380, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(report);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, "report-save-hint.png"));
                encoder.Save(output);
            }
            if (outcome == "failure") provider.Pending.SetException(new IOException("controlled failure"));
            else provider.Pending.SetResult(new(outcome == "empty" ? [] : [new("summary", "요약 결과", false, null)]));
            Until(() => save.IsEnabled);
            Assert.AreEqual(Visibility.Collapsed, hint.Visibility);
            Assert.AreEqual(Visibility.Visible, ((TextBlock)report.FindName("SaveStatusText")).Visibility);
        }
        finally { report.Close(); }
    });

    [TestMethod]
    public void InitialLoading_LabelsPreview_AndRetryReplacesItWithSummary() => RunSta(() =>
    {
        var provider = new ControlledProvider();
        var now = DateTime.UtcNow;
        var result = new SessionResult(now.AddMinutes(-25), now,
            [new NotificationRecord("n1", "Slack", "사용자", "원본 제목", "원본 본문", now, false)], [], true)
            { SessionId = "loading" };
        var report = Create(result, provider);
        try
        {
            report.Width = 480;
            report.Height = 380;
            report.Show();
            report.UpdateLayout();
            Assert.AreEqual("AI가 알림을 요약하고 있어요", Status(report).Text);
            Assert.AreEqual(Visibility.Visible, Indicator(report).Visibility);
            Assert.AreEqual("원본 알림 미리보기", ((TextBlock)report.FindName("LegacySummaryLabel")).Text);
            Assert.AreEqual(Visibility.Visible, ((StackPanel)report.FindName("LegacySummaryPanel")).Visibility);
            Assert.IsTrue(((Button)report.FindName("ReportCloseButton")).IsEnabled);
            Assert.IsTrue(((Button)report.FindName("SkipSaveButton")).IsEnabled);
            var scroll = (ScrollViewer)report.FindName("ReportScroll");
            var panel = (Border)report.FindName("SummaryStatusPanel");
            var bounds = panel.TransformToAncestor(scroll).TransformBounds(new Rect(panel.RenderSize));
            Assert.IsTrue(bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight + 1,
                "작은 창에서도 최초 화면에 AI 처리 상태가 보여야 합니다.");
            report.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            report.UpdateLayout();
            var progress = (ProgressBar)report.FindName("SummaryProgress");
            Assert.IsFalse(progress.IsIndeterminate);
            Assert.AreEqual(Visibility.Collapsed, progress.Visibility);
            Assert.AreEqual(Visibility.Visible, ((Grid)Indicator(report)).Children.OfType<Border>().Single().Visibility,
                "동작 줄이기에서는 움직임 없이 처리 중임을 표시해야 합니다.");

            provider.Pending.SetException(new IOException("controlled failure"));
            Until(() => Reload(report).IsEnabled);
            Assert.AreEqual(Visibility.Collapsed, Indicator(report).Visibility);
            Assert.AreEqual(Visibility.Visible, Retry(report).Visibility);

            provider.Pending = ControlledProvider.NewPending();
            Retry(report).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(2, provider.Reads);
            Assert.AreEqual(Visibility.Visible, Indicator(report).Visibility);
            provider.Pending.SetResult(new([new("summary", "생성된 요약", false, null) { AppName = "Slack", GroupId = "group" }]));
            Until(() => Reload(report).IsEnabled);
            Assert.AreEqual(Visibility.Collapsed, panel.Visibility);
            Assert.AreEqual(Visibility.Collapsed, Indicator(report).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((StackPanel)report.FindName("LegacySummaryPanel")).Visibility);
            Assert.AreEqual("생성된 요약", Cards(report).Items.Single().Summary.Text);
        }
        finally { report.Close(); }
    });

    [TestMethod]
    public void Refresh_KeepsExistingSummaryAndScheduleEdits_OnEmptyFailureAndRetry() => RunSta(() =>
    {
        var provider = new ControlledProvider();
        var now = DateTime.UtcNow;
        var summary = new FakeReportSummaryProvider().ReadAsync("refresh", CancellationToken.None).Result.Items[0]
            with { GroupId = "existing-group" };
        var report = Create(new(now.AddMinutes(-25), now, [], [], true)
            { SessionId = "refresh", SummaryItems = [summary] }, provider);
        try
        {
            var original = Cards(report).Items.Single();
            original.IsEditing = true;
            original.Title = "편집 중인 일정";
            Reload(report).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual("AI 요약을 갱신하고 있어요", Status(report).Text);
            Assert.AreSame(original, Cards(report).Items.Single());
            provider.Pending.SetResult(new([]));
            Until(() => Reload(report).IsEnabled);
            StringAssert.Contains(Status(report).Text, "기존 요약");
            Assert.AreSame(original, Cards(report).Items.Single());

            provider.Pending = ControlledProvider.NewPending();
            Reload(report).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            provider.Pending.SetException(new IOException("controlled failure"));
            Until(() => Reload(report).IsEnabled);
            Assert.AreEqual(Visibility.Visible, Retry(report).Visibility);
            Assert.AreSame(original, Cards(report).Items.Single());
            Assert.IsTrue(original.IsEditing);

            provider.Pending = ControlledProvider.NewPending();
            Retry(report).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            provider.Pending.SetResult(new([summary]));
            Until(() => Reload(report).IsEnabled);
            Assert.AreSame(original, Cards(report).Items.Single());
            Assert.AreEqual("편집 중인 일정", original.Title);
            Assert.AreEqual(Visibility.Collapsed, ((Border)report.FindName("SummaryStatusPanel")).Visibility);
        }
        finally { report.Close(); }
    });

    [TestMethod]
    public void EmptyResult_IsDistinctFromFailure_AndClosingCancelsPendingLoad() => RunSta(() =>
    {
        var provider = new ControlledProvider();
        var now = DateTime.UtcNow;
        var report = Create(new(now.AddMinutes(-25), now, [], [], true) { SessionId = "empty" }, provider);
        try
        {
            provider.Pending.SetResult(new([]));
            Until(() => Reload(report).IsEnabled);
            Assert.AreEqual("요약할 알림이 없습니다.", Status(report).Text);
            Assert.AreEqual(Visibility.Collapsed, Indicator(report).Visibility);
            Assert.AreEqual(Visibility.Collapsed, Retry(report).Visibility);

            provider.Pending = ControlledProvider.NewPending();
            Reload(report).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var loadingText = Status(report).Text;
            report.Close();
            Assert.IsTrue(provider.LastToken.IsCancellationRequested);
            provider.Pending.SetResult(new([new("late", "늦게 도착한 요약", false, null)]));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(loadingText, Status(report).Text, "닫힌 리포트를 뒤늦게 갱신하면 안 됩니다.");
        }
        finally { report.Close(); }
    });

    private static SessionReportWindow Create(SessionResult result, IReportSummaryProvider provider)
    {
        var folder = Path.Combine(Path.GetTempPath(), "altong-summary-loading-" + Guid.NewGuid().ToString("N"));
        return new(result, summaryProvider: provider, calendar: new DashboardCalendarStore(Path.Combine(folder, "calendar.json")),
            summaryStatePath: Path.Combine(folder, "state.json"));
    }

    private static TextBlock Status(SessionReportWindow report) => (TextBlock)report.FindName("SummaryLoadStatusText");
    private static FrameworkElement Indicator(SessionReportWindow report) => (FrameworkElement)report.FindName("SummaryLoadingIndicator");
    private static Button Reload(SessionReportWindow report) => (Button)report.FindName("ReloadSummaryButton");
    private static Button Retry(SessionReportWindow report) => (Button)report.FindName("RetrySummaryButton");
    private static NotificationSummaryCalendarView Cards(SessionReportWindow report) =>
        (NotificationSummaryCalendarView)((ContentControl)report.FindName("CalendarSummaryHost")).Content;

    private sealed class ControlledProvider : IReportSummaryProvider
    {
        public static TaskCompletionSource<ReportSummarySupply> NewPending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReportSummarySupply> Pending = NewPending();
        public int Reads;
        public CancellationToken LastToken;
        public Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken)
        {
            Reads++;
            LastToken = cancellationToken;
            return Pending.Task.WaitAsync(cancellationToken);
        }
    }

    private static void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.IsTrue(ready(), "요약 상태 전환 대기 시간 초과");
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }
}

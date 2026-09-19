using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Altong.Client.Models;
using Altong.Client.Services;

namespace Altong.Client;

public partial class DebugConsoleWindow : Window
{
    private readonly IActiveWindowTracker _tracker;
    private readonly DispatcherTimer _uiTimer;

    public DebugConsoleWindow(IActiveWindowTracker tracker)
    {
        InitializeComponent();

        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _tracker.ContextChanged += Tracker_ContextChanged;

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiTimer.Tick += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_tracker.CurrentContext.ActiveProcess))
            {
                DurationText.Text = $"⏱️ {_tracker.CurrentContext.DurationSeconds}초 지속";
            }
        };
        _uiTimer.Start();

        UpdateContextView(_tracker.CurrentContext);
        AppendLog($"[시스템] 디버그 모니터 연결됨. 초기 맥락: {_tracker.CurrentContext.ActiveProcess}");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 닫기 대신 숨기기 (다시 열었을 때 로그 보존)
        if (System.Windows.Application.Current is App { IsShuttingDown: false })
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _uiTimer.Stop();
        _tracker.ContextChanged -= Tracker_ContextChanged;
        base.OnClosing(e);
    }

    private CurrentContext? _lastContext;
    private DateTimeOffset _lastContextEnteredAt = DateTimeOffset.UtcNow;

    private void Tracker_ContextChanged(object? sender, CurrentContext context)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateContextView(context);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            // 직전 맥락이 존재하고, 다른 창으로 전환되는 경우 직전 세션 종료 [OUT] 선출력
            if (_lastContext is not null && !string.IsNullOrEmpty(_lastContext.ActiveProcess) &&
                (_lastContext.ActiveProcess != context.ActiveProcess || _lastContext.WindowTitle != context.WindowTitle))
            {
                int previousTotalDuration = Math.Max(0, (int)(now - _lastContextEnteredAt).TotalSeconds);
                AppendLog($"[{DateTime.Now:HH:mm:ss}] [OUT]     {_lastContext.ActiveProcess} ('{TruncateTitle(_lastContext.WindowTitle)}') 최종 체류: {previousTotalDuration}s");
            }

            _lastContext = context;
            _lastContextEnteredAt = now;

            string combi = context.RecentProcesses is { Count: > 0 }
                ? string.Join(", ", context.RecentProcesses)
                : "없음";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [CONFIRM] {context.ActiveProcess} ('{TruncateTitle(context.WindowTitle)}') | 콤비: [{combi}]");
        });
    }

    private void UpdateContextView(CurrentContext context)
    {
        if (string.IsNullOrEmpty(context.ActiveProcess))
        {
            ProcessText.Text = "🎯 활성 프로세스: 대기 중...";
            TitleText.Text = "📝 창 제목: -";
            DurationText.Text = "⏱️ 0초 지속";
            CombiText.Text = "👥 콤비 앱 큐: [없음]";
            return;
        }

        ProcessText.Text = $"🎯 활성 프로세스: {context.ActiveProcess}";
        TitleText.Text = $"📝 창 제목: {context.WindowTitle}";
        DurationText.Text = $"⏱️ {context.DurationSeconds}초 지속";

        string combi = context.RecentProcesses is { Count: > 0 }
            ? string.Join(", ", context.RecentProcesses)
            : "없음";
        CombiText.Text = $"👥 콤비 앱 큐: [{combi}]";
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private static string TruncateTitle(string title, int maxLength = 65)
    {
        if (string.IsNullOrEmpty(title) || title.Length <= maxLength)
        {
            return title ?? string.Empty;
        }

        int prefixLength = (maxLength - 3) / 2 + 1;
        int suffixLength = (maxLength - 3) / 2;

        return string.Concat(
            title.AsSpan(0, prefixLength),
            "...",
            title.AsSpan(title.Length - suffixLength, suffixLength));
    }
}

using System.ComponentModel;
using System.Windows;
using Altong.Client.Models;
using Altong.Client.Services;

namespace Altong.Client;

public partial class DebugConsoleWindow : Window
{
    private readonly IActiveWindowTracker _tracker;

    public DebugConsoleWindow(IActiveWindowTracker tracker)
    {
        InitializeComponent();

        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _tracker.ContextChanged += Tracker_ContextChanged;

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

        _tracker.ContextChanged -= Tracker_ContextChanged;
        base.OnClosing(e);
    }

    private void Tracker_ContextChanged(object? sender, CurrentContext context)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateContextView(context);
            string combi = context.RecentProcesses is { Count: > 0 }
                ? string.Join(", ", context.RecentProcesses)
                : "없음";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] [CONFIRM] {context.ActiveProcess} | 콤비: [{combi}] | 체류: {context.DurationSeconds}s");
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
}

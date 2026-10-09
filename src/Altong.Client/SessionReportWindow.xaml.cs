using System.Windows;
using Altong.Client.Services;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services.Calendar;

namespace Altong.Client;

public partial class SessionReportWindow : Window
{
    private SessionResult _result;
    private readonly SqliteSessionReportRepository? _reports;
    private bool _saving;
    private bool _loadingSummaries;
    private bool _closed;
    private readonly CancellationTokenSource _summaryLifetime = new();
    private readonly IReportSummaryProvider? _summaryProvider;
    private readonly bool _demoMode;

    public SessionReportWindow(SessionResult result, SqliteSessionReportRepository? reports = null, bool isSaved = false,
        IReportSummaryProvider? summaryProvider = null)
    {
        _result = result;
        _reports = reports;
        _demoMode = Environment.GetEnvironmentVariable("ALTONG_CALENDAR_DEMO") == "1";
        var summaryFolder = Environment.GetEnvironmentVariable("ALTONG_SUMMARY_DIR");
        _summaryProvider = isSaved ? null : summaryProvider ?? (_demoMode ? new FakeReportSummaryProvider()
            : string.IsNullOrWhiteSpace(summaryFolder) ? null : new JsonReportSummaryProvider(summaryFolder));
        InitializeComponent();
        Closing += (_, e) => e.Cancel = _saving;
        Closed += (_, _) => { _closed = true; _summaryLifetime.Cancel(); };
        if (isSaved || reports is null || result.SessionId is null)
        {
            SaveReportButton.Visibility = Visibility.Collapsed;
            SkipSaveButton.Content = "확인";
            SaveStatusText.Text = isSaved ? "캘린더에 저장된 리포트입니다." : "";
        }
        else
            SaveStatusText.Text = "이 리포트를 캘린더에 저장하시겠습니까? 닫으면 새 리포트는 저장되지 않습니다.";
        SourceInitialized += (_, _) => FitToWorkArea();
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        };
        var maximumSeconds = result.Apps.Count == 0 ? 0 : result.Apps.Max(app => app.Seconds);
        DataContext = result with
        {
            Apps = result.Apps.Select(app => app with
            {
                Percentage = maximumSeconds > 0 ? app.Seconds / maximumSeconds * 100 : 0
            }).ToArray()
        };
        if (result.SummaryItems.Count > 0) ShowSummaryItems(result.SummaryItems);
        else if (_summaryProvider is not null && result.SessionId is not null) _ = LoadSummaryItemsAsync();
    }

    private void ShowSummaryItems(IReadOnlyList<NotificationSummaryItem> items)
    {
        ReportSummaryItemsControl.ItemsSource = items;
        ReportSummaryItemsControl.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LegacySummaryPanel.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadSummaryItemsAsync()
    {
        if (_loadingSummaries || _saving || _closed || _summaryProvider is null || _result.SessionId is null) return;
        _loadingSummaries = true;
        SaveReportButton.IsEnabled = false;
        SummaryLoadStatusText.Visibility = Visibility.Visible;
        SummaryLoadStatusText.Text = "알림 요약을 불러오고 있습니다.";
        try
        {
            var supply = await _summaryProvider.ReadAsync(_result.SessionId, _summaryLifetime.Token);
            if (_closed) return;
            if (!supply.IsDemo) _result = _result with { SummaryItems = supply.Items };
            ShowSummaryItems(supply.Items);
            SummaryLoadStatusText.Text = supply.Items.Count == 0 ? "아직 도착한 알림 요약이 없습니다. 대시보드의 알림 확인에서 확인해 주세요." : "";
            SummaryLoadStatusText.Visibility = supply.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_closed) SummaryLoadStatusText.Text = "알림 요약을 불러오지 못했습니다. 기본 리포트를 저장하고 대시보드의 알림 확인에서 다시 확인할 수 있습니다.";
        }
        finally
        {
            _loadingSummaries = false;
            if (!_closed) SaveReportButton.IsEnabled = true;
        }
    }

    private void FitToWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var available = transform.Transform(new Vector(area.Width, area.Height));
        double width = Math.Max(1, available.X - 24);
        double height = Math.Max(1, available.Y - 24);
        MinWidth = Math.Min(520, width);
        MinHeight = Math.Min(420, height);
        Width = Math.Min(760, width);
        Height = Math.Min(780, height);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving) Close();
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _loadingSummaries || _reports is null) return;
        _saving = true;
        SaveReportButton.IsEnabled = false;
        SkipSaveButton.IsEnabled = false;
        SaveStatusText.Text = "리포트를 저장하고 있습니다.";
        try
        {
            await Task.Run(() => _reports.SaveAsync(_result));
            _saving = false;
            Close();
        }
        catch (Exception)
        {
            _saving = false;
            SaveReportButton.IsEnabled = true;
            SkipSaveButton.IsEnabled = true;
            SaveStatusText.Text = "저장하지 못했습니다. 다시 저장하거나 저장하지 않고 닫을 수 있습니다.";
        }
    }

    private void CopyQuickReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string draft }) return;
        try
        {
            System.Windows.Clipboard.SetText(draft);
            CopyStatusText.Text = "문구를 복사했어요. 전송 전에 내용을 확인하세요.";
        }
        catch (Exception)
        {
            CopyStatusText.Text = "클립보드에 복사하지 못했어요. 잠시 후 다시 시도해 주세요.";
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
}

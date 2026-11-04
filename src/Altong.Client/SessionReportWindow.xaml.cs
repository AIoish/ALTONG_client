using System.Windows;
using System.IO;
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
    private readonly DashboardCalendarStore _calendar;
    private readonly DashboardSummaryStateStore _summaryState;
    private readonly NotificationSummaryCalendarView _summaryCards;
    private int _notificationDisplayLimit = 50;
    private int _appDisplayLimit = 50;

    public SessionReportWindow(SessionResult result, SqliteSessionReportRepository? reports = null, bool isSaved = false,
        IReportSummaryProvider? summaryProvider = null, DashboardCalendarStore? calendar = null,
        string? summaryStatePath = null)
    {
        _result = result;
        _reports = reports;
        _demoMode = Environment.GetEnvironmentVariable("ALTONG_CALENDAR_DEMO") == "1";
        var summaryFolder = Environment.GetEnvironmentVariable("ALTONG_SUMMARY_DIR");
        _summaryProvider = summaryProvider ?? (_demoMode ? new FakeReportSummaryProvider()
            : string.IsNullOrWhiteSpace(summaryFolder) ? null : new JsonReportSummaryProvider(summaryFolder));
        _calendar = calendar ?? new DashboardCalendarStore();
        _summaryState = new(summaryStatePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "dashboard-summary-state.json"));
        _summaryState.Load();
        InitializeComponent();
        Title = isSaved ? "저장된 집중 리포트" : "집중 완료 · 집중 리포트";
        ReportHeadingText.Text = "집중 리포트";
        ReportBadgeText.Text = isSaved ? "보관됨" : "집중 완료";
        _summaryCards = new([], _demoMode);
        _summaryCards.ScheduleCorrected += SaveCorrection;
        _summaryCards.ScheduleSaveRequested += SaveSchedule;
        CalendarSummaryHost.Content = _summaryCards;
        _calendar.Changed += Calendar_Changed;
        ReloadSummaryButton.Visibility = _summaryProvider is null || result.SessionId is null ? Visibility.Collapsed : Visibility.Visible;
        Closing += (_, e) => e.Cancel = _saving;
        Closed += (_, _) =>
        {
            _closed = true;
            _summaryLifetime.Cancel();
            _calendar.Changed -= Calendar_Changed;
        };
        if (isSaved || reports is null || result.SessionId is null)
        {
            SaveReportButton.Visibility = Visibility.Collapsed;
            SkipSaveButton.Content = "확인";
            SaveStatusText.Text = isSaved ? "저장된 리포트" : "";
        }
        else
            SaveStatusText.Text = "저장 전";
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
        UpdateDetailLists();
    }

    private void UpdateDetailLists()
    {
        NotificationItemsControl.ItemsSource = _result.Notifications.Take(_notificationDisplayLimit).ToArray();
        AppItemsControl.ItemsSource = ((SessionResult)DataContext).Apps.Take(_appDisplayLimit).ToArray();
        MoreNotificationsButton.Visibility = _result.Notifications.Count > _notificationDisplayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreAppsButton.Visibility = _result.Apps.Count > _appDisplayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreNotificationsButton.Content = $"알림 더 보기 · {Math.Min(_notificationDisplayLimit, _result.Notifications.Count)}/{_result.Notifications.Count}";
        MoreAppsButton.Content = $"앱 더 보기 · {Math.Min(_appDisplayLimit, _result.Apps.Count)}/{_result.Apps.Count}";
    }

    private void MoreNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notificationDisplayLimit += 50;
        UpdateDetailLists();
    }

    private void MoreApps_Click(object sender, RoutedEventArgs e)
    {
        _appDisplayLimit += 50;
        UpdateDetailLists();
    }

    private void ShowSummaryItems(IReadOnlyList<NotificationSummaryItem> items)
    {
        var existing = _summaryCards.Items.ToDictionary(row => row.Summary.SummaryId);
        _summaryCards.SetItems(items.Select(item => existing.TryGetValue(item.SummaryId, out var row) && row.Summary == item
            ? row : new CalendarSummaryItemViewModel(item, _result.SessionId, correction: _result.SessionId is { } session
                ? _summaryState.Get(session, item.SummaryId)?.Correction ?? _calendar.Get(session, item.SummaryId)?.Schedule : null)).ToArray());
        UpdateSavedStates();
        CalendarSummaryHost.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LegacySummaryPanel.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Calendar_Changed(object? sender, EventArgs e) => UpdateSavedStates();

    private void UpdateSavedStates()
    {
        foreach (var row in _summaryCards.Items)
        {
            var saved = row.SessionId is { } session ? _calendar.Get(session, row.Summary.SummaryId) : null;
            if (row.IsSaved && saved is null) row.Status = "일정 삭제됨 · 다시 저장할 수 있어요.";
            row.IsSaved = saved?.Schedule == row.Draft;
        }
    }

    private void SaveCorrection(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null) return;
        try { _summaryState.Save(new(row.SessionId, row.Summary.SummaryId, row.Draft)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { row.Status = "보완 내용 저장 실패 · 다시 적용하세요."; }
    }

    private void SaveSchedule(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null || row.IsBusy || row.IsSaved) return;
        row.IsBusy = true;
        try
        {
            _calendar.Save(new(row.SessionId, row.Summary.SummaryId, row.Summary.AppName, row.Draft));
            row.IsSaved = true;
            row.Status = "캘린더에 저장했어요.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { row.Status = _calendar.LoadWarning ?? "일정 저장 실패 · 다시 시도하세요."; }
        finally { row.IsBusy = false; }
    }

    private async void ReloadSummary_Click(object sender, RoutedEventArgs e) => await LoadSummaryItemsAsync();

    private async Task LoadSummaryItemsAsync()
    {
        if (_loadingSummaries || _saving || _closed || _summaryProvider is null || _result.SessionId is null) return;
        _loadingSummaries = true;
        SaveReportButton.IsEnabled = false;
        ReloadSummaryButton.IsEnabled = false;
        SummaryLoadStatusText.Visibility = Visibility.Visible;
        SummaryLoadStatusText.Text = "요약 불러오는 중…";
        try
        {
            var supply = await _summaryProvider.ReadAsync(_result.SessionId, _summaryLifetime.Token);
            if (_closed) return;
            if (supply.Items.Count > 0)
            {
                if (!supply.IsDemo) _result = _result with { SummaryItems = supply.Items };
                ShowSummaryItems(supply.Items);
            }
            SummaryLoadStatusText.Text = supply.Items.Count == 0 ? "새 요약 없음 · 다시 불러올 수 있어요." : "";
            SummaryLoadStatusText.Visibility = supply.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_closed) SummaryLoadStatusText.Text = "요약 불러오기 실패 · 다시 시도하세요.";
        }
        finally
        {
            _loadingSummaries = false;
            if (!_closed) { SaveReportButton.IsEnabled = true; ReloadSummaryButton.IsEnabled = true; }
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
        MinWidth = Math.Min(480, width);
        MinHeight = Math.Min(380, height);
        Width = Math.Min(600, width);
        Height = Math.Min(660, height);
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
        SaveStatusText.Text = "리포트 저장 중…";
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
            SaveStatusText.Text = "저장 실패 · 다시 시도하세요.";
        }
    }

    private void CopyQuickReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string draft }) return;
        try
        {
            System.Windows.Clipboard.SetText(draft);
            CopyStatusText.Text = "복사 완료";
        }
        catch (Exception)
        {
            CopyStatusText.Text = "복사 실패 · 다시 시도하세요.";
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

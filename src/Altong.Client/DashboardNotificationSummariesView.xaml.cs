using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services.Calendar;

namespace Altong.Client;

public partial class DashboardNotificationSummariesView : System.Windows.Controls.UserControl
{
    private readonly SqliteSessionReportRepository _reports;
    private readonly IReportSummaryProvider? _provider;
    private readonly DashboardSummaryStateStore _state;
    private readonly DashboardCalendarStore _calendar;
    private readonly NotificationSummaryCalendarView _cards;
    private readonly bool _demo;
    private Dictionary<(string Session, string Summary), CalendarSummaryItemViewModel> _rows = [];
    private int _displayLimit = 20;
    private bool _loading;
    private bool _stopped;
    private string? _selectedApp;
    public event EventHandler? GroupsChanged;
    public event EventHandler? BackRequested;
    private IEnumerable<CalendarSummaryItemViewModel> AvailableRows => _rows.Values.Where(row =>
        _state.Get(row.SessionId!, row.Summary.SummaryId)?.Dismissed != true);
    public int UnassignedCount => AvailableRows.Count(row => string.IsNullOrWhiteSpace(row.Summary.AppName));
    public IReadOnlyDictionary<string, int> AppCounts => AvailableRows
        .Where(row => !string.IsNullOrWhiteSpace(row.Summary.AppName))
        .GroupBy(row => row.Summary.AppName!.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public DashboardNotificationSummariesView(SqliteSessionReportRepository reports,
        IReportSummaryProvider? summaryProvider = null, string? statePath = null, DashboardCalendarStore? calendar = null)
    {
        _reports = reports;
        _demo = summaryProvider is null && Environment.GetEnvironmentVariable("ALTONG_CALENDAR_DEMO") == "1";
        var folder = Environment.GetEnvironmentVariable("ALTONG_SUMMARY_DIR");
        _provider = summaryProvider ?? (_demo ? new FakeReportSummaryProvider()
            : string.IsNullOrWhiteSpace(folder) ? null : new JsonReportSummaryProvider(folder));
        _state = new(statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "dashboard-summary-state.json"));
        _state.Load();
        _calendar = calendar ?? new DashboardCalendarStore();
        InitializeComponent();
        _cards = new NotificationSummaryCalendarView([], _demo);
        _cards.ScheduleCorrected += SaveCorrection;
        _cards.SummaryDismissed += DismissSummary;
        _cards.ScheduleSaveRequested += SaveSchedule;
        _calendar.Changed += Calendar_Changed;
        SummaryCardsHost.Content = _cards;
        ShowRows();
    }

    public void Stop()
    {
        _stopped = true;
        _calendar.Changed -= Calendar_Changed;
    }

    private void Calendar_Changed(object? sender, EventArgs e) => UpdateSavedStates();

    private void UpdateSavedStates()
    {
        foreach (var row in _rows.Values)
        {
            var saved = row.SessionId is { } session ? _calendar.Get(session, row.Summary.SummaryId) : null;
            if (row.IsSaved && saved is null) row.Status = "캘린더에서 삭제한 일정입니다. 다시 저장할 수 있어요.";
            row.IsSaved = saved?.Schedule == row.Draft;
        }
    }

    public void SelectApp(string? appName)
    {
        _selectedApp = string.IsNullOrWhiteSpace(appName) ? null : appName.Trim();
        _displayLimit = 20;
        SummaryHeadingText.Text = _selectedApp is null ? "앱 미지정 AI 요약" : "AI 요약";
        CloseSummaryButton.Visibility = _selectedApp is null ? Visibility.Visible : Visibility.Collapsed;
        ShowRows();
    }

    public async Task RefreshAsync()
    {
        if (_loading || _stopped) return;
        _loading = true;
        ReloadSummaryButton.IsEnabled = false;
        try
        {
            // Capture UI state before leaving the Dispatcher. Keep old rows on a malformed AI response.
            var previous = _rows.Values.GroupBy(row => row.SessionId!).ToDictionary(group => group.Key,
                group => group.Select(row => row.Summary).ToArray());
            var data = await Task.Run(async () =>
            {
                var sessions = _demo ? new[] { new DashboardSummarySession("demo-preview", DateTime.UtcNow, []) }
                    : await _reports.ReadSummarySessionsAsync();
                var batches = new List<DashboardSummarySession>();
                bool failed = false;
                foreach (var session in sessions)
                {
                    var items = session.Items;
                    if (_provider is not null)
                    {
                        try
                        {
                            var supplied = await _provider.ReadAsync(session.SessionId, CancellationToken.None);
                            if (supplied.Items.Count > 0) items = supplied.Items;
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                        {
                            failed = true;
                            if (previous.TryGetValue(session.SessionId, out var old)) items = old;
                        }
                    }
                    batches.Add(session with { Items = items });
                }
                return (Batches: batches, Failed: failed);
            });
            if (_stopped) return;
            var updated = new Dictionary<(string, string), CalendarSummaryItemViewModel>();
            foreach (var session in data.Batches)
                foreach (var item in session.Items)
                {
                    var key = (session.SessionId, item.SummaryId);
                    if (_rows.TryGetValue(key, out var existing) && existing.Summary == item)
                        updated[key] = existing;
                    else
                        updated[key] = new(item, session.SessionId, session.EndedAt,
                            _state.Get(session.SessionId, item.SummaryId)?.Correction ??
                            _calendar.Get(session.SessionId, item.SummaryId)?.Schedule);
                }
            _rows = updated;
            UpdateSavedStates();
            ShowRows();
            SetStatus(_state.LoadWarning ?? _calendar.LoadWarning ?? (data.Failed
                ? "일부 요약을 불러오지 못했습니다. 기존 내용은 유지하며, 새로고침으로 다시 시도할 수 있어요." : ""));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!_stopped) SetStatus("요약을 불러오지 못했습니다. 잠시 후 새로고침해 주세요.");
        }
        finally
        {
            _loading = false;
            if (!_stopped) ReloadSummaryButton.IsEnabled = true;
        }
    }

    private void ShowRows()
    {
        var rows = AvailableRows.Where(row => _selectedApp is null
            ? string.IsNullOrWhiteSpace(row.Summary.AppName)
            : string.Equals(row.Summary.AppName?.Trim(), _selectedApp, StringComparison.OrdinalIgnoreCase)).ToArray();
        _cards.SetItems(rows.Take(_displayLimit).ToArray());
        SummaryCountText.Text = $"요약 {rows.Length}개 · 일정 {rows.Count(row => row.IsScheduleRelated)}개";
        SummaryEmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreSummariesButton.Visibility = rows.Length > _displayLimit ? Visibility.Visible : Visibility.Collapsed;
        GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveCorrection(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null) return;
        try { _state.Save(new(row.SessionId, row.Summary.SummaryId, row.Draft)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.Status = "일정은 보완했지만 보완 내용을 저장하지 못했습니다. 다시 보완하여 적용해 주세요.";
        }
    }

    private void SaveSchedule(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null || row.IsBusy || row.IsSaved) return;
        row.IsBusy = true;
        try
        {
            _calendar.Save(new(row.SessionId, row.Summary.SummaryId, row.Summary.AppName, row.Draft));
            row.IsSaved = true;
            row.Status = "대시보드 캘린더에 저장했어요. 캘린더에서 일정을 확인해 주세요.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            row.Status = "일정을 저장하지 못했습니다. " + (_calendar.LoadWarning ?? "잠시 후 다시 시도해 주세요.");
        }
        finally { row.IsBusy = false; }
    }

    private void DismissSummary(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null) return;
        try
        {
            var current = _state.Get(row.SessionId, row.Summary.SummaryId);
            _state.Save(new(row.SessionId, row.Summary.SummaryId, current?.Correction, Dismissed: true));
            ShowRows();
            SetStatus("요약을 목록에서 제거했습니다. 원본 알림·리포트·저장한 일정은 유지됩니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("요약을 제거하지 못했습니다. 잠시 후 다시 시도해 주세요.");
        }
    }

    private void SetStatus(string text)
    {
        SummaryLoadStatusText.Text = text;
        SummaryLoadStatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ReloadSummary_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void LoadMoreSummaries_Click(object sender, RoutedEventArgs e)
    {
        _displayLimit += 20;
        ShowRows();
    }
}

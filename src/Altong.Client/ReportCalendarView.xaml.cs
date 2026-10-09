using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Altong.Client;

public partial class ReportCalendarView : System.Windows.Controls.UserControl
{
    private readonly SessionResultsService _results;
    private readonly DashboardCalendarStore _calendar;
    private readonly Action<string> _openCalendar;
    private IReadOnlyDictionary<DateTime, int> _scheduleCounts = new Dictionary<DateTime, int>();
    private readonly ObservableCollection<ReportListEntry> _entries = new();
    private IReadOnlyDictionary<DateTime, int> _counts = new Dictionary<DateTime, int>();
    private int _revision;
    private bool _subscribed;
    private bool _mutating;
    private bool _openingCalendar;
    public DateTime SelectedDate { get; private set; } = DateTime.Today;
    public DateTime DisplayMonth { get; private set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public ReportCalendarView(SessionResultsService results, DashboardCalendarStore? calendar = null, Action<string>? openCalendar = null)
    {
        _results = results;
        _calendar = calendar ?? new DashboardCalendarStore();
        _openCalendar = openCalendar ?? GoogleCalendarLink.Open;
        InitializeComponent();
        ReportList.ItemsSource = _entries;
        RenderCalendar();
        SizeChanged += (_, _) => UpdateLayoutForSize();
        Loaded += async (_, _) =>
        {
            if (!_subscribed)
            {
                _results.Reports.Changed += Reports_Changed;
                _calendar.Changed += Reports_Changed;
                _subscribed = true;
            }
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _revision++;
            if (_subscribed)
            {
                _results.Reports.Changed -= Reports_Changed;
                _calendar.Changed -= Reports_Changed;
                _subscribed = false;
            }
        };
    }

    private void Reports_Changed(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(async () => await RefreshAsync()));

    public async Task SelectDateAsync(DateTime date)
    {
        SelectedDate = date.Date;
        DisplayMonth = new(date.Year, date.Month, 1);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        int revision = ++_revision;
        var date = SelectedDate;
        var month = DisplayMonth;
        _entries.Clear();
        LoadMoreButton.Visibility = Visibility.Collapsed;
        EmptyStateText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        SelectedDateText.Text = date.ToString("yyyy년 M월 d일 dddd");
        ReportCountText.Text = "불러오는 중…";
        HistoryStatusText.Text = _calendar.LoadWarning ?? "";
        _scheduleCounts = _calendar.GetMonthCounts(month);
        var schedules = _calendar.GetDay(date);
        ScheduleList.ItemsSource = schedules;
        ScheduleList.Visibility = schedules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleEmptyStateText.Visibility = schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleCountText.Text = _calendar.LoadWarning is null ? $"저장한 일정 {schedules.Count}개" : "일정 조회 실패";
        ScheduleEmptyStateText.Text = _calendar.LoadWarning ?? "이 날짜에 저장한 일정이 없습니다.";
        RenderCalendar();
        try
        {
            var data = await Task.Run(async () =>
            {
                var counts = await _results.Reports.GetMonthCountsAsync(month);
                var entries = await _results.Reports.GetDayPageAsync(date);
                return (Counts: counts, Entries: entries);
            });
            if (revision != _revision) return;
            _counts = data.Counts;
            foreach (var entry in data.Entries) _entries.Add(entry);
            if (_entries.Count > 0) ReportList.ScrollIntoView(_entries[0]);
            RenderCalendar();
            UpdateListStatus();
        }
        catch (Exception)
        {
            if (revision != _revision) return;
            _counts = new Dictionary<DateTime, int>();
            ReportCountText.Text = "조회 실패";
            HistoryStatusText.Text = "리포트를 불러오지 못했습니다. 다시 불러오기를 눌러 주세요.";
            RetryButton.Visibility = Visibility.Visible;
            RenderCalendar();
        }
    }

    public async Task LoadMoreAsync()
    {
        int revision = _revision;
        var date = SelectedDate;
        int offset = _entries.Count;
        LoadMoreButton.IsEnabled = false;
        try
        {
            var page = await Task.Run(() => _results.Reports.GetDayPageAsync(date, offset));
            if (revision != _revision) return;
            foreach (var entry in page) _entries.Add(entry);
            UpdateListStatus();
        }
        catch (Exception)
        {
            if (revision == _revision) HistoryStatusText.Text = "추가 목록을 불러오지 못했습니다. 더 보기를 다시 눌러 주세요.";
        }
        finally { LoadMoreButton.IsEnabled = true; }
    }

    private void UpdateListStatus()
    {
        int count = _counts.GetValueOrDefault(SelectedDate);
        ReportCountText.Text = $"집중 리포트 {count}개 · {_entries.Count}개 표시";
        ReportList.Visibility = _entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.Visibility = _entries.Count < count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderCalendar()
    {
        MonthText.Text = DisplayMonth.ToString("yyyy년 M월");
        PreviousMonthButton.IsEnabled = DisplayMonth.Year > 1 || DisplayMonth.Month > 1;
        NextMonthButton.IsEnabled = DisplayMonth.Year < 9999 || DisplayMonth.Month < 12;
        DaysPanel.Children.Clear();
        int leading = (int)DisplayMonth.DayOfWeek;
        int days = DateTime.DaysInMonth(DisplayMonth.Year, DisplayMonth.Month);
        for (int index = 0; index < 42; index++)
        {
            int day = index - leading + 1;
            if (day < 1 || day > days) { DaysPanel.Children.Add(new Border()); continue; }
            var date = DisplayMonth.AddDays(day - 1);
            int count = _counts.GetValueOrDefault(date);
            int scheduleCount = _scheduleCounts.GetValueOrDefault(date);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = day.ToString(), HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Foreground = Brushes.White });
            content.Children.Add(new TextBlock { Text = count > 0 ? $"● {count}" : " ", FontSize = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = date == SelectedDate ? Brushes.White : new SolidColorBrush(Color.FromRgb(201, 171, 255)) });
            content.Children.Add(new TextBlock { Text = scheduleCount > 0 ? $"◆ {scheduleCount}" : " ", FontSize = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = date == SelectedDate ? Brushes.White : new SolidColorBrush(Color.FromRgb(128, 213, 203)) });
            var button = new System.Windows.Controls.Button { Content = content, Tag = date, MinHeight = 44, Padding = new Thickness(1, 3, 1, 3),
                Margin = new Thickness(2), ToolTip = $"{date:M월 d일} · 일정 {scheduleCount}개 · 리포트 {count}개",
                Background = date == SelectedDate ? new SolidColorBrush(Color.FromRgb(130, 61, 224)) : Brushes.Transparent,
                BorderBrush = date == DateTime.Today ? new SolidColorBrush(Color.FromRgb(201, 171, 255)) : Brushes.Transparent };
            AutomationProperties.SetName(button, $"{date:yyyy년 M월 d일}, 리포트 {count}개, 일정 {scheduleCount}개");
            button.Click += async (_, _) => await SelectDateAsync(date);
            DaysPanel.Children.Add(button);
        }
    }

    private void UpdateLayoutForSize()
    {
        bool stacked = ActualWidth < 720;
        ResponsiveGrid.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(340);
        ResponsiveGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 16);
        ResponsiveGrid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(ListCard, stacked ? 0 : 2);
        Grid.SetRow(ListCard, stacked ? 1 : 0);
        ListCard.Margin = new Thickness(0, stacked ? 16 : 0, 0, 0);
        ReportList.Height = Math.Clamp(ActualHeight - 155, 220, 560);
        ScheduleList.MaxHeight = Math.Clamp(ActualHeight - 260, 210, 340);
    }

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e) => await SelectDateAsync(DisplayMonth.AddMonths(-1));
    private async void NextMonth_Click(object sender, RoutedEventArgs e) => await SelectDateAsync(DisplayMonth.AddMonths(1));
    private async void Today_Click(object sender, RoutedEventArgs e) => await SelectDateAsync(DateTime.Today);
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private void OpenGoogleCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (_openingCalendar || sender is not System.Windows.Controls.Button { Tag: DashboardCalendarEntry entry } button) return;
        _openingCalendar = true;
        button.IsEnabled = false;
        try
        {
            _openCalendar(GoogleCalendarLink.Build(entry.Schedule));
            HistoryStatusText.Text = "Google Calendar 작성 화면을 열었어요. 내용을 확인하고 저장을 눌러 주세요.";
        }
        catch { HistoryStatusText.Text = "Google Calendar 작성 화면을 열지 못했습니다. 다시 시도해 주세요."; }
        finally { _openingCalendar = false; button.IsEnabled = true; }
    }

    private async void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: DashboardCalendarEntry entry } button) return;
        button.IsEnabled = false;
        try
        {
            _calendar.Delete(entry.SessionId, entry.SummaryId);
            await RefreshAsync();
            HistoryStatusText.Text = "대시보드에서 일정을 삭제했습니다.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            HistoryStatusText.Text = "일정을 삭제하지 못했습니다. " + (_calendar.LoadWarning ?? "다시 시도해 주세요.");
        }
        finally { button.IsEnabled = true; }
    }

    private async void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_mutating || sender is not System.Windows.Controls.Button { Tag: ReportListEntry entry } button) return;
        button.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _results.OpenReportAsync(entry.SessionId));
            if (result is null) { await RefreshAsync(); return; }
            var report = new SessionReportWindow(result, _results.Reports, entry.IsSaved) { Owner = Window.GetWindow(this) };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            report.Closed += (_, _) => closed.TrySetResult();
            report.Show();
            await closed.Task;
        }
        catch (Exception) { HistoryStatusText.Text = "리포트를 열지 못했습니다. 다시 시도해 주세요."; }
        finally { button.IsEnabled = true; }
    }

    public async Task DeleteReportAsync(string sessionId)
    {
        if (_mutating) return;
        _mutating = true;
        try
        {
            await Task.Run(() => _results.Reports.DeleteAsync(sessionId));
            await RefreshAsync();
            HistoryStatusText.Text = "리포트를 삭제했습니다.";
        }
        catch (Exception) { HistoryStatusText.Text = "삭제하지 못했습니다. 다시 시도해 주세요."; }
        finally { _mutating = false; }
    }

    private async void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ReportListEntry entry } button) return;
        button.IsEnabled = false;
        await DeleteReportAsync(entry.SessionId);
        button.IsEnabled = true;
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;

namespace Altong.Client;

public partial class ReportCalendarView : System.Windows.Controls.UserControl
{
    private readonly SessionResultsService _results;
    private readonly DashboardCalendarStore _calendar;
    private readonly Action<string> _openCalendar;
    private readonly IReportSummaryProvider? _summaryProvider;
    private readonly string? _summaryStatePath;
    private IReadOnlyDictionary<DateTime, int> _scheduleCounts = new Dictionary<DateTime, int>();
    private readonly ObservableCollection<ReportListEntry> _entries = new();
    private IReadOnlyDictionary<DateTime, int> _counts = new Dictionary<DateTime, int>();
    private int _revision;
    private bool _subscribed;
    private bool _mutating;
    private bool _openingCalendar;
    private bool _loading;
    private int? _loadingMoreRevision;
    private int _scheduleDisplayLimit = 30;
    private IReadOnlyList<DashboardCalendarEntry> _daySchedules = [];
    private string? _selectedReportId;
    private DashboardCalendarEntry? _selectedSchedule;
    private bool _listMode;
    /// <summary>창 머리 부제목: "이번 달 · 집중 리포트 18개 · 저장한 일정 5개".</summary>
    public string MonthSummary { get; private set; } = "";
    public event EventHandler? SummaryChanged;

    /// <summary>오른쪽 칸을 그날 시간표(도안) 대신 일정·리포트 목록으로 본다. ⋯ 메뉴에서 바꾼다.</summary>
    public bool ListMode
    {
        get => _listMode;
        set
        {
            _listMode = value;
            DayTimelineCanvas.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            DayActionsBar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            DayListPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayoutForSize();
        }
    }

    internal IReadOnlyList<Models.PageMenuItem> MenuItems() =>
    [
        new(_listMode ? "시간표로 보기" : "일정·리포트 목록으로 보기", () => ListMode = !_listMode),
        new("새로고침", () => _ = RefreshAsync(), !_loading && !_mutating),
        new("기록 전체 초기화", () => ResetHistory_Click(this, new RoutedEventArgs()), !_loading && !_mutating, IsDestructive: true)
    ];
    public DateTime SelectedDate { get; private set; } = DateTime.Today;
    public DateTime DisplayMonth { get; private set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public ReportCalendarView(SessionResultsService results, DashboardCalendarStore? calendar = null, Action<string>? openCalendar = null,
        IReportSummaryProvider? summaryProvider = null, string? summaryStatePath = null)
    {
        _results = results;
        _calendar = calendar ?? new DashboardCalendarStore();
        _openCalendar = openCalendar ?? GoogleCalendarLink.Open;
        _summaryProvider = summaryProvider;
        _summaryStatePath = summaryStatePath;
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

    private void Reports_Changed(object? sender, EventArgs e)
    {
        if (_mutating) return;
        Dispatcher.BeginInvoke(new Action(async () => { if (!_mutating) await RefreshAsync(); }));
    }

    public async Task SelectDateAsync(DateTime date)
    {
        if (SelectedDate != date.Date) { _scheduleDisplayLimit = 30; _selectedReportId = null; _selectedSchedule = null; }
        SelectedDate = date.Date;
        DisplayMonth = new(date.Year, date.Month, 1);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        int revision = ++_revision;
        _loading = true;
        UpdateResetControls();
        var date = SelectedDate;
        var month = DisplayMonth;
        _entries.Clear();
        ReportList.Visibility = Visibility.Collapsed;
        _counts = new Dictionary<DateTime, int>();
        LoadMoreButton.Visibility = Visibility.Collapsed;
        LoadMoreButton.IsEnabled = true;
        LoadMoreButton.Content = "리포트 더 보기";
        EmptyStateText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        SelectedDateRun.Text = date.ToString("M월 d일 dddd", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
        string hint = date == DateTime.Today ? "오늘"
            : date == DateTime.Today.AddDays(1) ? "내일"
            : date == DateTime.Today.AddDays(-1) ? "어제"
            : date.Year != DateTime.Today.Year ? date.ToString("yyyy년", System.Globalization.CultureInfo.GetCultureInfo("ko-KR")) : "";
        SelectedDayHintRun.Text = hint.Length == 0 ? "" : "  " + hint;
        ReportCountText.Text = "불러오는 중…";
        HistoryStatusText.Text = _calendar.LoadWarning ?? "리포트 불러오는 중…";
        _scheduleCounts = _calendar.GetMonthCounts(month);
        var schedules = _calendar.GetDay(date);
        _daySchedules = schedules;
        UpdateScheduleItems();
        ScheduleList.Visibility = schedules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleEmptyStateText.Visibility = schedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleCountText.Text = _calendar.LoadWarning is null ? $"저장한 일정 {schedules.Count}개" : "일정 조회 실패";
        ScheduleEmptyStateText.Text = _calendar.LoadWarning ?? "저장한 일정 없음";
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
            RenderCalendar();
            UpdateListStatus();
            HistoryStatusText.Text = _calendar.LoadWarning ?? "";
        }
        catch (Exception)
        {
            if (revision != _revision) return;
            _counts = new Dictionary<DateTime, int>();
            ReportCountText.Text = "조회 실패";
            HistoryStatusText.Text = "리포트 조회 실패 · 다시 불러오세요.";
            RetryButton.Visibility = Visibility.Visible;
            RenderCalendar();
        }
        finally
        {
            if (revision == _revision) { _loading = false; UpdateResetControls(); }
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_loading || _loadingMoreRevision == _revision || _entries.Count >= _counts.GetValueOrDefault(SelectedDate)) return;
        int revision = _revision;
        _loadingMoreRevision = revision;
        var date = SelectedDate;
        int offset = _entries.Count;
        LoadMoreButton.IsEnabled = false;
        LoadMoreButton.Content = "불러오는 중…";
        try
        {
            var page = await Task.Run(() => _results.Reports.GetDayPageAsync(date, offset));
            if (revision != _revision) return;
            foreach (var entry in page) _entries.Add(entry);
            UpdateListStatus();
            HistoryStatusText.Text = _calendar.LoadWarning ?? "";
        }
        catch (Exception)
        {
            if (revision == _revision) HistoryStatusText.Text = "추가 목록 조회 실패 · 더 보기를 다시 누르세요.";
        }
        finally
        {
            if (revision == _revision)
            {
                _loadingMoreRevision = null;
                LoadMoreButton.IsEnabled = true;
                LoadMoreButton.Content = "리포트 더 보기";
            }
        }
    }

    private void UpdateScheduleItems()
    {
        ScheduleList.ItemsSource = _daySchedules.Take(_scheduleDisplayLimit).ToArray();
        LoadMoreSchedulesButton.Visibility = _daySchedules.Count > _scheduleDisplayLimit ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreSchedulesButton.Content = $"일정 더 보기 · {Math.Min(_scheduleDisplayLimit, _daySchedules.Count)}/{_daySchedules.Count}";
    }

    private void LoadMoreSchedules_Click(object sender, RoutedEventArgs e)
    {
        _scheduleDisplayLimit += 30;
        UpdateScheduleItems();
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
        int monthReports = 0, monthSchedules = 0;
        for (int index = 0; index < 42; index++)
        {
            int day = index - leading + 1;
            if (day < 1 || day > days) { DaysPanel.Children.Add(new Border()); continue; }
            var date = DisplayMonth.AddDays(day - 1);
            int count = _counts.GetValueOrDefault(date);
            int scheduleCount = _scheduleCounts.GetValueOrDefault(date);
            monthReports += count;
            monthSchedules += scheduleCount;
            var titles = scheduleCount > 0 ? _calendar.GetDay(date).Select(entry => entry.Title.Trim()).ToArray() : [];
            var button = new System.Windows.Controls.Button { Content = DayCell(date, day, count, titles), Tag = date,
                Height = 40, MinWidth = 0, Padding = new Thickness(0), Margin = new Thickness(1), FontWeight = FontWeights.Normal,
                ToolTip = titles.Length == 0 ? $"{date:M월 d일} · 일정 {scheduleCount}개 · 리포트 {count}개"
                    : $"{date:M월 d일} · 일정 {scheduleCount}개 · 리포트 {count}개\n" + string.Join("\n", titles.Select(title => "· " + title)),
                Background = date == SelectedDate ? new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xF0, 0xDC)) : Brushes.Transparent,
                BorderBrush = date == SelectedDate ? (System.Windows.Media.Brush)FindResource("BlackAction")
                    : date == DateTime.Today ? (System.Windows.Media.Brush)FindResource("NavSelectedRule") : Brushes.Transparent };
            AutomationProperties.SetName(button, $"{date:yyyy년 M월 d일}, 리포트 {count}개, 일정 {scheduleCount}개");
            button.Click += async (_, _) => await SelectDateAsync(date);
            DaysPanel.Children.Add(button);
        }
        string monthName = DisplayMonth == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) ? "이번 달" : DisplayMonth.ToString("yyyy년 M월");
        string summary = monthReports == 0 && monthSchedules == 0 ? $"{monthName} · 아직 기록이 없어요"
            : $"{monthName} · 집중 리포트 {monthReports}개 · 저장한 일정 {monthSchedules}개";
        if (summary != MonthSummary) { MonthSummary = summary; SummaryChanged?.Invoke(this, EventArgs.Empty); }
        var next = _calendar.GetNext(DateTimeOffset.Now);
        NextScheduleText.Text = next is null ? "저장한 일정 없음"
            : $"{(next.Schedule.Start!.Value.LocalDateTime.Date == DateTime.Today ? "오늘" : next.Schedule.Start!.Value.LocalDateTime.Date == DateTime.Today.AddDays(1) ? "내일" : next.Schedule.Start!.Value.LocalDateTime.ToString("M/d"))} {next.Schedule.Start!.Value.LocalDateTime:HH:mm} {next.Title.Trim()}";
        RenderDayTimeline();
    }

    /// <summary>날짜 칸: 숫자 아래에 리포트(호박)와 일정(세이지) 점. 자세한 내용은 오른쪽 시간표에 있다.</summary>
    private StackPanel DayCell(DateTime date, int day, int reports, IReadOnlyList<string> titles)
    {
        Brush Brush(string key) => (Brush)FindResource(key);
        bool selected = date == SelectedDate, today = date == DateTime.Today;
        var cell = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        cell.Children.Add(new TextBlock { Text = day.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
            Foreground = selected || today ? Brush("BlackAction") : date.DayOfWeek == DayOfWeek.Sunday ? Brush("BlackError") : Brush("BlackText") });
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Height = 5, Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < Math.Min(reports, 3); i++)
            dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(1, 0, 1, 0), Fill = Brush("BlackAction") });
        for (int i = 0; i < Math.Min(titles.Count, 2); i++)
            // 일정은 막대로: 색만이 아니라 모양으로도 리포트(동그라미)와 구별된다.
            dots.Children.Add(new Border { Width = 9, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center, Background = Brush("BlackSuccess") });
        cell.Children.Add(dots);
        return cell;
    }

    private void DayTimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderDayTimeline();

    /// <summary>
    /// 고른 날의 시간표(도안 "월 + 그날 시간표"). 집중은 왼쪽 46%에 호박색, 저장한 일정은 오른쪽 절반에 세이지로
    /// 같은 시간축에 놓는다. 블록을 누르면 아래 버튼이 그 리포트나 일정을 가리킨다.
    /// </summary>
    private void RenderDayTimeline()
    {
        DayTimelineCanvas.Children.Clear();
        double width = DayTimelineCanvas.ActualWidth, height = DayTimelineCanvas.ActualHeight;
        if (width <= 0 || height <= 0) { UpdateDayActions(); return; }
        Brush Brush(string key) => (Brush)FindResource(key);
        var day = SelectedDate.Date;
        var hours = _entries.SelectMany(entry => new[] { entry.StartedAt.ToLocalTime(), entry.EndedAt.ToLocalTime() })
            .Concat(_daySchedules.Where(entry => entry.Schedule.Start is not null && entry.Schedule.End is not null)
                .SelectMany(entry => new[] { entry.Schedule.Start!.Value.LocalDateTime, entry.Schedule.End!.Value.LocalDateTime }))
            .Where(time => time.Date == day).ToArray();
        int first = Math.Min(9, hours.Length == 0 ? 9 : hours.Min(time => time.Hour));
        int last = Math.Max(22, hours.Length == 0 ? 22 : Math.Min(24, hours.Max(time => time.Hour + 1)));
        // 도안처럼 "09" 글자 위쪽에 맞추고, 시각 선은 글자 8px 아래(글자 가운데)에 긋는다.
        double perHour = (height - 8) / Math.Max(1, last - first);
        for (int hour = first; hour <= last; hour += 2)
        {
            double y = (hour - first) * perHour;
            var label = new TextBlock { Text = $"{hour:00}", FontSize = 11, Foreground = Brush("TimelineHour") };
            Canvas.SetTop(label, Math.Min(y, height - 16));
            DayTimelineCanvas.Children.Add(label);
            var line = new Border { Height = 1, Width = Math.Max(0, width - 40), Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xF0, 0xDC)) };
            Canvas.SetLeft(line, 40);
            Canvas.SetTop(line, Math.Min(y + 8, height - 1));
            DayTimelineCanvas.Children.Add(line);
        }
        (double Top, double Height) Slot(DateTime start, DateTime end)
        {
            if (start.Date < day) start = day;
            if (end.Date > day) end = day.AddDays(1);
            double size = Math.Max(30, (end - start).TotalHours * perHour);
            // 두 줄(제목 + 시각)이 들어갈 만큼 길면 글자가 잘리지 않게 46px 이상으로 둔다.
            return ((start - day.AddHours(first)).TotalHours * perHour + 8, size >= 36 ? Math.Max(46, size) : size);
        }
        // 집중: 왼쪽 52px부터 폭 46%
        var reports = _entries.OrderBy(entry => entry.StartedAt).ToArray();
        // 고른 리포트는 시각까지 두 줄로 보이게 최소 46px.
        var reportSlots = reports.Select(entry =>
        {
            var slot = Slot(entry.StartedAt.ToLocalTime(), entry.EndedAt.ToLocalTime());
            return entry.SessionId == SelectedReport()?.SessionId ? (Top: slot.Top, Height: Math.Max(46, slot.Height)) : slot;
        }).ToArray();
        var reportTops = TimelineLayout.Stack(reportSlots, height);
        for (int index = 0; index < reports.Length; index++)
        {
            var entry = reports[index];
            bool selected = entry.SessionId == SelectedReport()?.SessionId;
            string title = $"집중 {FormatMinutes(entry.EndedAt - entry.StartedAt)}" + (entry.IsSaved ? " · 리포트 저장됨" : "");
            string detail = $"{entry.StartedAt.ToLocalTime():HH:mm} – {entry.EndedAt.ToLocalTime():HH:mm}" + (entry.BlockedCount > 0 ? $" · 차단 {entry.BlockedCount}" : "");
            var block = Block(title, detail, $"{entry.StartedAt.ToLocalTime():HH:mm}", reportSlots[index].Height, Brush("BlackText"), Brush("BlackMuted"),
                Color.FromArgb(entry.IsSaved || selected ? (byte)0x42 : (byte)0x29, 0xE9, 0xC9, 0x95),
                selected ? Brush("BlackAction") : new SolidColorBrush(Color.FromArgb(0x66, 0xE9, 0xC9, 0x95)));
            block.Width = Math.Max(0, width * 0.46);
            string id = entry.SessionId;
            block.MouseLeftButtonUp += (_, _) => { _selectedReportId = id; RenderDayTimeline(); };
            AutomationProperties.SetName(block, $"{title}, {detail}");
            Canvas.SetLeft(block, 52);
            Canvas.SetTop(block, reportTops[index]);
            DayTimelineCanvas.Children.Add(block);
        }
        // 일정: 오른쪽 52%부터 끝까지
        var schedules = _daySchedules.Where(entry => entry.Schedule.Start is not null && entry.Schedule.End is not null)
            .OrderBy(entry => entry.Schedule.Start).ToArray();
        var scheduleSlots = schedules.Select(entry => Slot(entry.Schedule.Start!.Value.LocalDateTime, entry.Schedule.End!.Value.LocalDateTime)).ToArray();
        var scheduleTops = TimelineLayout.Stack(scheduleSlots, height);
        for (int index = 0; index < schedules.Length; index++)
        {
            var entry = schedules[index];
            var start = entry.Schedule.Start!.Value.LocalDateTime;
            string detail = $"{start:HH:mm} – {entry.Schedule.End!.Value.LocalDateTime:HH:mm}"
                + (string.IsNullOrWhiteSpace(entry.Schedule.Location) ? "" : $" · {entry.Schedule.Location!.Trim()}");
            bool selected = ReferenceEquals(entry, SelectedSchedule());
            var block = Block(entry.Title.Trim(), detail, $"{start:HH:mm}", scheduleSlots[index].Height,
                new SolidColorBrush(Color.FromArgb(0xFF, 0xDC, 0xEB, 0xE0)), Brush("BlackSuccessMuted"), Color.FromArgb(0x24, 0xB9, 0xD6, 0xC2),
                selected && schedules.Length > 1 ? Brush("BlackSuccess") : new SolidColorBrush(Color.FromArgb(0x66, 0xB9, 0xD6, 0xC2)));
            block.Width = Math.Max(0, width * 0.48);
            block.MouseLeftButtonUp += (_, _) => { _selectedSchedule = entry; RenderDayTimeline(); };
            AutomationProperties.SetName(block, $"{entry.Title.Trim()}, {detail}");
            Canvas.SetLeft(block, width * 0.52);
            Canvas.SetTop(block, scheduleTops[index]);
            DayTimelineCanvas.Children.Add(block);
        }
        UpdateDayActions();
    }

    /// <summary>시간표 한 칸. 키가 작으면 "집중 42분 15:10"처럼 한 줄로 줄인다.</summary>
    private static Border Block(string title, string detail, string shortTime, double height, Brush titleBrush, Brush detailBrush, Color tint, Brush rule)
    {
        var content = new StackPanel();
        if (height >= 36)
        {
            content.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = titleBrush,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = detailBrush,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        }
        else
        {
            var line = new TextBlock { FontSize = 12, Foreground = titleBrush, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            line.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.Bold });
            line.Inlines.Add(new System.Windows.Documents.Run(" " + shortTime) { Foreground = detailBrush });
            content.Children.Add(line);
        }
        return new Border { Height = height, CornerRadius = new CornerRadius(12), Padding = height >= 36 ? new Thickness(10, 6, 10, 5) : new Thickness(10, 6, 10, 6),
            Background = new SolidColorBrush(tint), BorderBrush = rule, BorderThickness = new Thickness(1), Child = content,
            ToolTip = $"{title} · {detail}", Cursor = System.Windows.Input.Cursors.Hand };
    }

    private ReportListEntry? SelectedReport() => _entries.FirstOrDefault(entry => entry.SessionId == _selectedReportId)
        ?? _entries.OrderBy(entry => entry.StartedAt).LastOrDefault();
    private DashboardCalendarEntry? SelectedSchedule() => _selectedSchedule is { } chosen && _daySchedules.Contains(chosen) ? chosen
        : _daySchedules.FirstOrDefault(entry => entry.Schedule.Start is not null);

    /// <summary>시간표 아래 버튼: 고른(없으면 마지막) 리포트 열기, 고른(없으면 첫) 일정을 Google Calendar로.</summary>
    private void UpdateDayActions()
    {
        var report = SelectedReport();
        var schedule = SelectedSchedule();
        DayReportButton.Visibility = report is null ? Visibility.Collapsed : Visibility.Visible;
        DayReportButton.Content = report is null ? "" : $"{report.StartedAt.ToLocalTime():HH:mm} 리포트 열기";
        DayReportButton.Tag = report;
        DayGoogleButton.Visibility = schedule is null ? Visibility.Collapsed : Visibility.Visible;
        DayGoogleButton.Tag = schedule;
        DayActionsBar.Visibility = _listMode || (report is null && schedule is null) ? Visibility.Collapsed : Visibility.Visible;
        int reportCount = _entries.Count, scheduleCount = _daySchedules.Count;
        DaySummaryText.Text = reportCount == 0 && scheduleCount == 0 ? "기록 없음" : $"집중 {reportCount}회 · 일정 {scheduleCount}개";
    }

    private void DayReport_Click(object sender, RoutedEventArgs e) => OpenReport_Click(sender, e);
    private void DayGoogle_Click(object sender, RoutedEventArgs e) => OpenGoogleCalendar_Click(sender, e);

    /// <summary>다른 탭(알림 확인, 활동 일지)에서 특정 집중의 리포트를 연다. 그날로 옮긴 뒤 연다.</summary>
    public async Task OpenSessionReportAsync(string sessionId, DateTime localDate)
    {
        await SelectDateAsync(localDate);
        var entry = _entries.FirstOrDefault(item => item.SessionId == sessionId);
        _selectedReportId = entry?.SessionId;
        RenderDayTimeline();
        await OpenReportAsync(sessionId, entry?.IsSaved ?? false, null);
    }

    private static string FormatMinutes(TimeSpan duration) => duration.TotalMinutes < 1 ? "1분 미만"
        : duration.TotalHours < 1 ? $"{(int)duration.TotalMinutes}분"
        : duration.Minutes == 0 ? $"{(int)duration.TotalHours}시간" : $"{(int)duration.TotalHours}시간 {duration.Minutes}분";

    private void UpdateLayoutForSize()
    {
        // 대시보드 기본 크기에서도 달력과 고른 날 목록을 나란히 둬 한눈에 보이게 한다.
        bool stacked = ActualWidth < 600;
        ResponsiveGrid.MinHeight = stacked || _listMode ? 0 : Math.Max(0, PageScroll.ViewportHeight);
        ResponsiveGrid.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 2, GridUnitType.Star);
        ResponsiveGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 16);
        ResponsiveGrid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        Grid.SetColumn(ListCard, stacked ? 0 : 2);
        Grid.SetRow(ListCard, stacked ? 1 : 0);
        ListCard.Margin = new Thickness(0, stacked ? 16 : 0, 0, 0);
    }

    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutForSize();

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e) => await SelectDateAsync(DisplayMonth.AddMonths(-1));
    private async void NextMonth_Click(object sender, RoutedEventArgs e) => await SelectDateAsync(DisplayMonth.AddMonths(1));
    private async void Today_Click(object sender, RoutedEventArgs e) => await SelectDateAsync(DateTime.Today);
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private void UpdateResetControls()
    {
        ResetHistoryButton.IsEnabled = ConfirmResetButton.IsEnabled = !_mutating && !_loading;
        CancelResetButton.IsEnabled = !_mutating;
        ListCard.IsEnabled = !_mutating;
    }

    private void ResetHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_mutating || _loading) return;
        ResetConfirmationPanel.Visibility = Visibility.Visible;
        CancelResetButton.Focus();
    }

    private void CancelReset_Click(object sender, RoutedEventArgs e)
    {
        if (_mutating) return;
        ResetConfirmationPanel.Visibility = Visibility.Collapsed;
        ResetHistoryButton.Focus();
    }

    private void ResetConfirmation_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || _mutating) return;
        CancelReset_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private async void ConfirmReset_Click(object sender, RoutedEventArgs e)
    {
        if (ResetConfirmationPanel.Visibility == Visibility.Visible) await ResetAllAsync();
    }

    internal async Task ResetAllAsync()
    {
        if (_mutating || _loading) return;
        _mutating = true;
        _revision++;
        _loadingMoreRevision = null;
        UpdateResetControls();
        HistoryStatusText.Text = "리포트와 일정을 초기화하는 중…";
        bool schedulesCleared = false;
        string message;
        try
        {
            _calendar.Clear();
            schedulesCleared = true;
            await Task.Run(() => _results.DeleteAllReportsAsync());
            ResetConfirmationPanel.Visibility = Visibility.Collapsed;
            _scheduleDisplayLimit = 30;
            message = "저장된 리포트·메모·일정을 모두 초기화했습니다.";
        }
        catch (Exception)
        {
            message = schedulesCleared
                ? "일정은 삭제했지만 리포트·메모 정리를 완료하지 못했습니다. 다시 시도해 주세요."
                : "초기화하지 못했습니다. 일정 파일을 확인한 뒤 다시 시도해 주세요.";
        }
        try
        {
            await RefreshAsync();
            HistoryStatusText.Text = message;
        }
        finally
        {
            _mutating = false;
            UpdateResetControls();
            if (ResetConfirmationPanel.Visibility == Visibility.Collapsed) ResetHistoryButton.Focus();
        }
    }

    private void OpenGoogleCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (_openingCalendar || sender is not System.Windows.Controls.Button { Tag: DashboardCalendarEntry entry } button) return;
        _openingCalendar = true;
        button.IsEnabled = false;
        try
        {
            _openCalendar(GoogleCalendarLink.Build(entry.Schedule));
            HistoryStatusText.Text = "Google Calendar에서 확인 후 저장하세요.";
        }
        catch { HistoryStatusText.Text = "Google Calendar 열기 실패 · 다시 시도하세요."; }
        finally { _openingCalendar = false; button.IsEnabled = true; }
    }

    private async void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_mutating || _loading || sender is not System.Windows.Controls.Button { Tag: DashboardCalendarEntry entry } button) return;
        _mutating = true;
        UpdateResetControls();
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
        finally
        {
            button.ClearValue(IsEnabledProperty);
            _mutating = false;
            UpdateResetControls();
        }
    }

    private async void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_mutating || sender is not System.Windows.Controls.Button { Tag: ReportListEntry entry } button) return;
        await OpenReportAsync(entry.SessionId, entry.IsSaved, button);
    }

    private async Task OpenReportAsync(string sessionId, bool isSaved, System.Windows.Controls.Button? button)
    {
        if (_mutating) return;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _results.OpenReportAsync(sessionId));
            if (result is null) { await RefreshAsync(); HistoryStatusText.Text = "이 집중의 리포트가 없어요."; return; }
            var report = new SessionReportWindow(result, _results.Reports, isSaved, _summaryProvider, _calendar, _summaryStatePath)
                { Owner = Window.GetWindow(this) };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            report.Closed += (_, _) => closed.TrySetResult();
            report.Show();
            await closed.Task;
        }
        catch (Exception) { HistoryStatusText.Text = "리포트를 열지 못했습니다. 다시 시도해 주세요."; }
        finally { if (button is not null) button.IsEnabled = true; }
    }

    public async Task DeleteReportAsync(string sessionId)
    {
        if (_mutating || _loading) return;
        _mutating = true;
        UpdateResetControls();
        try
        {
            await Task.Run(() => _results.DeleteReportAsync(sessionId));
            await RefreshAsync();
            HistoryStatusText.Text = "리포트와 메모를 삭제했습니다.";
        }
        catch (Exception) { HistoryStatusText.Text = "삭제하지 못했습니다. 다시 시도해 주세요."; }
        finally
        {
            _mutating = false;
            UpdateResetControls();
        }
    }

    private async void DeleteReport_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ReportListEntry entry } button) return;
        button.IsEnabled = false;
        await DeleteReportAsync(entry.SessionId);
        button.IsEnabled = true;
    }
}

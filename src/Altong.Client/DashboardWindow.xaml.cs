using System.Windows;
using System.Windows.Media;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using Altong.Client.Models;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Altong.Client.Services.Focus;
using Altong.Client.Services.NotificationControl;
using Color = System.Windows.Media.Color;

namespace Altong.Client;

public partial class DashboardWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly FocusSettingsStore _focusSettings;
    private readonly FocusRoutineService _focusRoutine;
    private readonly IActiveWindowTracker _activeWindowTracker;
    private readonly FocusModeService? _focusModeService;
    private readonly FocusModeCoordinator? _focusModeCoordinator;
    private readonly Action? _showRoutineReminder;
    private readonly Action? _shutdownApplication;
    private readonly AutoDndSettingsStore? _autoDndSettings;
    private readonly StartupRegistration? _startupRegistration;
    private readonly Lazy<DashboardInsightsView> _insightsView;
    private readonly IFocusInsightRepository _insightRepository;
    private DateTime _lastHomeExtrasRefresh = DateTime.MinValue;
    private int _homeExtrasRevision = -1;
    private Altong.Client.Data.Repositories.ReportListEntry? _homeLatestReport;
    private bool _settingsReady;
    public SessionResultsService Results { get; }
    private bool _dashboardDataLoading;
    private bool _dashboardDataRefreshPending;
    private bool _isClosed;
    private DateTime _lastDashboardRefresh = DateTime.MinValue;
    private DateTime _lastFocusUsageRefresh = DateTime.MinValue;
    private DateTime _focusUsageDate;
    private bool _focusUsageLoading;
    private bool _focusUsageRefreshPending;
    private int _focusUsageRevision;
    private DashboardCalendarStore _calendar = null!;
    private int _journalRange;
    private string _journalSubtitle = "";
    private bool _journalDetails;
    private readonly Lazy<ReportCalendarView> _calendarView;
    private static readonly System.Globalization.CultureInfo Korean = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
    private MessengerRunningStatus? _messengerStatus;

    internal bool IsHomeVisible => IsVisible && WindowState != WindowState.Minimized && DashboardTabs.SelectedIndex == 0;

    public DashboardWindow(
        FocusSettingsStore focusSettings,
        FocusRoutineService focusRoutine,
        SessionResultsService results,
        IActiveWindowTracker activeWindowTracker,
        FocusModeService? focusModeService = null,
        FocusModeCoordinator? focusModeCoordinator = null,
        Action? showRoutineReminder = null,
        Action? shutdownApplication = null,
        IReportSummaryProvider? summaryProvider = null,
        string? summaryStatePath = null,
        Action<string>? openCalendar = null,
        AutoDndSettingsStore? autoDndSettings = null,
        StartupRegistration? startupRegistration = null,
        DashboardCalendarStore? calendar = null,
        IFocusInsightRepository? insightRepository = null,
        IFocusInsightCoachingService? insightCoaching = null)
    {
        Results = results;
        _autoDndSettings = autoDndSettings;
        _startupRegistration = startupRegistration;
        _focusSettings = focusSettings;
        _focusRoutine = focusRoutine;
        _activeWindowTracker = activeWindowTracker;
        _focusModeService = focusModeService;
        _focusModeCoordinator = focusModeCoordinator;
        _showRoutineReminder = showRoutineReminder;
        _shutdownApplication = shutdownApplication;
        InitializeComponent();
        calendar ??= new DashboardCalendarStore(summaryStatePath is null ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(summaryStatePath))!, "dashboard-calendar-schedules.json"));
        _calendar = calendar;
        _calendar.Changed += Calendar_Changed;
        Results.PropertyChanged += Results_PropertyChanged;
        ShowHomeFocusUsage(SqliteFocusUsageRepository.Aggregate(DateTime.Today, DateTime.UtcNow, []));
        UpdateNavigationSelection();
        _calendarView = new(() =>
        {
            var view = new ReportCalendarView(results, calendar, openCalendar, summaryProvider, summaryStatePath);
            view.SummaryChanged += (_, _) => UpdateHeader();
            return view;
        });
        _insightRepository = insightRepository ?? results.FocusInsights;
        _insightsView = new(() => new DashboardInsightsView(_insightRepository, insightCoaching, () => Results.ActiveSession));
        _activeWindowTracker.ContextChanged += ActiveWindowTracker_ContextChanged;
        if (_focusModeService is not null)
            _focusModeService.StateChanged += FocusMode_StateChanged;
        if (_focusModeCoordinator is not null)
            _focusModeCoordinator.StateChanged += FocusMode_StateChanged;
        _settingsReady = true;
        if (_focusSettings.LoadWarning is { } warning)
            SetRoutineFeedback(warning, true);
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        var version = typeof(DashboardWindow).Assembly.GetName().Version;
        SettingsVersionText.Text = version is null ? "ALTONG" : $"ALTONG {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        UpdateLaunchAtStartupView();
        UpdateFocusModeView();
        SourceInitialized += (_, _) =>
        {
            DisableNativeWindowFrameChrome();
            FitToWorkArea();
        };
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            UpdateDashboardCornerClip();
        };
        Loaded += (_, _) =>
        {
            _clockTimer.Start();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateClock(); _clockTimer.Start(); }
            else _clockTimer.Stop();
            // 숨어 있는 동안에는 불씨가 숨 쉬지 않는다.
            Backdrop.SetBreathing(IsVisible && _ambientFocus == true && MotionBehavior.IsMotionAllowed);
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _calendar.Changed -= Calendar_Changed;
            if (_insightsView.IsValueCreated) _insightsView.Value.CancelPending();
            Results.PropertyChanged -= Results_PropertyChanged;
            _clockTimer.Stop();
            _activeWindowTracker.ContextChanged -= ActiveWindowTracker_ContextChanged;
            if (_focusModeService is not null)
                _focusModeService.StateChanged -= FocusMode_StateChanged;
            if (_focusModeCoordinator is not null)
                _focusModeCoordinator.StateChanged -= FocusMode_StateChanged;
        };
    }

    internal void ShowHome() => NavigateTo(0);

    internal void SetMessengerStatus(MessengerRunningStatus? status)
    {
        if (_messengerStatus == status) return;

        _messengerStatus = status;
        KakaoTalkRunningBadge.Content = status?.KakaoTalk;
        SlackRunningBadge.Content = status?.Slack;
        DiscordRunningBadge.Content = status?.Discord;
    }

    private void ActiveWindowTracker_ContextChanged(object? sender, Altong.Client.Models.CurrentContext context)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ActiveWindowTracker_ContextChanged(sender, context));
            return;
        }

        CurrentApplicationText.Text = string.IsNullOrWhiteSpace(context.ActiveProcess)
            ? "추적 대기 중"
            : context.ActiveProcess;

    }

    // Only presentation concerns belong here; session/DB/AI contracts remain unchanged.
    private void UpdateClock()
    {
        DashboardClockText.Text = DateTime.Now.ToString("HH:mm");
        UpdateHeader();
        UpdateFocusTimerView();
        UpdateQuickSettings();
        if (IsVisible && DashboardTabs.SelectedIndex == 0 &&
            (DateTime.UtcNow - _lastFocusUsageRefresh >= TimeSpan.FromSeconds(5) || _focusUsageDate != DateTime.Today))
            _ = RefreshHomeFocusUsageAsync();
        var context = _activeWindowTracker.CurrentContext;
        CurrentApplicationText.Text = string.IsNullOrWhiteSpace(context.ActiveProcess)
            ? "추적 대기 중"
            : context.ActiveProcess;
        if (IsVisible &&
            DashboardTabs.SelectedIndex == 2 &&
            DateTime.UtcNow - _lastDashboardRefresh >= TimeSpan.FromSeconds(5))
            _ = RefreshDashboardDataAsync();
    }

    /// <summary>집중·휴식이 얼마나 지났는지 타이머 아래 호박색 막대로 보여 준다. 타이머가 없거나 끝나면 숨긴다.</summary>
    private void UpdateFocusProgress()
    {
        double minutes = _focusRoutine.Phase switch
        {
            FocusRoutinePhase.Focus => _focusSettings.Current.FocusMinutes,
            FocusRoutinePhase.Break => _focusSettings.Current.BreakMinutes,
            _ => 0
        };
        bool show = _focusRoutine.UsesTimer && minutes > 0;
        HomeFocusProgressTrack.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            HomeFocusProgressScale.ScaleX = Math.Clamp(1 - _focusRoutine.Remaining.TotalMinutes / minutes, 0, 1);
    }

    private void UpdateFocusTimerView()
    {
        UpdateFocusProgress();
        if (!_focusRoutine.UsesTimer)
        {
            bool enabled = _focusModeService?.IsEnabled == true;
            FocusTimerTitleText.Text = enabled ? "집중모드 경과 시간" : "집중모드";
            FocusDurationText.Text = enabled
                ? $"{(int)_focusRoutine.Elapsed.TotalMinutes:00}:{_focusRoutine.Elapsed.Seconds:00}" : "OFF";
            FocusTimerCaptionText.Text = "";
            FocusTimerCaptionText.Visibility = Visibility.Collapsed;
            return;
        }
        FocusTimerCaptionText.Visibility = Visibility.Visible;
        switch (_focusRoutine.Phase)
        {
            case FocusRoutinePhase.Focus:
                FocusTimerTitleText.Text = "집중 시간";
                FocusDurationText.Text = FormatRemaining(_focusRoutine.Remaining);
                FocusTimerCaptionText.Text = "집중 중";
                break;

            case FocusRoutinePhase.Break:
                FocusTimerTitleText.Text = "휴식 시간";
                FocusDurationText.Text = FormatRemaining(_focusRoutine.Remaining);
                FocusTimerCaptionText.Text = "휴식 중";
                break;

            case FocusRoutinePhase.Completed:
                FocusTimerTitleText.Text = "집중 완료";
                FocusDurationText.Text = "00:00";
                FocusTimerCaptionText.Text = "집중모드 유지 중";
                break;

            default:
                FocusTimerTitleText.Text = "집중 시간";
                FocusDurationText.Text = $"{_focusSettings.Current.FocusMinutes:00}:00";
                FocusTimerCaptionText.Text = DescribeTimerSettings(_focusSettings.Current);
                break;
        }
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        long seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void Results_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Results_PropertyChanged(sender, e));
            return;
        }
        if (_isClosed) return;
        _focusUsageRevision++;
        _lastFocusUsageRefresh = DateTime.MinValue;
        if (IsVisible && DashboardTabs.SelectedIndex == 0) _ = RefreshHomeFocusUsageAsync();
        if (IsVisible && DashboardTabs.SelectedIndex == 5) _ = _insightsView.Value.RefreshAsync();
    }

    private async Task RefreshHomeFocusUsageAsync()
    {
        if (_isClosed) return;
        if (_focusUsageLoading) { _focusUsageRefreshPending = true; return; }
        _focusUsageLoading = true;
        _lastFocusUsageRefresh = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var day = now.ToLocalTime().Date;
        var active = Results.ActiveSession;
        int revision = _focusUsageRevision;
        if (_focusUsageDate != day)
            ShowHomeFocusUsage(SqliteFocusUsageRepository.Aggregate(day, now, []));
        try
        {
            var usage = await Task.Run(() => Results.FocusUsage.ReadDayAsync(day, now, active));
            var reports = await Task.Run(() => Results.Reports.GetDayPageAsync(day));
            if (_isClosed) return;
            if (revision != _focusUsageRevision || day != DateTime.Today)
            {
                _focusUsageRefreshPending = true;
                _lastFocusUsageRefresh = DateTime.MinValue;
                return;
            }
            ShowHomeFocusUsage(usage);
            ShowRecentReports(reports);
            ShowNextSchedule();
            _ = RefreshHomeExtrasAsync(revision);
        }
        catch (Exception)
        {
            if (!_isClosed)
                HomeFocusUsageStatusText.Text = "불러오지 못했어요";
                HomeFocusUsageStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _focusUsageLoading = false;
            if (_focusUsageRefreshPending)
            {
                _focusUsageRefreshPending = false;
                if (!_isClosed && IsVisible && DashboardTabs.SelectedIndex == 0)
                    _ = RefreshHomeFocusUsageAsync();
            }
        }
    }

    private void ShowHomeFocusUsage(FocusUsageDay usage)
    {
        _focusUsageDate = usage.Date;
        HomeFocusUsageItems.ItemsSource = usage.Hours;
        HomeFocusUsageTotalText.Text = usage.TotalText.StartsWith("총 ", StringComparison.Ordinal) ? usage.TotalText[2..] : usage.TotalText;
        HomeFocusUsageStatusText.Text = usage.Total == TimeSpan.Zero ? "아직 집중 기록 없음" : "";
        HomeFocusUsageStatusText.Visibility = usage.Total == TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshDashboardDataAsync()
    {
        if (_isClosed) return;
        if (DashboardTabs.SelectedIndex == 1)
        {
            await ((DashboardNotificationsView)NotificationsHost.Content).CheckForUpdatesAsync();
            return;
        }
        if (_dashboardDataLoading) { _dashboardDataRefreshPending = true; return; }
        _dashboardDataLoading = true;
        _lastDashboardRefresh = DateTime.UtcNow;
        try
        {
            int page = DashboardTabs.SelectedIndex;
            int range = _journalRange;
            var from = JournalRangeStart(range).ToUniversalTime();
            var to = DateTime.UtcNow;
            var activeFocus = _focusModeService?.IsEnabled == true ? Results.ActiveSession : null;
            var context = activeFocus is null ? null : _activeWindowTracker.CaptureNow();
            var data = await Task.Run(async () =>
            {
                var entries = (await Results.ReadActivityJournalAsync(from, to, focusSessionsOnly: true)).ToList();
                if (context is { DurationSeconds: > 0 } && activeFocus is not null &&
                    !string.IsNullOrWhiteSpace(context.ActiveProcess))
                {
                    var start = context.LastUpdated.ToUniversalTime().AddSeconds(-context.DurationSeconds);
                    if (start < activeFocus.StartedAt) start = activeFocus.StartedAt;
                    if (start < from) start = from;
                    var end = context.LastUpdated.ToUniversalTime();
                    if (end > to) end = to;
                    if (end > start) entries.Add(new ActivityJournalEntry(context.ActiveProcess, context.WindowTitle, start, end));
                }
                var journal = ActivityJournalHour.Group(entries);
                return (Journal: journal, Usage: ActivityJournalHour.SummarizeApps(journal), Heatmap: JournalHourlyUsage.Build(journal));
            });
            if (_isClosed) return;
            if (page != DashboardTabs.SelectedIndex || range != _journalRange) { _dashboardDataRefreshPending = true; return; }
            string rangeName = JournalRangeName(range);
            DataPeriodText.Text = $"{rangeName} · 집중 중 앱 사용 기록";
            ActivityJournalEmptyText.Text = $"{rangeName}의 집중 기록 없음";
            double maximum = data.Usage.Count == 0 ? 1 : data.Usage.Max(item => item.Seconds);
            AppUsageItemsControl.ItemsSource = data.Usage.Select(item => item with { Percentage = item.Seconds / maximum * 100 }).ToArray();
            JournalItemsControl.ItemsSource = data.Journal;
            ActivityJournalEmptyText.Visibility = data.Journal.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (page == 2) ShowJournalUsage(data.Heatmap);
            DataErrorText.Text = "";
        }
        catch (Exception)
        {
            if (!_isClosed) DataErrorText.Text = "기록 조회 실패 · 자동 재시도 중…";
        }
        finally
        {
            _dashboardDataLoading = false;
            if (_dashboardDataRefreshPending && !_isClosed)
            {
                _dashboardDataRefreshPending = false;
                _ = RefreshDashboardDataAsync();
            }
        }
    }
    /// <summary>
    /// 탭 머리: 작은 부제목 + 큰 제목, 오른쪽 도구는 탭마다 다르다(도안 그대로).
    /// 홈·알림 = 시계, 활동 일지 = ‹ 오늘 ›, 캘린더 = 범례, 설정 = 버전. 홈 밖의 탭에는 ⋯ 메뉴가 붙는다.
    /// </summary>
    private void UpdateHeader()
    {
        if (DashboardTabs is null || DashboardDateText is null) return;
        int index = DashboardTabs.SelectedIndex;
        DashboardClockText.Visibility = index is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        JournalRangeTabs.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsVersionText.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageMenuButton.Visibility = index is 0 or 5 ? Visibility.Collapsed : Visibility.Visible;
        DashboardDateText.Text = index switch
        {
            2 => _journalSubtitle.Length > 0 ? _journalSubtitle : "집중 중 쓴 앱",
            3 => "자주 바꾸는 것은 위에서 바로",
            4 => _calendarView.Value.MonthSummary,
            _ => DateTime.Now.ToString("M월 d일 dddd", Korean)
        };
    }

    /// <summary>⋯ 메뉴. 도안 화면에 없는 기능을 탭별로 모아 보여 준다.</summary>
    private void PageMenu_Click(object sender, RoutedEventArgs e)
    {
        var items = DashboardTabs.SelectedIndex switch
        {
            1 => ((DashboardNotificationsView)NotificationsHost.Content).MenuItems(),
            2 => (IReadOnlyList<PageMenuItem>)
            [
                new(_journalDetails ? "앱 순위로 돌아가기" : "시간대별 기록 보기", () => ShowJournalDetails(!_journalDetails)),
                new("새로고침", () => { _lastDashboardRefresh = DateTime.MinValue; _ = RefreshDashboardDataAsync(); })
            ],
            3 => SettingsMenuItems(),
            4 => _calendarView.Value.MenuItems(),
            _ => []
        };
        if (items.Count == 0) return;
        var menu = new System.Windows.Controls.ContextMenu { Style = (Style)FindResource("BlackMenu"), PlacementTarget = PageMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, HorizontalOffset = -150, VerticalOffset = 6 };
        foreach (var item in items)
        {
            var entry = new System.Windows.Controls.MenuItem { Header = item.Header, IsEnabled = item.IsEnabled, Style = (Style)FindResource("BlackMenuItem") };
            if (item.IsDestructive) entry.Foreground = (System.Windows.Media.Brush)FindResource("BlackError");
            var run = item.Run;
            entry.Click += (_, _) => run();
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    /// <summary>알림 확인·활동 일지의 [리포트 열기]: 캘린더 탭으로 옮겨 그날의 그 리포트를 연다.</summary>
    private async Task OpenSessionReportAsync(string sessionId, DateTime day)
    {
        NavigateTo(4);
        await _calendarView.Value.OpenSessionReportAsync(sessionId, day);
    }

    internal void ShowJournalDetails(bool show)
    {
        _journalDetails = show;
        JournalOverview.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        JournalDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        JournalScroll.ScrollToTop();
    }

    private void JournalBack_Click(object sender, RoutedEventArgs e) => ShowJournalDetails(false);
    private void JournalRange_Checked(object sender, RoutedEventArgs e)
    {
        if (JournalMonthTab is null) return; // 창을 만드는 중(InitializeComponent)에 [오늘]이 처음 체크될 때
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag } && int.TryParse(tag, out int range)) SelectJournalRange(range);
    }

    /// <summary>활동 일지 기간: 0 오늘, 1 이번 주(일요일부터), 2 이번 달.</summary>
    internal void SelectJournalRange(int range)
    {
        range = Math.Clamp(range, 0, 2);
        var tab = range switch { 1 => JournalWeekTab, 2 => JournalMonthTab, _ => JournalTodayTab };
        if (tab.IsChecked != true) tab.IsChecked = true;
        if (range == _journalRange) return;
        _journalRange = range;
        _journalSubtitle = "";
        UpdateHeader();
        _lastDashboardRefresh = DateTime.MinValue;
        if (_settingsReady) _ = RefreshDashboardDataAsync();
    }

    private static DateTime JournalRangeStart(int range) => range switch
    {
        1 => DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek),
        2 => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
        _ => DateTime.Today
    };

    private static string JournalRangeName(int range) => range switch { 1 => "이번 주", 2 => "이번 달", _ => "오늘" };

    private void JournalScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (JournalOverview is null) return;
        // 좁으면 앱 순위 아래로 열지도를 내린다. 넓으면 두 칸이 창 아래까지 채운다(MinHeight = 보이는 높이).
        bool stacked = JournalScroll.ActualWidth < 600;
        JournalOverview.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 14);
        JournalOverview.ColumnDefinitions[2].Width = new GridLength(stacked ? 0 : 3, GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(HeatmapPane, stacked ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(HeatmapPane, stacked ? 1 : 0);
        HeatmapPane.Margin = new Thickness(0, stacked ? 14 : 0, 0, 0);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string page } &&
            int.TryParse(page, out int index))
            NavigateTo(index);
    }

    private void NavigateTo(int index) => DashboardTabs.SelectedIndex = index;

    private void UpdateNavigationSelection()
    {
        if (NavigationRail is null) return;
        foreach (var button in NavigationRail.Children.OfType<System.Windows.Controls.Button>())
        {
            bool selected = button.Tag is string tag && tag == DashboardTabs.SelectedIndex.ToString(CultureInfo.InvariantCulture);
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "선택됨" : "");
            MotionBehavior.SetBrush(button, System.Windows.Controls.Control.BackgroundProperty,
                selected ? (System.Windows.Media.Brush)FindResource("NavSelected") : System.Windows.Media.Brushes.Transparent);
            MotionBehavior.SetBrush(button, System.Windows.Controls.Control.BorderBrushProperty,
                selected ? (System.Windows.Media.Brush)FindResource("NavSelectedRule") : System.Windows.Media.Brushes.Transparent);
            MotionBehavior.SetBrush(button, System.Windows.Controls.Control.ForegroundProperty,
                (System.Windows.Media.Brush)FindResource(selected ? "BlackText" : "BlackMuted"));
        }
    }

    /// <summary>
    /// 홈 벤토. 넓으면 타이머(2/3) 옆에 [오늘 집중]·[연속 집중], 아래 줄에 [알림]·[다음 일정]·[최근 리포트]가 창 높이를 채운다.
    /// 좁으면(600px 미만) 타이머 아래로 두 칸씩 쌓고 스크롤한다.
    /// </summary>
    private void HomeScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeLayout is null || HomeTopGrid is null) return;
        HomeLayout.Width = Math.Max(0, HomeScroll.ActualWidth - 20);
        bool stacked = HomeScroll.ActualWidth < 600;
        static GridLength Star(double value) => new(value, GridUnitType.Star);
        static void Place(UIElement element, int row, int column, int span = 1)
        {
            System.Windows.Controls.Grid.SetRow(element, row);
            System.Windows.Controls.Grid.SetColumn(element, column);
            System.Windows.Controls.Grid.SetColumnSpan(element, span);
        }
        // 전체: 넓으면 위 2 : 아래 1로 창을 채우고, 좁으면 내용 높이대로.
        HomeLayout.RowDefinitions[0].Height = stacked ? GridLength.Auto : Star(2);
        HomeLayout.RowDefinitions[2].Height = stacked ? GridLength.Auto : Star(1);
        // 위: 타이머 | 오른쪽 두 칸  ↔  타이머 아래에 두 칸 나란히
        HomeTopGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 14);
        HomeTopGrid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : Star(1);
        Place(HomeSideTiles, stacked ? 1 : 0, stacked ? 0 : 2);
        HomeSideTiles.Margin = new Thickness(0, stacked ? 14 : 0, 0, 0);
        HomeSideTiles.RowDefinitions[0].Height = stacked ? GridLength.Auto : Star(1);
        HomeSideTiles.RowDefinitions[1].Height = new GridLength(stacked ? 0 : 14);
        HomeSideTiles.ColumnDefinitions[1].Width = new GridLength(stacked ? 14 : 0);
        HomeSideTiles.ColumnDefinitions[2].Width = stacked ? Star(1) : new GridLength(0);
        Place(HomeStreakShortcut, stacked ? 0 : 2, stacked ? 2 : 0);
        // 아래: 알림 | 일정 | 리포트  ↔  알림 한 줄, 그 아래 일정·리포트 나란히
        HomeBottomGrid.RowDefinitions[0].Height = stacked ? GridLength.Auto : Star(1);
        HomeBottomGrid.RowDefinitions[1].Height = new GridLength(stacked ? 14 : 0);
        HomeBottomGrid.ColumnDefinitions[0].Width = stacked ? Star(1) : Star(3);
        HomeBottomGrid.ColumnDefinitions[2].Width = stacked ? Star(1) : Star(2);
        HomeBottomGrid.ColumnDefinitions[3].Width = new GridLength(stacked ? 0 : 14);
        HomeBottomGrid.ColumnDefinitions[4].Width = stacked ? new GridLength(0) : Star(1);
        Place(HomeNotificationsShortcut, 0, 0, stacked ? 3 : 1);
        Place(HomeCalendarShortcut, stacked ? 2 : 0, stacked ? 0 : 2);
        Place(HomeReportShortcut, stacked ? 2 : 0, stacked ? 2 : 4);
    }

    private void HomeRoutine_Click(object sender, RoutedEventArgs e) => NavigateTo(3);

    /// <summary>[최근 리포트]: 오늘 마지막 집중의 리포트를 연다. 오늘 리포트가 없으면 캘린더로 간다.</summary>
    private void HomeReport_Click(object sender, RoutedEventArgs e)
    {
        if (_homeLatestReport is { } latest)
            _ = OpenSessionReportAsync(latest.SessionId, latest.EndedAt.ToLocalTime().Date);
        else
            NavigateTo(4);
    }

    /// <summary>
    /// [집중 중 받은 알림]과 [연속 집중]: 매 5초 새로 고침마다 읽기엔 무거우니 30초에 한 번, 또는 집중 기록이 바뀌었을 때만 읽는다.
    /// 읽지 못해도 홈의 다른 칸은 그대로 둔다.
    /// </summary>
    private async Task RefreshHomeExtrasAsync(int revision)
    {
        if (_isClosed || (revision == _homeExtrasRevision && DateTime.UtcNow - _lastHomeExtrasRefresh < TimeSpan.FromSeconds(30))) return;
        _homeExtrasRevision = revision;
        _lastHomeExtrasRefresh = DateTime.UtcNow;
        var active = Results.ActiveSession;
        try
        {
            var notifications = await Task.Run(async () =>
            {
                var snapshot = await Results.DashboardNotifications.ReadSnapshotAsync(new Altong.Client.Data.Repositories.DashboardNotificationQuery());
                return await Results.DashboardNotifications.ReadSnapshotPageAsync(snapshot, 0, 3);
            });
            if (!_isClosed) ShowHomeNotifications(notifications);
        }
        catch (Exception) { if (!_isClosed) { HomeNotificationSummaryText.Text = "—"; HomeNotificationSummaryText.Visibility = Visibility.Visible; } }
        try
        {
            var insight = await Task.Run(() => _insightRepository.ReadAsync(InsightPeriod.Last7Days, DateTime.UtcNow, active));
            if (!_isClosed) ShowHomeStreak(insight.Quality);
        }
        catch (Exception) { if (!_isClosed) HomeStreakText.Text = "—"; }
    }

    private void ShowHomeNotifications(IReadOnlyList<Altong.Client.Data.Models.NotificationRecord> records)
    {
        HomeRecentNotifications.Children.Clear();
        HomeNotificationSummaryText.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeNotificationSummaryText.Text = "아직 없어요";
        System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);
        foreach (var record in records)
        {
            var item = new NotificationHistoryItem(record);
            var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            bool passed = record.IsPassed == true;
            var tag = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(passed ? System.Windows.Media.Color.FromArgb(0x1F, 0xB9, 0xD6, 0xC2) : System.Windows.Media.Color.FromArgb(0x1F, 0xE9, 0xC9, 0x95)),
                Child = new System.Windows.Controls.TextBlock { Text = item.ClassificationText, FontSize = TypeScale.Caption, FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(passed ? "BlackSuccess" : "BlackAction") } };
            System.Windows.Controls.DockPanel.SetDock(tag, System.Windows.Controls.Dock.Right);
            row.Children.Add(tag);
            var text = new System.Windows.Controls.TextBlock { FontSize = TypeScale.Label, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = item.AccessibleText };
            text.Inlines.Add(new System.Windows.Documents.Run(item.RowTitleText) { FontWeight = FontWeights.SemiBold, Foreground = Brush("BlackText") });
            text.Inlines.Add(new System.Windows.Documents.Run("  " + item.PreviewText) { Foreground = Brush("BlackMuted") });
            row.Children.Add(text);
            HomeRecentNotifications.Children.Add(row);
        }
    }

    /// <summary>[연속 집중]: 며칠째인지와 최근 7일 잔디(인사이트와 같은 세이지 단계, 오늘은 테두리).</summary>
    private void ShowHomeStreak(InsightQualityMetrics? quality)
    {
        int days = Math.Max(0, quality?.CurrentStreakDays ?? 0);
        HomeStreakText.Text = days == 0 ? "0일" : $"{days}일째";
        HomeStreakDays.Children.Clear();
        var recent = (quality?.HeatmapDays ?? []).Where(day => !day.IsFuture).OrderBy(day => day.LocalDate).TakeLast(7).ToArray();
        double[] alpha = [0, .35, .5, .7, .85];
        for (int i = 0; i < 7; i++)
        {
            var day = i - (7 - recent.Length) is int index && index >= 0 ? recent[index] : null;
            int level = Math.Clamp(day?.IntensityLevel ?? 0, 0, 4);
            var cell = new System.Windows.Controls.Border { Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(2.5),
                Background = level == 0 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xF0, 0xDC))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(alpha[level] * 255), 0xB9, 0xD6, 0xC2)),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BlackSuccess"), BorderThickness = new Thickness(day?.IsToday == true ? 1.5 : 0),
                ToolTip = day?.TooltipText };
            HomeStreakDays.Children.Add(cell);
        }
    }

    /// <summary>홈 제목. 시간대와 상관없이 늘 같은 문장으로 맞이한다.</summary>
    internal const string HomeTitle = "나만의 집중 공간";

    /// <summary>
    /// 도안 "앱 순위 + 언제 무엇을 썼나". 왼쪽은 오래 쓴 앱 다섯 개, 오른쪽은 시각별로 쌓은 막대(높이 = 쓴 시간, 색 = 앱).
    /// </summary>
    private void ShowJournalUsage(JournalHourlyUsage map)
    {
        System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);
        var total = TimeSpan.FromSeconds(map.TotalSeconds);
        _journalSubtitle = map.Ranking.Count == 0 ? $"집중 중 쓴 앱 · {JournalRangeName(_journalRange)} 기록 없음"
            : $"집중 중 쓴 앱 · {ActivityJournalHour.FormatDuration(total)}";
        UpdateHeader();

        // 왼쪽: 순위 · 아이콘 · 이름과 막대 · 시간
        JournalRankingList.Children.Clear();
        double top = map.Ranking.Count == 0 ? 1 : map.Ranking[0].Seconds;
        var icons = (AppIconConverter)FindResource("AppIcon");
        for (int index = 0; index < map.Ranking.Count; index++)
        {
            var app = map.Ranking[index];
            var row = new System.Windows.Controls.Grid();
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), FontSize = TypeScale.Label, FontWeight = FontWeights.Bold,
                Foreground = index == 0 ? Brush("BlackAction") : Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center });
            var icon = new System.Windows.Controls.Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Brush("GlassPaneHover"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = new System.Windows.Controls.Image { Width = 16, Height = 16,
                    Source = (System.Windows.Media.ImageSource?)icons.Convert(app.AppName, typeof(System.Windows.Media.ImageSource), null!, CultureInfo.CurrentCulture) } };
            System.Windows.Controls.Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var middle = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(new System.Windows.Controls.TextBlock { Text = AppNameDisplayConverter.Format(app.AppName), FontSize = TypeScale.Body, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = app.AppName });
            var bar = new System.Windows.Controls.Grid { Height = 5, Margin = new Thickness(0, 6, 0, 0) };
            double share = Math.Clamp(app.Seconds / top, 0.02, 1);
            bar.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1 - share, GridUnitType.Star) });
            bar.Children.Add(new System.Windows.Controls.Border { CornerRadius = new CornerRadius(3),
                Background = new System.Windows.Media.SolidColorBrush(index < ChartColors.Length ? ChartColors[index] : OtherColor) });
            middle.Children.Add(bar);
            System.Windows.Controls.Grid.SetColumn(middle, 2);
            row.Children.Add(middle);
            var duration = new System.Windows.Controls.TextBlock { Text = ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(app.Seconds)), FontSize = TypeScale.Label,
                Foreground = Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Controls.Grid.SetColumn(duration, 3);
            row.Children.Add(duration);
            System.Windows.Automation.AutomationProperties.SetName(row, $"{index + 1}위 {AppNameDisplayConverter.Format(app.AppName)} {duration.Text}");
            JournalRankingList.Children.Add(new System.Windows.Controls.Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xF0, 0xDC)),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = row });
        }
        if (map.Ranking.Count == 0)
            JournalRankingList.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{JournalRangeName(_journalRange)}은 집중 중 쓴 앱이 아직 없어요",
                Foreground = Brush("MutedText"), FontSize = TypeScale.Body, Margin = new Thickness(0, 8, 0, 0) });

        _journalUsage = map;
        JournalEmptyText.Visibility = map.Ranking.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JournalInsight.Visibility = map.BusiestRangeText is null ? Visibility.Collapsed : Visibility.Visible;
        JournalInsightHour.Text = map.BusiestRangeText ?? "";
        JournalInsightTail.Text = map.BusiestTailText;
        // 범례: 순위 1–3위 앱 + 나머지는 기타. 막대 색과 왼쪽 순위 막대 색이 같다.
        JournalLegend.Children.Clear();
        foreach (var (label, color) in ChartSeries(map))
        {
            var item = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            item.Children.Add(new System.Windows.Controls.Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Background = new System.Windows.Media.SolidColorBrush(color) });
            item.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = TypeScale.Label, Foreground = Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 });
            JournalLegend.Children.Add(item);
        }
        RenderJournalChart();
    }

    private JournalHourlyUsage? _journalUsage;

    /// <summary>
    /// 차트 색. 테마의 연한 호박·세이지는 서로 너무 비슷해 막대끼리 구별이 안 되므로, 어두운 칸 위에서 색약 검사를 통과한
    /// 호박 · 파랑 · 청록을 순위 1–3위에 고정으로 쓰고, 4위부터는 회색 "기타"로 묶는다.
    /// </summary>
    private static readonly System.Windows.Media.Color[] ChartColors =
    [
        System.Windows.Media.Color.FromRgb(0xD2, 0x8E, 0x05),
        System.Windows.Media.Color.FromRgb(0x33, 0x8C, 0xED),
        System.Windows.Media.Color.FromRgb(0x00, 0xA8, 0x88)
    ];
    private static readonly System.Windows.Media.Color OtherColor = System.Windows.Media.Color.FromRgb(0x6B, 0x62, 0x59);

    private static IReadOnlyList<(string Label, System.Windows.Media.Color Color)> ChartSeries(JournalHourlyUsage map)
    {
        var series = map.Ranking.Take(ChartColors.Length).Select((app, index) => (AppNameDisplayConverter.Format(app.AppName), ChartColors[index])).ToList();
        if (map.Ranking.Count > ChartColors.Length || map.TotalSeconds > map.Ranking.Take(ChartColors.Length).Sum(app => app.Seconds) + 1)
            series.Add(("기타", OtherColor));
        return series;
    }

    private void JournalHourChart_SizeChanged(object sender, SizeChangedEventArgs e) => RenderJournalChart();

    /// <summary>
    /// 시각별 쌓인 막대. 가로는 시각, 막대 높이는 그 시각에 쓴 시간, 조각 색은 앱이다.
    /// 가장 긴 시각에만 합계를 적고, 나머지는 막대에 마우스를 올리면 앱별 시간이 나온다.
    /// </summary>
    private void RenderJournalChart()
    {
        var canvas = JournalHourChart;
        canvas.Children.Clear();
        var map = _journalUsage;
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (map is null || map.Ranking.Count == 0 || width <= 0 || height <= 0) return;
        System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);
        const double axisWidth = 44, labelHeight = 20, topRoom = 18;
        double plotHeight = Math.Max(40, height - labelHeight - topRoom);
        double baseline = topRoom + plotHeight;
        int columns = map.LastHour - map.FirstHour + 1;
        double columnWidth = (width - axisWidth) / columns;
        double barWidth = Math.Clamp(columnWidth * .62, 6, 28);

        // 세로 눈금: 보기 좋은 간격(10분, 15분, 30분, 1시간, 2시간 …)으로 3–4줄만, 아주 옅게
        double maxMinutes = Math.Max(1, map.MaxHourSeconds / 60);
        double[] steps = [5, 10, 15, 30, 60, 120, 180, 300, 600, 1200];
        double step = steps.FirstOrDefault(value => maxMinutes / value <= 4, 1200);
        double top = Math.Ceiling(maxMinutes / step) * step;
        string Minutes(double minutes) => minutes < 60 ? $"{minutes:0}분" : minutes % 60 == 0 ? $"{minutes / 60:0}시간" : $"{Math.Floor(minutes / 60):0}시간 {minutes % 60:0}분";
        for (double tick = step; tick <= top + .1; tick += step)
        {
            double y = baseline - tick / top * plotHeight;
            canvas.Children.Add(Place(new System.Windows.Controls.Border { Width = Math.Max(0, width - axisWidth), Height = 1,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xF0, 0xDC)) }, axisWidth, y));
            canvas.Children.Add(Place(new System.Windows.Controls.TextBlock { Text = Minutes(tick), FontSize = TypeScale.Caption, Foreground = Brush("TimelineHour"),
                Width = axisWidth - 8, TextAlignment = TextAlignment.Right }, 0, y - 7));
        }
        canvas.Children.Add(Place(new System.Windows.Controls.Border { Width = Math.Max(0, width - axisWidth), Height = 1,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x29, 0xFF, 0xF0, 0xDC)) }, axisWidth, baseline));

        var series = ChartSeries(map);
        var named = map.Ranking.Take(ChartColors.Length).ToArray();
        int peak = map.BusiestHour ?? -1;
        for (int i = 0; i < columns; i++)
        {
            int hour = map.FirstHour + i;
            double x = axisWidth + i * columnWidth;
            if (columns <= 14 || hour % 2 == 0)
                canvas.Children.Add(Place(new System.Windows.Controls.TextBlock { Text = $"{hour}시", FontSize = TypeScale.Caption, Foreground = Brush("TimelineHour"),
                    Width = columnWidth, TextAlignment = TextAlignment.Center }, x, baseline + 5));
            double total = map.HourSeconds(hour);
            if (total <= 0) continue;
            // 아래부터 1위, 2위, 3위, 기타 순으로 쌓는다. 조각 사이 2px 틈, 맨 위만 둥글게.
            var parts = named.Select((app, index) => (Seconds: map.CellSeconds(app.AppName, hour), Color: ChartColors[index])).ToList();
            double other = total - parts.Sum(part => part.Seconds);
            if (other > 30) parts.Add((other, OtherColor));
            parts = parts.Where(part => part.Seconds > 0).ToList();
            double barHeight = Math.Max(3, total / 60 / top * plotHeight);
            double y = baseline;
            for (int p = 0; p < parts.Count; p++)
            {
                double h = parts[p].Seconds / total * barHeight;
                bool last = p == parts.Count - 1;
                double drawn = Math.Max(1, h - (last ? 0 : 2));
                y -= h;
                var segment = new System.Windows.Controls.Border { Width = barWidth, Height = drawn, Background = new System.Windows.Media.SolidColorBrush(parts[p].Color),
                    CornerRadius = last ? new CornerRadius(Math.Min(4, drawn / 2), Math.Min(4, drawn / 2), 0, 0) : new CornerRadius(0), IsHitTestVisible = false };
                canvas.Children.Add(Place(segment, x + (columnWidth - barWidth) / 2, last ? y : y + 2));
            }
            if (hour == peak)
                canvas.Children.Add(Place(new System.Windows.Controls.TextBlock { Text = ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(total)), FontSize = TypeScale.Caption,
                    FontWeight = FontWeights.SemiBold, Foreground = Brush("BlackText"), Width = Math.Max(columnWidth, 60), TextAlignment = TextAlignment.Center },
                    x + columnWidth / 2 - Math.Max(columnWidth, 60) / 2, baseline - barHeight - 17));
            // 마우스를 올릴 칸: 막대보다 넓게 그 시각 전체
            var lines = new List<string> { $"{hour}시 · {ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(total))}" };
            foreach (var (app, index) in named.Select((app, index) => (app, index)))
                if (map.CellSeconds(app.AppName, hour) > 0)
                    lines.Add($"{series[index].Label} {ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(map.CellSeconds(app.AppName, hour)))}");
            if (other > 30) lines.Add($"기타 {ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(other))}");
            var hit = new System.Windows.Controls.Border { Width = columnWidth, Height = plotHeight + topRoom, Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = string.Join("\n", lines) };
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(hit, 0);
            System.Windows.Automation.AutomationProperties.SetName(hit, string.Join(", ", lines));
            canvas.Children.Add(Place(hit, x, 0));
        }
    }

    private static UIElement Place(UIElement element, double left, double top)
    {
        System.Windows.Controls.Canvas.SetLeft(element, left);
        System.Windows.Controls.Canvas.SetTop(element, top);
        return element;
    }

    private void Calendar_Changed(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowNextSchedule);

    private void ShowNextSchedule()
    {
        if (_isClosed) return;
        var next = _calendar.GetNext(DateTimeOffset.Now);
        if (next is null)
        {
            HomeNextScheduleTitle.Text = "예정된 일정 없음";
            HomeNextScheduleWhen.Text = "";
            return;
        }
        var start = next.Schedule.Start!.Value.ToLocalTime();
        string day = start.Date == DateTime.Today ? "오늘"
            : start.Date == DateTime.Today.AddDays(1) ? "내일"
            : start.ToString("M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR"));
        HomeNextScheduleTitle.Text = next.Title.Trim();
        HomeNextScheduleWhen.Text = string.IsNullOrWhiteSpace(next.Schedule.Location)
            ? $"{day} {start:HH:mm}" : $"{day} {start:HH:mm} · {next.Schedule.Location!.Trim()}";
    }

    /// <summary>[최근 리포트]: 오늘 마지막으로 끝난 집중의 길이·시각·차단 수.</summary>
    private void ShowRecentReports(IReadOnlyList<Altong.Client.Data.Repositories.ReportListEntry> reports)
    {
        _homeLatestReport = reports.OrderBy(entry => entry.EndedAt).LastOrDefault();
        if (_homeLatestReport is not { } latest)
        {
            HomeReportDurationText.Text = "—";
            HomeReportMetaText.Text = "오늘은 아직 없어요";
            return;
        }
        HomeReportDurationText.Text = ActivityJournalHour.FormatDuration(latest.EndedAt - latest.StartedAt);
        HomeReportMetaText.Text = $"{latest.StartedAt.ToLocalTime():HH:mm} · 차단 {latest.BlockedCount}";
    }

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DashboardTabs) || DashboardTabs.SelectedIndex < 0)
            return;
        int index = DashboardTabs.SelectedIndex;
        string[] titles = [HomeTitle, "알림 확인", "활동 일지", "설정", "캘린더", "집중 인사이트"];
        switch (index)
        {
            case 1 when NotificationsHost.Content is null:
                var notifications = new DashboardNotificationsView(Results.DashboardNotifications, _calendar);
                notifications.OpenReportRequested += (sessionId, day) => _ = OpenSessionReportAsync(sessionId, day);
                NotificationsHost.Content = notifications;
                break;
            case 4 when ReportCalendarHost.Content is null:
                ReportCalendarHost.Content = _calendarView.Value;
                break;
            case 5 when InsightsHost.Content is null:
                InsightsHost.Content = _insightsView.Value;
                break;
        }
        PageTitleText.Text = titles[index];
        UpdateNavigationSelection();
        UpdateHeader();
        if (_settingsReady && index == 2)
            _ = RefreshDashboardDataAsync();
        if (_settingsReady && index == 0)
            _ = RefreshHomeFocusUsageAsync();
        if (_settingsReady && index == 5)
            _ = _insightsView.Value.RefreshAsync();
    }

    private static string DescribeTimerSettings(FocusTimerSettings settings) => !settings.TimerEnabled
        ? "타이머 꺼짐"
        : settings.BreakMinutes == 0
            ? $"집중 {settings.FocusMinutes}분 · 휴식 없음"
            : $"집중 {settings.FocusMinutes}분 · 휴식 {settings.BreakMinutes}분";

    private void SetRoutineFeedback(string message, bool isError)
    {
        RoutineFeedbackText.Text = message;
        RoutineFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "BlackError" : "MutedText");
    }

    private void FocusModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusModeCoordinator?.RequestToggle();
    }

    private void FocusMode_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateFocusModeView();
                _lastDashboardRefresh = DateTime.MinValue;
                _ = RefreshDashboardDataAsync();
            });
            return;
        }
        UpdateFocusModeView();
        _lastDashboardRefresh = DateTime.MinValue;
        _ = RefreshDashboardDataAsync();
    }

    private void UpdateFocusModeView()
    {
        bool enabled = _focusModeService?.IsEnabled == true;
        HomeFocusHintText.Text = enabled ? "집중모드 켜짐" : "집중모드 꺼짐";
        // 집중 중에는 호박색 점과 글자, 꺼져 있으면 차분한 회색으로 물러난다.
        MotionBehavior.SetBrush(HomeFocusHintText, System.Windows.Controls.TextBlock.ForegroundProperty,
            (System.Windows.Media.Brush)FindResource(enabled ? "BlackAccentText" : "MutedText"));
        MotionBehavior.SetBrush(HomeFocusStatusDot, System.Windows.Shapes.Shape.FillProperty,
            (System.Windows.Media.Brush)FindResource(enabled ? "BlackAction" : "MutedText"));
        MotionBehavior.SetBrush(HomeFocusStatusBadge, System.Windows.Controls.Border.BackgroundProperty,
            (System.Windows.Media.Brush)FindResource(enabled ? "AmberSoft" : "GlassPane"));
        MotionBehavior.SetBrush(HomeFocusStatusBadge, System.Windows.Controls.Border.BorderBrushProperty,
            (System.Windows.Media.Brush)FindResource(enabled ? "AmberSoft" : "GlassPaneRule"));
        ApplyFocusAmbience(enabled);
        UpdateFocusTimerView(); // 큰 숫자도 불씨와 같은 순간에 바뀌게(다음 초를 기다리지 않는다)
        FocusModeToggleButton.Content = enabled ? "집중 모드 끄기" : "집중 모드 켜기";
        FocusModeToggleButton.IsEnabled = _focusModeCoordinator is not null;
        QuickDndCard.ToolTip = _focusModeCoordinator is null
            ? "Windows 방해 금지: 확인 불가"
            : GetWindowsNotificationModeDescription(_focusModeCoordinator.WindowsState);
        QuickDndStateText.Text = _focusModeCoordinator?.WindowsState.Kind switch
        {
            WindowsNotificationModeKind.Unrestricted or WindowsNotificationModeKind.PriorityOnly or WindowsNotificationModeKind.AlarmsOnly => "연결됨",
            WindowsNotificationModeKind.Unsupported => "미지원",
            null or WindowsNotificationModeKind.Error => "확인 불가",
            _ => "확인 중"
        };
        QuickDndStateText.Foreground = (System.Windows.Media.Brush)FindResource(QuickDndStateText.Text == "연결됨" ? "BlackSuccess" : "MutedText");
        UpdateNotificationControlView();
    }

    private bool? _ambientFocus;

    /// <summary>
    /// 집중이 켜졌음을 어느 탭에서든 알아보게: 창 뒤 불씨가 번지고, 창 테두리가 데워지고, 윗변을 따라 빛이 한 번 지나간다.
    /// 처음 그릴 때나 창이 숨어 있을 때는 바로 바꾸고, Windows 애니메이션이 꺼져 있으면 짧은 페이드만 쓴다.
    /// </summary>
    private void ApplyFocusAmbience(bool on)
    {
        if (_ambientFocus == on) return;
        bool first = _ambientFocus is null;
        _ambientFocus = on;
        bool motion = MotionBehavior.IsMotionAllowed;
        var transition = first || !IsVisible ? FocusAmbienceTransition.Instant
            : motion ? FocusAmbienceTransition.Ignite : FocusAmbienceTransition.Fade;
        Backdrop.ShowFocus(on, transition, breathe: IsVisible && motion);
        HomeFocusCard.BorderBrush = (System.Windows.Media.Brush)FindResource(on ? "FocusRimBrush" : "GlassRim");
        double target = on ? 1 : 0;
        if (transition == FocusAmbienceTransition.Instant)
        {
            FocusRim.BeginAnimation(OpacityProperty, null);
            FocusRim.Opacity = target;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(transition == FocusAmbienceTransition.Fade ? 200 : on ? 700 : 350);
        FocusRim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(target, duration)
        {
            EasingFunction = new System.Windows.Media.Animation.ExponentialEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Exponent = 5 }
        });
        if (on && transition == FocusAmbienceTransition.Ignite) PlayFocusSweep();
    }

    /// <summary>
    /// 윗변을 따라 왼쪽에서 오른쪽으로 빛 한 줄이 지나간다(0.9초, 한 번). 밝아지고 사라지는 구간이 모두 창 안에서 보이도록,
    /// 빛의 가운데가 왼쪽 끝에서 출발해 오른쪽 끝에 닿기 전에 사라진다.
    /// </summary>
    private void PlayFocusSweep()
    {
        double width = DashboardSurface.ActualWidth;
        if (width <= 0) return;
        double half = FocusSweep.Width / 2;
        FocusSweep.Visibility = Visibility.Visible;
        var duration = TimeSpan.FromMilliseconds(900);
        var move = new System.Windows.Media.Animation.DoubleAnimation(-half + 24, width - half - 24, duration)
        {
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        var glow = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = duration };
        glow.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromPercent(.25)));
        glow.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromPercent(.55)));
        glow.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromPercent(.85)));
        glow.Completed += (_, _) => FocusSweep.Visibility = Visibility.Collapsed;
        FocusSweepShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, move);
        FocusSweep.BeginAnimation(OpacityProperty, glow);
    }

    private void UpdateNotificationControlView()
    {
        UpdateNotificationSettingsSummary(_focusModeCoordinator?.NativeControl);
        UpdateAutoDndView();
        UpdateQuickAppList();
    }

    /// <summary>[집중 중 막을 앱] 칸의 상태 요약(화면 낭독기·테스트용).</summary>
    private void UpdateNotificationSettingsSummary(INativeFocusController? control)
    {
        string summary = control is null ? "연결 안 됨"
            : control.Backend == FocusControlBackend.Windows ? "Windows 방식"
            : $"자체 제어 · {control.Senders.Count(row => row.IsEligible && control.IsSelected(row.Id))}개 선택";
        System.Windows.Automation.AutomationProperties.SetItemStatus(BlockedAppsPane, summary);
    }

    /// <summary>자동 집중 카드: 켜져 있어도 동작할 수 없는 상태면 그 이유를 카드 설명에 적는다.</summary>
    private void UpdateAutoDndView()
    {
        if (_autoDndSettings is null)
        {
            QuickAutoDndCard.Tag = false;
            QuickAutoDndCard.IsEnabled = false;
            QuickAutoDndText.Text = "이 환경에서는 쓸 수 없어요";
            System.Windows.Automation.AutomationProperties.SetItemStatus(QuickAutoDndCard, "사용 불가");
            return;
        }
        bool enabled = _autoDndSettings.Current.Enabled;
        QuickAutoDndCard.Tag = enabled;
        QuickAutoDndCard.IsEnabled = true;
        System.Windows.Automation.AutomationProperties.SetItemStatus(QuickAutoDndCard, enabled ? "설정 켜짐" : "꺼짐");
        QuickAutoDndText.Text = !enabled ? "작업을 감지하면 집중을 제안해요"
            : _focusModeCoordinator?.CanStartDirectly != true ? "알통 자체 제어에서만 동작해요"
            : _focusModeCoordinator.HasControllableApps != true ? "막을 앱을 골라야 동작해요"
            : "작업을 감지하면 집중을 제안해요";
    }

    private void QuickAutoDndCard_Click(object sender, RoutedEventArgs e)
    {
        if (_autoDndSettings is null) return;
        try
        {
            _autoDndSettings.Save(new AutoDndSettings(!_autoDndSettings.Current.Enabled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            QuickAutoDndText.Text = "설정 저장 실패 · 다시 시도하세요.";
            return;
        }
        UpdateAutoDndView();
        UpdateQuickSettings();
    }

    private void UpdateLaunchAtStartupView()
    {
        if (_startupRegistration is null)
        {
            QuickStartupCard.Tag = false;
            QuickStartupCard.IsEnabled = false;
            QuickStartupText.Text = "이 환경에서는 확인할 수 없어요";
            System.Windows.Automation.AutomationProperties.SetItemStatus(QuickStartupCard, "자동 실행 확인 불가");
            return;
        }
        try
        {
            bool enabled = _startupRegistration.IsEnabled;
            QuickStartupCard.Tag = enabled;
            QuickStartupCard.IsEnabled = true;
            QuickStartupText.Text = "Windows가 켜질 때 트레이에서 대기";
            System.Windows.Automation.AutomationProperties.SetItemStatus(QuickStartupCard, enabled ? "자동 실행 켜짐" : "자동 실행 꺼짐");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            QuickStartupCard.IsEnabled = false;
            QuickStartupText.Text = "시작 프로그램 설정 확인 실패";
            System.Windows.Automation.AutomationProperties.SetItemStatus(QuickStartupCard, "자동 실행 확인 실패");
        }
    }

    private void QuickStartupCard_Click(object sender, RoutedEventArgs e)
    {
        if (_startupRegistration is null) return;
        bool enable = QuickStartupCard.Tag is not true;
        try
        {
            _startupRegistration.SetEnabled(enable);
            QuickStartupCard.Tag = enable;
            QuickStartupText.Text = "Windows가 켜질 때 트레이에서 대기";
            System.Windows.Automation.AutomationProperties.SetItemStatus(QuickStartupCard, enable ? "자동 실행 켜짐" : "자동 실행 꺼짐");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            QuickStartupText.Text = "자동 실행 설정 실패 · 다시 시도하세요.";
            System.Windows.Automation.AutomationProperties.SetItemStatus(QuickStartupCard, "자동 실행 확인 필요");
        }
    }

    /// <summary>설정 탭 ⋯ 메뉴: 세부 설정에만 있던 동작(알림 설정 복구·앱 새로고침·자체 제어 복귀·루틴 알림·종료).</summary>
    private IReadOnlyList<PageMenuItem> SettingsMenuItems()
    {
        var control = _focusModeCoordinator?.NativeControl;
        var items = new List<PageMenuItem>
        {
            new("앱 목록 새로고침", () => _focusModeCoordinator?.RefreshNativeControl(), control is not null),
            new("집중 뒤 알림 설정 복구", () => _focusModeCoordinator?.RetryNativeRecovery(), control is not null)
        };
        // 설정 손상 등으로 Windows 방식으로 돌아갔고 자체 제어를 쓸 수 있을 때만 복귀를 보인다.
        if (control is not null && control.CanUseAltong && _focusModeCoordinator!.Backend == FocusControlBackend.Windows)
            items.Add(new("알통 자체 제어 다시 사용", () => _focusModeCoordinator?.SwitchBackend(FocusControlBackend.Altong)));
        items.Add(new("현재 루틴 알림 보기", () => _showRoutineReminder?.Invoke(), _focusModeService?.IsEnabled == true && _showRoutineReminder is not null));
        items.Add(new("ALTONG 종료", () => _shutdownApplication?.Invoke(), _shutdownApplication is not null, IsDestructive: true));
        return items;
    }

    /// <summary>좁은 창에서는 카드 네 개를 2×2로, 아래 두 칸은 위아래로 쌓는다. 넓으면 두 칸이 창 아래까지 채운다.</summary>
    private void SettingsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = SettingsScroll.ActualWidth < 640;
        QuickSettingsGrid.Columns = compact ? 2 : 4;
        SettingsPanes.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 14);
        SettingsPanes.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(BlockedAppsPane, compact ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(BlockedAppsPane, compact ? 1 : 0);
        BlockedAppsPane.Margin = new Thickness(0, compact ? 14 : 0, 0, 0);
        SettingsPanes.MinHeight = compact ? 0 : Math.Max(300, SettingsScroll.ViewportHeight - 128 - 12 - 2);
    }

    /// <summary>위 카드와 [집중 루틴] 칸을 저장된 설정에 맞춘다. 입력 중인 칸은 건드리지 않는다.</summary>
    private void UpdateQuickSettings()
    {
        var settings = _focusSettings.Current;
        QuickTimerCard.Tag = settings.TimerEnabled;
        QuickTimerText.Text = DescribeTimerSettings(settings);
        System.Windows.Automation.AutomationProperties.SetItemStatus(QuickTimerCard,
            _focusSettings.LoadWarning is not null ? "설정 확인 필요" : DescribeTimerSettings(settings));
        foreach (var preset in RoutinePresetGrid.Children.OfType<System.Windows.Controls.Button>())
        {
            bool selected = settings.TimerEnabled && preset.Tag is string tag && tag == $"{settings.FocusMinutes},{settings.BreakMinutes}";
            System.Windows.Automation.AutomationProperties.SetItemStatus(preset, selected ? "선택됨" : "");
        }
        RoutineOptions.Visibility = settings.TimerEnabled ? Visibility.Visible : Visibility.Collapsed;
        RoutineTimerOffText.Visibility = settings.TimerEnabled ? Visibility.Collapsed : Visibility.Visible;
        if (!RoutineFocusInput.IsKeyboardFocusWithin) RoutineFocusInput.Text = settings.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        if (!RoutineBreakInput.IsKeyboardFocusWithin) RoutineBreakInput.Text = settings.BreakMinutes.ToString(CultureInfo.InvariantCulture);
        AwayAutoEndText.Text = _autoDndSettings?.Current.Enabled == false
            ? "자동 집중을 켜면 15분 동안 입력이 없을 때 집중을 끝내요" : "15분 동안 입력이 없으면 집중을 끝내요";
    }

    /// <summary>집중 루틴 고르기: 바로 저장하고 타이머를 켠다. 저장 버튼은 없다.</summary>
    private void RoutinePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string preset } || preset.Split(',') is not [var focus, var rest]) return;
        SaveRoutine(int.Parse(focus, CultureInfo.InvariantCulture), int.Parse(rest, CultureInfo.InvariantCulture), enableTimer: true);
    }

    private void RoutineInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommitRoutineInputs();
        e.Handled = true;
    }

    private void RoutineInput_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitRoutineInputs();

    /// <summary>직접 입력: 칸을 벗어나거나 Enter를 누르면 저장한다. 잘못된 값이면 저장하지 않고 알려 준다.</summary>
    internal void CommitRoutineInputs()
    {
        bool focusOk = int.TryParse(RoutineFocusInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int focus);
        string restText = RoutineBreakInput.Text.Trim();
        int rest = 0;
        bool restOk = restText.Length == 0 || int.TryParse(restText, NumberStyles.None, CultureInfo.InvariantCulture, out rest);
        var current = _focusSettings.Current;
        if (focusOk && restOk && focus == current.FocusMinutes && rest == current.BreakMinutes) { RoutineFeedbackText.Text = ""; return; }
        if (!focusOk || !restOk || !new FocusTimerSettings(focus, rest, true).IsValid)
        {
            SetRoutineFeedback("집중 1–180분, 휴식 0–60분으로 입력해 주세요.", true);
            return;
        }
        SaveRoutine(focus, rest, enableTimer: current.TimerEnabled);
    }

    private void SaveRoutine(int focus, int rest, bool enableTimer)
    {
        var settings = new FocusTimerSettings(focus, rest, enableTimer);
        try
        {
            _focusSettings.Save(settings);
            SetRoutineFeedback("저장됨 · 다음 집중부터 적용", false);
            UpdateClock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetRoutineFeedback("설정 저장 실패 · 다시 시도하세요.", true);
        }
    }

    private bool _showAllApps;
    private string? _appControlError;
    private List<string> _appOrder = [];

    /// <summary>
    /// [집중 중 막을 앱]: 접혀 있으면 다섯 개(막는 앱 먼저), [앱 목록 전체 보기]를 누르면 같은 칸에서 전부 보인다.
    /// 스위치를 눌러도 줄 순서는 그대로 둔다(누른 줄이 다른 자리로 튀지 않게).
    /// </summary>
    private void UpdateQuickAppList()
    {
        QuickAppList.Children.Clear();
        var control = _focusModeCoordinator?.NativeControl;
        var rows = control?.Senders.Where(row => row.IsEligible).ToArray() ?? [];
        BlockedAppsCountText.Text = control is null ? "" : $"{rows.Length}개 중 {rows.Count(row => control.IsSelected(row.Id))}개";
        BlockedAppsStatusText.Text = _appControlError
            ?? (control?.Backend == FocusControlBackend.Windows ? "Windows 방해 금지 방식이라 앱별로 막지 않아요" : "");
        ShowAllAppsButton.Visibility = rows.Length > 5 || _showAllApps ? Visibility.Visible : Visibility.Collapsed;
        ShowAllAppsButton.Content = _showAllApps ? "접기" : $"앱 목록 전체 보기 · {rows.Length}개";
        if (control is null || rows.Length == 0)
        {
            QuickAppList.Children.Add(new System.Windows.Controls.TextBlock { Text = control is null ? "알림 제어가 연결되지 않았어요" : "제어할 앱이 아직 없어요",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedText"), FontSize = TypeScale.Body, Margin = new Thickness(0, 9, 0, 0) });
            return;
        }
        if (_appOrder.Count != rows.Length || rows.Any(row => !_appOrder.Contains(row.Id)))
            _appOrder = rows.OrderByDescending(row => control.IsSelected(row.Id))
                .ThenBy(row => AppNameDisplayConverter.Format(row.Name), StringComparer.CurrentCultureIgnoreCase).Select(row => row.Id).ToList();
        var ordered = rows.OrderBy(row => _appOrder.IndexOf(row.Id)).Take(_showAllApps ? rows.Length : 5);
        var icons = (AppIconConverter)FindResource("AppIcon");
        foreach (var row in ordered)
        {
            var line = new System.Windows.Controls.Grid();
            line.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(34) });
            line.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            line.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            // 아이콘: Windows 앱 ID로 먼저 찾고, 없으면 이름으로 찾는다.
            var image = AppIdIconResolver.Resolve(row.Id)
                ?? (System.Windows.Media.ImageSource?)icons.Convert(row.Name, typeof(System.Windows.Media.ImageSource), null!, CultureInfo.CurrentCulture);
            line.Children.Add(new System.Windows.Controls.Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = (System.Windows.Media.Brush)FindResource("GlassPaneHover"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Child = new System.Windows.Controls.Image { Width = 18, Height = 18, Source = image } });
            var name = new System.Windows.Controls.TextBlock { Text = AppNameDisplayConverter.Format(row.Name), FontSize = TypeScale.Body, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.Name };
            System.Windows.Controls.Grid.SetColumn(name, 1);
            line.Children.Add(name);
            bool blocked = control.IsSelected(row.Id);
            var toggle = new System.Windows.Controls.Button { Style = (Style)FindResource("AppSwitch"), Tag = blocked, IsEnabled = !control.IsActive, Margin = new Thickness(8, 0, 0, 0),
                ToolTip = control.IsActive ? "집중이 끝나면 바꿀 수 있어요" : blocked ? "집중 중 막음" : "허용" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, $"{AppNameDisplayConverter.Format(row.Name)} {(blocked ? "막음" : "허용")}");
            string id = row.Id;
            toggle.Click += (_, _) =>
            {
                try { control.SetSelected(id, !control.IsSelected(id)); _appControlError = null; }
                catch (Exception ex) { _appControlError = ex.Message; }
                UpdateNotificationControlView();
            };
            System.Windows.Controls.Grid.SetColumn(toggle, 2);
            line.Children.Add(toggle);
            QuickAppList.Children.Add(new System.Windows.Controls.Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xF0, 0xDC)),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = line });
        }
    }

    /// <summary>[앱 목록 전체 보기] ↔ [접기]: 다른 화면으로 가지 않고 이 칸에서 바로 펼친다.</summary>
    private void ShowAllApps_Click(object sender, RoutedEventArgs e)
    {
        _showAllApps = !_showAllApps;
        _appOrder = [];
        UpdateQuickAppList();
        if (_showAllApps) Dispatcher.BeginInvoke(() => QuickAppList.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>타이머 카드: 지금 루틴 그대로 타이머만 켜고 끈다. 바로 저장한다.</summary>
    private void QuickTimerCard_Click(object sender, RoutedEventArgs e)
    {
        var current = _focusSettings.Current;
        SaveRoutine(current.FocusMinutes, current.BreakMinutes, enableTimer: !current.TimerEnabled);
    }

    private static string GetWindowsNotificationModeDescription(WindowsNotificationModeState state)
    {
        return state.Kind switch
        {
            WindowsNotificationModeKind.Unrestricted => "Windows 방해 금지: 꺼짐",
            WindowsNotificationModeKind.PriorityOnly or WindowsNotificationModeKind.AlarmsOnly => "Windows 방해 금지: 켜짐",
            WindowsNotificationModeKind.Unsupported => "Windows 방해 금지 연동 미지원",
            WindowsNotificationModeKind.Error => "Windows 방해 금지: 확인 불가",
            _ => "Windows 방해 금지: 확인 중…",
        };
    }

    private void DashboardSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDashboardCornerClip();

    private void UpdateDashboardCornerClip()
    {
        if (DashboardSurface is null) return;
        double radius = WindowState == WindowState.Maximized ? 0 : 24;
        DashboardSurface.CornerRadius = new CornerRadius(radius);
        FocusRim.CornerRadius = new CornerRadius(radius);
        DashboardSurface.Clip = null;
        if (DashboardInnerGrid is not null)
        {
            double innerRadius = Math.Max(0, radius - 1);
            DashboardInnerGrid.Clip = innerRadius > 0
                ? new RectangleGeometry(new Rect(DashboardInnerGrid.RenderSize), innerRadius, innerRadius)
                : null;
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

    /// <summary>
    /// AllowsTransparency + WindowChrome 조합에서 Windows 11 DWM이 8px 기본 둥근 모서리·테두리를
    /// 덧그려 24px 모서리 바깥에 회색 삼각형 잔상이 비치는 현상을 막는다.
    /// </summary>
    private void DisableNativeWindowFrameChrome()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        const int dwmwaWindowCornerPreference = 33;
        const uint dwmwcpDoNotRound = 1;
        const int dwmwaBorderColor = 34;
        const uint dwmwaColorNone = 0xFFFFFFFE;
        uint corner = dwmwcpDoNotRound;
        uint border = dwmwaColorNone;
        try
        {
            _ = DwmSetWindowAttribute(handle, dwmwaWindowCornerPreference, ref corner, sizeof(uint));
            _ = DwmSetWindowAttribute(handle, dwmwaBorderColor, ref border, sizeof(uint));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private void FitToWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var available = transform.Transform(new Vector(area.Width, area.Height));
        double width = Math.Max(1, available.X - 24);
        double height = Math.Max(1, available.Y - 24);
        MinWidth = Math.Min(640, width);
        MinHeight = Math.Min(520, height);
        Width = Math.Min(920, width);
        Height = Math.Min(780, height);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

}

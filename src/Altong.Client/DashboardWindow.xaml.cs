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
    private ReportCalendarView _calendarView = null!;
    private static readonly System.Globalization.CultureInfo Korean = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");

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
        DashboardCalendarStore? calendar = null)
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
        var notifications = new DashboardNotificationsView(Results.DashboardNotifications, calendar);
        notifications.OpenReportRequested += (sessionId, day) => _ = OpenSessionReportAsync(sessionId, day);
        NotificationsHost.Content = notifications;
        UpdateNavigationSelection();
        _calendarView = new ReportCalendarView(results, calendar, openCalendar, summaryProvider, summaryStatePath);
        _calendarView.SummaryChanged += (_, _) => UpdateHeader();
        ReportCalendarHost.Content = _calendarView;
        _activeWindowTracker.ContextChanged += ActiveWindowTracker_ContextChanged;
        if (_focusModeService is not null)
            _focusModeService.StateChanged += FocusMode_StateChanged;
        if (_focusModeCoordinator is not null)
            _focusModeCoordinator.StateChanged += FocusMode_StateChanged;
        TimerEnabledCheckBox.IsChecked = _focusSettings.Current.TimerEnabled;
        FocusSessionDurationSetting.Text = _focusSettings.Current.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        BreakDurationSetting.Text = _focusSettings.Current.BreakMinutes == 0
            ? "" : _focusSettings.Current.BreakMinutes.ToString(CultureInfo.InvariantCulture);
        _settingsReady = true;
        UpdateTimerOptionsVisibility();
        UpdateTimerSettingsFeedback();
        if (_focusSettings.LoadWarning is { } warning)
            SetSettingsFeedback(warning, true);
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        var version = typeof(DashboardWindow).Assembly.GetName().Version;
        AppVersionText.Text = $"ALTONG · {version}";
        SettingsVersionText.Text = version is null ? "ALTONG" : $"ALTONG {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        UpdateLaunchAtStartupView();
        UpdateFocusModeView();
        SourceInitialized += (_, _) => FitToWorkArea();
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
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _calendar.Changed -= Calendar_Changed;
            Results.PropertyChanged -= Results_PropertyChanged;
            _clockTimer.Stop();
            _activeWindowTracker.ContextChanged -= ActiveWindowTracker_ContextChanged;
            if (_focusModeService is not null)
                _focusModeService.StateChanged -= FocusMode_StateChanged;
            if (_focusModeCoordinator is not null)
                _focusModeCoordinator.StateChanged -= FocusMode_StateChanged;
        };
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
        var settings = _focusSettings.Current;
        if (DashboardTabs.SelectedIndex == 0) PageTitleText.Text = Greeting(DateTime.Now);
        UpdateQuickSettings();
        System.Windows.Automation.AutomationProperties.SetItemStatus(TimerSettingsExpander,
            _focusSettings.LoadWarning is not null ? "설정 확인 필요"
            : !settings.TimerEnabled ? "타이머 꺼짐" : settings.BreakMinutes == 0
                ? $"집중 {settings.FocusMinutes}분 · 휴식 없음"
                : $"집중 {settings.FocusMinutes}분 · 휴식 {settings.BreakMinutes}분");
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
                FocusTimerCaptionText.Text = "집중 진행 중";
                break;

            case FocusRoutinePhase.Break:
                FocusTimerTitleText.Text = "휴식 시간";
                FocusDurationText.Text = FormatRemaining(_focusRoutine.Remaining);
                FocusTimerCaptionText.Text = "휴식 진행 중";
                break;

            case FocusRoutinePhase.Completed:
                FocusTimerTitleText.Text = "집중 시간 완료";
                FocusDurationText.Text = "00:00";
                FocusTimerCaptionText.Text = "집중모드 유지 중";
                break;

            default:
                FocusTimerTitleText.Text = "집중 시간";
                FocusDurationText.Text = $"{_focusSettings.Current.FocusMinutes:00}:00";
                FocusTimerCaptionText.Text = "설정된 집중 시간";
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
        }
        catch (Exception)
        {
            if (!_isClosed)
                HomeFocusUsageStatusText.Text = "집중 기록을 불러오지 못했어요. 자동 재시도 중…";
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
        HomeFocusUsageTotalText.Text = usage.TotalText;
        HomeFocusUsageStatusText.Text = usage.Total == TimeSpan.Zero
            ? "아직 집중 기록 없음"
            : usage.HasRecoveredSession
                ? "휴식 포함 · 종료 누락 시 마지막 기록 기준"
            : "휴식 포함";
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
        PageMenuButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        DashboardDateText.Text = index switch
        {
            2 => _journalSubtitle.Length > 0 ? _journalSubtitle : "집중 중 쓴 앱",
            3 => "자주 바꾸는 것은 위에서 바로",
            4 => _calendarView?.MonthSummary ?? "",
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
            3 =>
            [
                new(SettingsDetailPanel.Visibility == Visibility.Visible ? "빠른 설정으로 돌아가기" : "세부 설정 열기",
                    () => ShowSettingsDetails(SettingsDetailPanel.Visibility != Visibility.Visible)),
                new("현재 루틴 알림 보기", () => _showRoutineReminder?.Invoke(), ShowRoutineReminderButton.IsEnabled),
                new("ALTONG 종료", () => _shutdownApplication?.Invoke(), _shutdownApplication is not null, IsDestructive: true)
            ],
            4 => _calendarView.MenuItems(),
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
        await _calendarView.OpenSessionReportAsync(sessionId, day);
    }

    internal void ShowJournalDetails(bool show)
    {
        _journalDetails = show;
        JournalOverview.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        JournalDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        JournalScroll.ScrollToTop();
    }

    internal void ShowSettingsDetails(bool show)
    {
        SettingsQuickPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsDetailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.ScrollToTop();
    }

    private void JournalBack_Click(object sender, RoutedEventArgs e) => ShowJournalDetails(false);
    private void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowSettingsDetails(false);
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

    private void HomeScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeLayout is null || HomeSidePanel is null) return;
        HomeLayout.Width = Math.Max(0, HomeScroll.ActualWidth - 20);
        // 좁으면 "오늘의 집중 시간"을 타이머 아래로 내린다. 바로가기 세 개는 한 줄을 유지한다.
        bool stacked = HomeScroll.ActualWidth < 600;
        HomeLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 16);
        HomeLayout.ColumnDefinitions[2].Width = new GridLength(stacked ? 0 : 2, GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(HomeFlowCard, stacked ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(HomeFlowCard, stacked ? 1 : 0);
        HomeFlowCard.Margin = new Thickness(0, stacked ? 16 : 0, 0, 0);
        System.Windows.Controls.Grid.SetRow(HomeSidePanel, stacked ? 2 : 1);
    }

    /// <summary>홈 제목. 시간대에 맞는 인사로 맞이한다.</summary>
    internal static string Greeting(DateTime now) => now.Hour switch
    {
        >= 5 and < 11 => "좋은 아침이에요",
        >= 11 and < 17 => "좋은 오후예요",
        >= 17 and < 22 => "좋은 저녁이에요",
        _ => "늦은 밤이에요"
    };

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
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = index == 0 ? Brush("BlackAction") : Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center });
            var icon = new System.Windows.Controls.Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Brush("GlassPaneHover"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = new System.Windows.Controls.Image { Width = 16, Height = 16,
                    Source = (System.Windows.Media.ImageSource?)icons.Convert(app.AppName, typeof(System.Windows.Media.ImageSource), null!, CultureInfo.CurrentCulture) } };
            System.Windows.Controls.Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var middle = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(new System.Windows.Controls.TextBlock { Text = AppNameDisplayConverter.Format(app.AppName), FontSize = 13, FontWeight = FontWeights.SemiBold,
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
            var duration = new System.Windows.Controls.TextBlock { Text = ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(app.Seconds)), FontSize = 12,
                Foreground = Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Controls.Grid.SetColumn(duration, 3);
            row.Children.Add(duration);
            System.Windows.Automation.AutomationProperties.SetName(row, $"{index + 1}위 {AppNameDisplayConverter.Format(app.AppName)} {duration.Text}");
            JournalRankingList.Children.Add(new System.Windows.Controls.Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xF0, 0xDC)),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = row });
        }
        if (map.Ranking.Count == 0)
            JournalRankingList.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{JournalRangeName(_journalRange)}은 집중 중 쓴 앱이 아직 없어요",
                Foreground = Brush("MutedText"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0) });

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
            item.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontSize = 12, Foreground = Brush("MutedText"), VerticalAlignment = VerticalAlignment.Center,
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
            canvas.Children.Add(Place(new System.Windows.Controls.TextBlock { Text = Minutes(tick), FontSize = 10, Foreground = Brush("TimelineHour"),
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
                canvas.Children.Add(Place(new System.Windows.Controls.TextBlock { Text = $"{hour}시", FontSize = 10, Foreground = Brush("TimelineHour"),
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
                canvas.Children.Add(Place(new System.Windows.Controls.TextBlock { Text = ActivityJournalHour.FormatDuration(TimeSpan.FromSeconds(total)), FontSize = 11,
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
            HomeNextScheduleWhen.Text = "일정 · 집중 리포트";
            return;
        }
        var start = next.Schedule.Start!.Value.ToLocalTime();
        string day = start.Date == DateTime.Today ? "오늘"
            : start.Date == DateTime.Today.AddDays(1) ? "내일"
            : start.ToString("M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR"));
        HomeNextScheduleWhen.Text = $"{day} {start:HH:mm} {next.Title.Trim()}";
    }

    private void ShowRecentReports(IReadOnlyList<Altong.Client.Data.Repositories.ReportListEntry> reports)
    {
        // 오늘 끝난 집중들의 알림 합계. 지금 진행 중인 집중은 끝나야 더해진다.
        HomeNotificationSummaryText.Text = reports.Count == 0 ? "통과·차단 알림"
            : $"오늘 차단 {reports.Sum(entry => entry.BlockedCount)} · 통과 {reports.Sum(entry => entry.PassedCount)}";
    }

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DashboardTabs) || DashboardTabs.SelectedIndex < 0)
            return;
        int index = DashboardTabs.SelectedIndex;
        string[] titles = [Greeting(DateTime.Now), "알림 확인", "활동 일지", "설정", "캘린더"];
        PageTitleText.Text = titles[index];
        UpdateNavigationSelection();
        UpdateHeader();
        if (_settingsReady && index == 2)
            _ = RefreshDashboardDataAsync();
        if (_settingsReady && index == 0)
            _ = RefreshHomeFocusUsageAsync();
    }

    private bool TryReadTimerSettings(out FocusTimerSettings settings)
    {
        settings = _focusSettings.Current with { TimerEnabled = false };
        if (TimerEnabledCheckBox.IsChecked != true) return true;
        if (!int.TryParse(FocusSessionDurationSetting.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int focus))
            return false;
        int rest = 0;
        string restText = BreakDurationSetting.Text.Trim();
        if (restText.Length > 0 && !int.TryParse(restText, NumberStyles.None, CultureInfo.InvariantCulture, out rest))
            return false;
        settings = new FocusTimerSettings(focus, rest, TimerEnabled: true);
        return settings.IsValid;
    }

    private static string DescribeTimerSettings(FocusTimerSettings settings) => !settings.TimerEnabled
        ? "타이머 꺼짐"
        : settings.BreakMinutes == 0
            ? $"집중 {settings.FocusMinutes}분 · 휴식 없음"
            : $"집중 {settings.FocusMinutes}분 · 휴식 {settings.BreakMinutes}분";

    private void UpdateTimerOptionsVisibility() => TimerOptionsPanel.Visibility =
        TimerEnabledCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void TimerOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        UpdateTimerOptionsVisibility();
        UpdateTimerSettingsFeedback();
    }

    private void TimerSetting_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_settingsReady) UpdateTimerSettingsFeedback();
    }

    private void UpdateTimerSettingsFeedback()
    {
        bool valid = TryReadTimerSettings(out var settings);
        TimerRoutinePreviewText.Text = !valid
            ? "집중·휴식 시간을 입력하세요."
            : settings.BreakMinutes == 0
                ? $"집중 {settings.FocusMinutes}분 → 완료 알림"
                : $"집중 {settings.FocusMinutes}분 → 휴식 {settings.BreakMinutes}분 → 반복";
        SetSettingsFeedback(valid
            ? settings == _focusSettings.Current
                ? "다음 집중부터 적용"
                : "변경사항 저장 필요 · 다음 집중부터 적용"
            : "집중 1–180분, 휴식 0–60분으로 입력해 주세요.", !valid);
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadTimerSettings(out var settings))
        {
            SetSettingsFeedback("집중 1–180분, 휴식 0–60분으로 입력해 주세요.", true);
            return;
        }

        try
        {
            _focusSettings.Save(settings);
            SetSettingsFeedback("저장 완료 · 다음 집중부터 적용", false);
            UpdateClock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetSettingsFeedback("설정 저장 실패 · 다시 시도하세요.", true);
        }
    }

    private void SetSettingsFeedback(string message, bool isError)
    {
        TimerSettingsFeedbackText.Text = message;
        TimerSettingsFeedbackText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("BlackError")
            : (System.Windows.Media.Brush)FindResource("MutedText");
    }

    private void FocusModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusModeCoordinator?.RequestToggle();
    }

    private void ShowRoutineReminderButton_Click(object sender, RoutedEventArgs e)
    {
        _showRoutineReminder?.Invoke();
    }

    private void ExitApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        _shutdownApplication?.Invoke();
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
        FocusModeToggleButton.Content = enabled ? "집중 모드 끄기" : "집중 모드 켜기";
        FocusModeToggleButton.IsEnabled = _focusModeCoordinator is not null;
        ShowRoutineReminderButton.IsEnabled = enabled && _showRoutineReminder is not null;
        WindowsDndStatusText.Text = _focusModeCoordinator is null
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

    private void UpdateNotificationControlView()
    {
        var control = _focusModeCoordinator?.NativeControl;
        UpdateNotificationSettingsSummary(control);
        NotificationControlStatusText.Text = control is null ? "알림 제어 연결 안 됨"
            : $"현재 방식: {(_focusModeCoordinator!.Backend == FocusControlBackend.Altong ? "알통 자체 제어" : "Windows 방해 금지")}\n{control.Status}";
        // 방식 전환 버튼은 두지 않는다. 설정 손상 등으로 Windows 방식으로 초기화됐고 자체 제어를 쓸 수 있을 때만 복귀 버튼을 보인다.
        // 검증되지 않은 Windows에서는 자동으로 Windows 방식을 쓰므로 버튼을 숨긴다.
        bool canReenable = control is not null && control.CanUseAltong && _focusModeCoordinator!.Backend == FocusControlBackend.Windows;
        UseAltongControlButton.Visibility = canReenable ? Visibility.Visible : Visibility.Collapsed;
        ReenableAltongControlHint.Visibility = UseAltongControlButton.Visibility;
        RefreshNotificationAppsButton.IsEnabled = control is not null;
        RestoreNotificationAppsButton.IsEnabled = control is not null;
        UpdateAutoDndView();
        NotificationSenderList.Children.Clear();
        if (control is null) { UpdateQuickAppList(); return; }
        foreach (var row in control.Senders.Where(row => row.IsEligible))
        {
            var check = new System.Windows.Controls.CheckBox
            {
                Content = AppNameDisplayConverter.Format(row.Name),
                IsChecked = control.IsSelected(row.Id),
                IsEnabled = !control.IsActive,
                ToolTip = row.Name,
                Margin = new Thickness(0, 4, 0, 4)
            };
            System.Windows.Automation.AutomationProperties.SetName(check, AppNameDisplayConverter.Format(row.Name));
            check.Click += (_, _) =>
            {
                try
                {
                    control.SetSelected(row.Id, check.IsChecked == true);
                    UpdateNotificationSettingsSummary(control);
                    UpdateQuickAppList();
                }
                catch (Exception ex)
                {
                    check.IsChecked = control.IsSelected(row.Id);
                    NotificationControlStatusText.Text = ex.Message;
                }
            };
            NotificationSenderList.Children.Add(check);
        }
        if (NotificationSenderList.Children.Count == 0)
        {
            NotificationSenderList.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "제어할 앱 없음",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedText"),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
        UpdateQuickAppList();
    }

    private void UpdateNotificationSettingsSummary(INativeFocusController? control)
    {
        string summary = control is null ? "연결 안 됨"
            : control.Backend == FocusControlBackend.Windows ? "Windows 방식"
            : $"자체 제어 · {control.Senders.Count(row => row.IsEligible && control.IsSelected(row.Id))}개 선택";
        System.Windows.Automation.AutomationProperties.SetItemStatus(NotificationSettingsExpander, summary);
    }

    private void UpdateAutoDndView()
    {
        if (_autoDndSettings is null)
        {
            AutoDndSettingsCard.Visibility = Visibility.Collapsed;
            return;
        }
        bool enabled = _autoDndSettings.Current.Enabled;
        System.Windows.Automation.AutomationProperties.SetItemStatus(AutoDndSettingsExpander, enabled ? "설정 켜짐" : "꺼짐");
        AutoDndEnabledCheckBox.IsChecked = enabled;
        AutoDndStatusText.Text = !enabled
            ? "꺼짐"
            : _focusModeCoordinator?.CanStartDirectly != true
                ? "알통 자체 제어에서 사용 가능"
                : _focusModeCoordinator.HasControllableApps != true
                    ? "제어할 앱 없음 · 앱 목록을 확인하세요."
                    : "켜짐 · 작업 감지 시 제안 · 타이머 완료 시 종료 확인";
    }

    private void AutoDndEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_autoDndSettings is null) return;
        try
        {
            _autoDndSettings.Save(new AutoDndSettings(AutoDndEnabledCheckBox.IsChecked == true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutoDndStatusText.Text = "설정 저장 실패 · 다시 시도하세요.";
            AutoDndEnabledCheckBox.IsChecked = _autoDndSettings.Current.Enabled;
            return;
        }
        UpdateAutoDndView();
    }

    private void UpdateLaunchAtStartupView()
    {
        QuickStartupText.Text = "Windows가 켜질 때 트레이에서 대기";
        if (_startupRegistration is null)
        {
            System.Windows.Automation.AutomationProperties.SetItemStatus(SystemSettingsExpander, "자동 실행 확인 불가");
            LaunchAtStartupCheckBox.Visibility = Visibility.Collapsed;
            LaunchAtStartupStatusText.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            LaunchAtStartupCheckBox.IsChecked = _startupRegistration.IsEnabled;
            System.Windows.Automation.AutomationProperties.SetItemStatus(SystemSettingsExpander,
                LaunchAtStartupCheckBox.IsChecked == true ? "자동 실행 켜짐" : "자동 실행 꺼짐");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            LaunchAtStartupCheckBox.IsEnabled = false;
            LaunchAtStartupStatusText.Text = "시작 프로그램 설정 확인 실패";
            System.Windows.Automation.AutomationProperties.SetItemStatus(SystemSettingsExpander, "자동 실행 확인 실패");
        }
    }

    private void LaunchAtStartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_startupRegistration is null) return;
        bool enabled = LaunchAtStartupCheckBox.IsChecked == true;
        try
        {
            _startupRegistration.SetEnabled(enabled);
            System.Windows.Automation.AutomationProperties.SetItemStatus(SystemSettingsExpander,
                enabled ? "자동 실행 켜짐" : "자동 실행 꺼짐");
            LaunchAtStartupStatusText.Text = enabled
                ? "로그인 시 트레이에서 시작"
                : "자동 실행 꺼짐";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            LaunchAtStartupCheckBox.IsChecked = !enabled;
            LaunchAtStartupStatusText.Text = "자동 실행 설정 실패 · 다시 시도하세요.";
            QuickStartupText.Text = LaunchAtStartupStatusText.Text;
            System.Windows.Automation.AutomationProperties.SetItemStatus(SystemSettingsExpander, "자동 실행 확인 필요");
        }
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
        if (QuickTimerText.Text != "시간 입력을 확인해 주세요 · 세부 설정") QuickTimerText.Text = DescribeTimerSettings(settings);
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
    private void CommitRoutineInputs()
    {
        bool focusOk = int.TryParse(RoutineFocusInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int focus);
        string restText = RoutineBreakInput.Text.Trim();
        int rest = 0;
        bool restOk = restText.Length == 0 || int.TryParse(restText, NumberStyles.None, CultureInfo.InvariantCulture, out rest);
        var current = _focusSettings.Current;
        if (focusOk && restOk && focus == current.FocusMinutes && rest == current.BreakMinutes) { RoutineFeedbackText.Text = ""; return; }
        if (!focusOk || !restOk || !new FocusTimerSettings(focus, rest, true).IsValid)
        {
            RoutineFeedbackText.Text = "집중 1–180분, 휴식 0–60분으로 입력해 주세요.";
            RoutineFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource("BlackError");
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
            TimerEnabledCheckBox.IsChecked = enableTimer;
            FocusSessionDurationSetting.Text = focus.ToString(CultureInfo.InvariantCulture);
            BreakDurationSetting.Text = rest == 0 ? "" : rest.ToString(CultureInfo.InvariantCulture);
            RoutineFeedbackText.Text = "저장됨 · 다음 집중부터 적용";
            RoutineFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource("MutedText");
            UpdateClock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RoutineFeedbackText.Text = "설정 저장 실패 · 다시 시도하세요.";
            RoutineFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource("BlackError");
        }
    }

    /// <summary>[집중 중 막을 앱]: 막는 앱을 먼저 몇 개만. 전체 목록과 복구는 세부 설정의 앱별 알림 제어에 있다.</summary>
    private void UpdateQuickAppList()
    {
        QuickAppList.Children.Clear();
        var control = _focusModeCoordinator?.NativeControl;
        var rows = control?.Senders.Where(row => row.IsEligible).ToArray() ?? [];
        BlockedAppsCountText.Text = control is null ? "" : $"{rows.Length}개 중 {rows.Count(row => control.IsSelected(row.Id))}개";
        if (control is null || rows.Length == 0)
        {
            QuickAppList.Children.Add(new System.Windows.Controls.TextBlock { Text = control is null ? "알림 제어가 연결되지 않았어요" : "제어할 앱이 아직 없어요",
                Foreground = (System.Windows.Media.Brush)FindResource("MutedText"), FontSize = 13, Margin = new Thickness(0, 9, 0, 0) });
            return;
        }
        foreach (var row in rows.OrderByDescending(row => control.IsSelected(row.Id)).Take(5))
        {
            var line = new System.Windows.Controls.Grid { Margin = new Thickness(0) };
            line.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(34) });
            line.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            line.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            var icon = new System.Windows.Controls.Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = (System.Windows.Media.Brush)FindResource("GlassPaneHover"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = new System.Windows.Controls.Image { Width = 16, Height = 16, Source = (System.Windows.Media.ImageSource?)((AppIconConverter)FindResource("AppIcon")).Convert(row.Name, typeof(System.Windows.Media.ImageSource), null!, CultureInfo.CurrentCulture) } };
            line.Children.Add(icon);
            var name = new System.Windows.Controls.TextBlock { Text = AppNameDisplayConverter.Format(row.Name), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
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
                try { control.SetSelected(id, !control.IsSelected(id)); UpdateNotificationControlView(); }
                catch (Exception ex) { NotificationControlStatusText.Text = ex.Message; UpdateQuickAppList(); }
            };
            System.Windows.Controls.Grid.SetColumn(toggle, 2);
            line.Children.Add(toggle);
            QuickAppList.Children.Add(new System.Windows.Controls.Border { BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x12, 0xFF, 0xF0, 0xDC)),
                BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = line });
        }
    }

    private void ShowAllApps_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsDetails(true);
        NotificationSettingsExpander.IsExpanded = true;
        Dispatcher.BeginInvoke(() => NativeNotificationControlCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>타이머 카드: 아래 체크박스를 바꾸고 바로 저장한다. 시간 입력이 잘못돼 저장하지 못하면 되돌린다.</summary>
    private void QuickTimerCard_Click(object sender, RoutedEventArgs e)
    {
        bool enable = TimerEnabledCheckBox.IsChecked != true;
        TimerEnabledCheckBox.IsChecked = enable;
        SaveSettingsButton_Click(sender, e);
        if (_focusSettings.Current.TimerEnabled != enable)
        {
            TimerEnabledCheckBox.IsChecked = _focusSettings.Current.TimerEnabled;
            QuickTimerText.Text = "시간 입력을 확인해 주세요 · 세부 설정";
        }
    }

    private void QuickAutoDndCard_Click(object sender, RoutedEventArgs e)
    {
        AutoDndEnabledCheckBox.IsChecked = AutoDndEnabledCheckBox.IsChecked != true;
        AutoDndEnabledCheckBox_Click(AutoDndEnabledCheckBox, e);
    }

    private void QuickStartupCard_Click(object sender, RoutedEventArgs e)
    {
        LaunchAtStartupCheckBox.IsChecked = LaunchAtStartupCheckBox.IsChecked != true;
        LaunchAtStartupCheckBox_Click(LaunchAtStartupCheckBox, e);
    }

    /// <summary>자주 쓰는 루틴을 눌러 집중·휴식 시간을 채운다. 저장은 [루틴 저장]으로 한다.</summary>
    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string preset } || preset.Split(',') is not [var focus, var rest]) return;
        TimerEnabledCheckBox.IsChecked = true;
        FocusSessionDurationSetting.Text = focus;
        BreakDurationSetting.Text = rest;
    }

    private void UseAltongControl_Click(object sender, RoutedEventArgs e) => _focusModeCoordinator?.SwitchBackend(FocusControlBackend.Altong);
    private void RefreshNotificationApps_Click(object sender, RoutedEventArgs e) => _focusModeCoordinator?.RefreshNativeControl();
    private void RestoreNotificationApps_Click(object sender, RoutedEventArgs e) => _focusModeCoordinator?.RetryNativeRecovery();

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
        DashboardSurface.Clip = new RectangleGeometry(new Rect(DashboardSurface.RenderSize), radius, radius);
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

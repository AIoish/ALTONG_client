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
    private DateTime _lastNotificationsRefresh = DateTime.MinValue;
    private readonly HashSet<string> _selectedNotificationIds = new(StringComparer.Ordinal);
    private NotificationHistoryItem[] _notificationRows = [];
    private int _notificationDisplayLimit = 50;
    private bool _removingNotifications;
    private int _notificationQueryRevision;
    private DateTime _lastFocusUsageRefresh = DateTime.MinValue;
    private DateTime _focusUsageDate;
    private bool _focusUsageLoading;
    private bool _focusUsageRefreshPending;
    private bool? _lastDisplayedFocusState;
    private int _focusUsageRevision;

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
        Results.PropertyChanged += Results_PropertyChanged;
        ShowHomeFocusUsage(SqliteFocusUsageRepository.Aggregate(DateTime.Today, DateTime.UtcNow, []));
        UpdateNotificationActions();
        UpdateNavigationSelection();
        ReportCalendarHost.Content = new ReportCalendarView(results, calendar, openCalendar, summaryProvider, summaryStatePath);
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
        AppVersionText.Text = $"ALTONG · {typeof(DashboardWindow).Assembly.GetName().Version}";
        UpdateLaunchAtStartupView();
        UpdateFocusModeView();
        SourceInitialized += (_, _) => FitToWorkArea();
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
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
            Results.PropertyChanged -= Results_PropertyChanged;
            _clockTimer.Stop();
            _activeWindowTracker.ContextChanged -= ActiveWindowTracker_ContextChanged;
            foreach (var row in _notificationRows) row.PropertyChanged -= NotificationSelection_Changed;
            foreach (var group in BlockedNotificationItemsControl.Items.Cast<NotificationAppGroup>()
                         .Concat(NotificationItemsControl.Items.Cast<NotificationAppGroup>())) group.SetRows([]);
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
        DashboardDateText.Text = DateTime.Now.ToString("M월 d일 dddd");
        UpdateFocusTimerView();
        var settings = _focusSettings.Current;
        HomeRoutineSettingsText.Text = DescribeTimerSettings(settings);
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
            DashboardTabs.SelectedIndex is 1 or 2 &&
            DateTime.UtcNow - _lastNotificationsRefresh >= TimeSpan.FromSeconds(5))
            _ = RefreshDashboardDataAsync();
    }

    private void UpdateFocusTimerView()
    {
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
            if (_isClosed) return;
            if (revision != _focusUsageRevision || day != DateTime.Today)
            {
                _focusUsageRefreshPending = true;
                _lastFocusUsageRefresh = DateTime.MinValue;
                return;
            }
            ShowHomeFocusUsage(usage);
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
        HomeFocusUsageScaleText.Text = usage.ScaleText;
        HomeFocusUsageMidScaleText.Text = usage.MidScaleText;
        HomeFocusUsageStatusText.Text = usage.Total == TimeSpan.Zero
            ? "아직 집중 기록 없음"
            : usage.HasRecoveredSession
                ? "휴식 포함 · 종료 누락 시 마지막 기록 기준"
            : "휴식 포함";
    }

    private async Task RefreshDashboardDataAsync()
    {
        if (_isClosed) return;
        if (_dashboardDataLoading)
        {
            _dashboardDataRefreshPending = true;
            return;
        }
        _dashboardDataLoading = true;
        UpdateNotificationActions();
        if (DashboardTabs.SelectedIndex == 1 && _notificationRows.Length == 0)
        {
            NotificationsEmptyStateText.Text = "알림 불러오는 중…";
            NotificationsEmptyStateText.Visibility = Visibility.Visible;
        }
        _lastNotificationsRefresh = DateTime.UtcNow;
        try
        {
            int page = DashboardTabs.SelectedIndex;
            bool loadNotifications = page != 2;
            bool loadActivity = page != 1;
            var from = DateTime.Today.ToUniversalTime();
            var to = DateTime.UtcNow;
            var notificationRevision = _notificationQueryRevision;
            var notificationLimit = _notificationDisplayLimit;
            var selectedIds = _selectedNotificationIds.ToArray();
            var activeFocus = _focusModeService?.IsEnabled == true ? Results.ActiveSession : null;
            var context = !loadActivity || activeFocus is null
                ? null : _activeWindowTracker.CaptureNow();
            // SQLite executes much of its async API synchronously. Keep reads and aggregation off the UI thread.
            var data = await Task.Run(async () =>
            {
                var journalTask = !loadActivity
                    ? Task.FromResult<IReadOnlyList<ActivityJournalEntry>>(Array.Empty<ActivityJournalEntry>())
                    : Results.ReadActivityJournalAsync(from, to, focusSessionsOnly: true);
                var notificationTask = loadNotifications
                    ? Results.DashboardNotifications.ReadAsync(notificationLimit, selectedIds)
                    : Task.FromResult(DashboardNotificationPage.Empty);
                await Task.WhenAll(notificationTask, journalTask);
                var entries = (await journalTask).ToList();
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
                return (Journal: journal, Notifications: await notificationTask,
                    Usage: ActivityJournalHour.SummarizeApps(journal));
            });
            if (_isClosed) return;
            if (page != DashboardTabs.SelectedIndex || (loadNotifications && notificationRevision != _notificationQueryRevision))
            {
                _dashboardDataRefreshPending = true;
                return;
            }
            if (loadNotifications)
            {
                foreach (var row in _notificationRows) row.PropertyChanged -= NotificationSelection_Changed;
                _selectedNotificationIds.IntersectWith(data.Notifications.Records.Select(record => record.Id));
                var rows = data.Notifications.Records.Select(record => new NotificationHistoryItem(record)
                    { IsSelected = _selectedNotificationIds.Contains(record.Id) }).ToArray();
                _notificationRows = rows;
                foreach (var row in rows) row.PropertyChanged += NotificationSelection_Changed;
                var blocked = rows.Where(row => row.Record.IsPassed == false).ToArray();
                var passed = rows.Where(row => row.Record.IsPassed == true).ToArray();
                BlockedNotificationItemsControl.ItemsSource = NotificationAppGroup.Group(blocked,
                    BlockedNotificationItemsControl.Items.Cast<NotificationAppGroup>());
                NotificationItemsControl.ItemsSource = NotificationAppGroup.Group(passed,
                    NotificationItemsControl.Items.Cast<NotificationAppGroup>());
                BlockedNotificationCountText.Text = blocked.Length.ToString(CultureInfo.InvariantCulture) + (data.Notifications.HasMoreBlocked ? "+" : "");
                PassedNotificationCountText.Text = passed.Length.ToString(CultureInfo.InvariantCulture) + (data.Notifications.HasMorePassed ? "+" : "");
                LoadMoreNotificationsButton.Visibility = data.Notifications.HasMore ? Visibility.Visible : Visibility.Collapsed;
                NotificationsEmptyStateText.Text = "알림 없음";
                NotificationsEmptyStateText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateNotificationActions();
            }

            if (loadActivity)
            {
                var usage = data.Usage;
                double maximum = usage.Count == 0 ? 1 : usage.Max(item => item.Seconds);
                AppUsageItemsControl.ItemsSource = usage
                    .Select(item => item with { Percentage = item.Seconds / maximum * 100 })
                    .ToArray();
                JournalItemsControl.ItemsSource = data.Journal;
                ActivityJournalEmptyText.Visibility = data.Journal.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            DataErrorText.Text = "";
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                DataErrorText.Text = "기록 조회 실패 · 자동 재시도 중…";
                if (_notificationRows.Length == 0) NotificationsEmptyStateText.Visibility = Visibility.Collapsed;
            }
            Console.WriteLine($"[Dashboard] {ex.Message}");
        }
        finally
        {
            _dashboardDataLoading = false;
            if (!_isClosed) UpdateNotificationActions();
            if (_dashboardDataRefreshPending && !_isClosed)
            {
                _dashboardDataRefreshPending = false;
                _ = RefreshDashboardDataAsync();
            }
        }
    }

    private async void LoadMoreNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboardDataLoading || _removingNotifications || _isClosed) return;
        _notificationDisplayLimit += 50;
        _notificationQueryRevision++;
        await RefreshDashboardDataAsync();
    }

    private void NotificationSelection_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NotificationHistoryItem row || e.PropertyName != nameof(NotificationHistoryItem.IsSelected)) return;
        if (row.IsSelected) _selectedNotificationIds.Add(row.Record.Id);
        else _selectedNotificationIds.Remove(row.Record.Id);
        if (_dashboardDataLoading)
        {
            _notificationQueryRevision++;
            _dashboardDataRefreshPending = true;
        }
        UpdateNotificationActions();
    }

    private void UpdateNotificationActions()
    {
        UpdateNotificationGroupActions(SelectAllBlockedNotificationsButton, DismissSelectedBlockedNotificationsButton,
            SelectedBlockedNotificationCountText, false);
        UpdateNotificationGroupActions(SelectAllPassedNotificationsButton, DismissSelectedPassedNotificationsButton,
            SelectedPassedNotificationCountText, true);
        LoadMoreNotificationsButton.IsEnabled = !_removingNotifications && !_dashboardDataLoading;
        LoadMoreNotificationsButton.Content = _dashboardDataLoading ? "불러오는 중…" : "이전 알림 더 보기";
    }

    private void UpdateNotificationGroupActions(System.Windows.Controls.Button button,
        System.Windows.Controls.Button removeButton, System.Windows.Controls.TextBlock countText, bool passed)
    {
        var rows = _notificationRows.Where(row => row.Record.IsPassed == passed).ToArray();
        var selectedCount = rows.Count(row => row.IsSelected);
        countText.Text = $"선택 {selectedCount}개";
        removeButton.IsEnabled = !_removingNotifications && selectedCount > 0;
        button.IsEnabled = !_removingNotifications && rows.Length > 0;
        button.Content = rows.Length > 0 && rows.All(row => row.IsSelected)
            ? "선택 해제" : "전체 선택";
    }

    private void SelectAllNotifications_Click(object sender, RoutedEventArgs e)
    {
        if (_removingNotifications || _isClosed) return;
        var rows = _notificationRows.Where(row => row.Record.IsPassed == (sender == SelectAllPassedNotificationsButton)).ToArray();
        bool select = !rows.All(row => row.IsSelected);
        foreach (var row in rows) row.IsSelected = select;
    }

    private async void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NotificationHistoryItem row })
            await DismissNotificationsAsync([row.Record.Id]);
    }

    private async void DismissSelectedNotifications_Click(object sender, RoutedEventArgs e)
    {
        var passed = sender == DismissSelectedPassedNotificationsButton;
        await DismissNotificationsAsync(_notificationRows
            .Where(row => row.Record.IsPassed == passed && row.IsSelected)
            .Select(row => row.Record.Id).ToArray());
    }

    private async Task DismissNotificationsAsync(string[] ids)
    {
        if (_removingNotifications || ids.Length == 0 || _isClosed) return;
        _removingNotifications = true;
        _notificationQueryRevision++;
        UpdateNotificationActions();
        try
        {
            await Task.Run(() => Results.DashboardNotifications.DismissAsync(ids));
            if (_isClosed) return;
            _selectedNotificationIds.ExceptWith(ids);
            NotificationHistoryStatusText.Text = $"알림 {ids.Length}개 제거됨";
            NotificationHistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                NotificationHistoryStatusText.Text = "제거 실패 · 다시 시도하세요.";
                NotificationHistoryStatusText.Visibility = Visibility.Visible;
            }
            Console.WriteLine($"[Dashboard] 알림 목록 제거 실패: {ex.GetBaseException().Message}");
        }
        finally
        {
            _removingNotifications = false;
            if (!_isClosed)
            {
                _notificationQueryRevision++;
                UpdateNotificationActions();
                await RefreshDashboardDataAsync();
            }
        }
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
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,
                button.Tag is string tag && tag == DashboardTabs.SelectedIndex.ToString(CultureInfo.InvariantCulture)
                    ? "선택됨" : "");
    }

    private void HomeScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeLayout is null || HomeSidePanel is null) return;
        bool compact = HomeScroll.ActualWidth < 740;
        HomeLayout.Width = Math.Max(0, HomeScroll.ActualWidth - 20);
        HomeLayout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 20);
        HomeLayout.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 212);
        HomeMainPanel.Margin = compact ? new Thickness(0) : new Thickness(0, 32, 0, 0);
        System.Windows.Controls.Grid.SetColumn(HomeSidePanel, compact ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(HomeSidePanel, compact ? 1 : 0);
        HomeSidePanel.Margin = new Thickness(0, compact ? 16 : 0, 0, 0);
        HomeSidePanel.ColumnDefinitions[1].Width = new GridLength(compact ? 16 : 0);
        HomeSidePanel.ColumnDefinitions[2].Width = new GridLength(compact ? 1 : 0, GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(HomeSettingsShortcut, compact ? 2 : 0);
        System.Windows.Controls.Grid.SetRow(HomeSettingsShortcut, compact ? 0 : 2);
        HomeCalendarPanel.Height = compact ? 176 : 284;
        HomeCalendarPanel.Margin = new Thickness(0, 0, 0, compact ? 0 : 12);
        HomeCalendarShortcut.Height = compact ? 176 : 238;
        HomeCalendarShortcut.Margin = new Thickness(0, compact ? 0 : 32, 0, 0);
        HomeCalendarIcon.FontSize = compact ? 24 : 28;
        HomeCalendarIcon.Margin = new Thickness(0, 0, 0, compact ? 12 : 24);
        HomeCalendarTitle.FontSize = compact ? 18 : 23;
    }

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DashboardTabs) || DashboardTabs.SelectedIndex < 0)
            return;
        int index = DashboardTabs.SelectedIndex;
        string[] titles = ["대시보드", "알림 확인", "오늘의 집중 활동", "설정", "캘린더"];
        PageTitleText.Text = titles[index];
        MotionBehavior.Acknowledge(PageTitleText);
        UpdateNavigationSelection();
        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_settingsReady && index is 1 or 2)
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
            : $"집중 {settings.FocusMinutes}분 · 휴식 {settings.BreakMinutes}분 반복";

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
                _lastNotificationsRefresh = DateTime.MinValue;
                _ = RefreshDashboardDataAsync();
            });
            return;
        }
        UpdateFocusModeView();
        _lastNotificationsRefresh = DateTime.MinValue;
        _ = RefreshDashboardDataAsync();
    }

    private void UpdateFocusModeView()
    {
        bool enabled = _focusModeService?.IsEnabled == true;
        HomeFocusHintText.Text = enabled ? "집중모드 켜짐" : "집중모드 꺼짐";
        HomeFocusHintText.Foreground = enabled ? (System.Windows.Media.Brush)FindResource("BlackSuccess")
            : (System.Windows.Media.Brush)FindResource("MutedText");
        HomeFocusStatusDot.Fill = enabled ? (System.Windows.Media.Brush)FindResource("BlackSuccess")
            : (System.Windows.Media.Brush)FindResource("MutedText");
        HomeFocusStatusBadge.Background = (System.Windows.Media.Brush)FindResource(enabled ? "BlackSuccessSurface" : "BlackInput");
        HomeFocusStatusBadge.BorderBrush = (System.Windows.Media.Brush)FindResource(enabled ? "BlackSuccessRule" : "BlackRule");
        if (_lastDisplayedFocusState is { } previous && previous != enabled) MotionBehavior.Acknowledge(HomeFocusStatusBadge);
        _lastDisplayedFocusState = enabled;
        FocusModeToggleButton.Content = enabled ? "집중 모드 끄기" : "집중 모드 켜기";
        FocusModeToggleButton.IsEnabled = _focusModeCoordinator is not null;
        ShowRoutineReminderButton.IsEnabled = enabled && _showRoutineReminder is not null;
        WindowsDndStatusText.Text = _focusModeCoordinator is null
            ? "Windows 방해 금지: 확인 불가"
            : GetWindowsNotificationModeDescription(_focusModeCoordinator.WindowsState);
        UpdateNotificationControlView();
    }

    private void UpdateNotificationControlView()
    {
        var control = _focusModeCoordinator?.NativeControl;
        UpdateNotificationSettingsSummary(control);
        NotificationControlStatusText.Text = control is null ? "알림 제어 연결 안 됨"
            : $"현재 방식: {(_focusModeCoordinator!.Backend == FocusControlBackend.Altong ? "알통 자체 제어" : "Windows 방해 금지")}\n{control.Status}";
        UseAltongControlButton.IsEnabled = control is not null && _focusModeCoordinator!.Backend != FocusControlBackend.Altong;
        UseWindowsControlButton.IsEnabled = control is not null && _focusModeCoordinator!.Backend != FocusControlBackend.Windows;
        RefreshNotificationAppsButton.IsEnabled = control is not null;
        RestoreNotificationAppsButton.IsEnabled = control is not null;
        UpdateAutoDndView();
        NotificationSenderList.Children.Clear();
        if (control is null) return;
        foreach (var row in control.Senders.Where(row => row.IsEligible))
        {
            var check = new System.Windows.Controls.CheckBox
            {
                Content = row.Name,
                IsChecked = control.IsSelected(row.Id),
                IsEnabled = !control.IsActive,
                ToolTip = row.Name,
                Margin = new Thickness(0, 4, 0, 4)
            };
            System.Windows.Automation.AutomationProperties.SetName(check, row.Name);
            check.Click += (_, _) =>
            {
                try
                {
                    control.SetSelected(row.Id, check.IsChecked == true);
                    UpdateNotificationSettingsSummary(control);
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
            System.Windows.Automation.AutomationProperties.SetItemStatus(SystemSettingsExpander, "자동 실행 확인 필요");
        }
    }

    private void UseAltongControl_Click(object sender, RoutedEventArgs e) => _focusModeCoordinator?.SwitchBackend(FocusControlBackend.Altong);
    private void UseWindowsControl_Click(object sender, RoutedEventArgs e) => _focusModeCoordinator?.SwitchBackend(FocusControlBackend.Windows);
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

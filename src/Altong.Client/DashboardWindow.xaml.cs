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
using Color = System.Windows.Media.Color;
using ContentControl = System.Windows.Controls.ContentControl;

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
    private int _focusUsageRevision;
    private readonly DashboardNotificationSummariesView _notificationSummaries;
    private NotificationAppGroup? _summaryGroup;
    private ContentControl? _summaryHost;

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
        Action<string>? openCalendar = null)
    {
        Results = results;
        _focusSettings = focusSettings;
        _focusRoutine = focusRoutine;
        _activeWindowTracker = activeWindowTracker;
        _focusModeService = focusModeService;
        _focusModeCoordinator = focusModeCoordinator;
        _showRoutineReminder = showRoutineReminder;
        _shutdownApplication = shutdownApplication;
        InitializeComponent();
        var calendar = new DashboardCalendarStore(summaryStatePath is null ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(summaryStatePath))!, "dashboard-calendar-schedules.json"));
        _notificationSummaries = new(results.Reports, summaryProvider, summaryStatePath, calendar);
        NotificationSummariesHost.Content = _notificationSummaries;
        _summaryHost = NotificationSummariesHost;
        _notificationSummaries.GroupsChanged += SummaryGroups_Changed;
        _notificationSummaries.BackRequested += SummaryBack_Requested;
        Results.PropertyChanged += Results_PropertyChanged;
        ShowHomeFocusUsage(SqliteFocusUsageRepository.Aggregate(DateTime.Today, DateTime.UtcNow, []));
        UpdateNotificationActions();
        UpdateNavigationSelection();
        ReportCalendarHost.Content = new ReportCalendarView(results, calendar, openCalendar);
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
            _notificationSummaries.Stop();
            _notificationSummaries.GroupsChanged -= SummaryGroups_Changed;
            _notificationSummaries.BackRequested -= SummaryBack_Requested;
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
                FocusTimerCaptionText.Text = "집중모드는 계속 켜져 있습니다.";
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
                HomeFocusUsageStatusText.Text = "집중 기록을 불러오지 못했어요. 잠시 후 다시 시도합니다.";
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
            ? "아직 집중 기록이 없어요. 집중모드를 켜면 여기에 표시됩니다."
            : usage.HasRecoveredSession
                ? "종료가 누락된 세션은 마지막 확인 시간까지 반영 · 휴식 포함"
            : "집중모드가 켜져 있던 시간 · 휴식 포함 · 가로축: 시";
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
                NotificationsEmptyStateText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateNotificationActions();
                if (page == 1) await _notificationSummaries.RefreshAsync();
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
                DataErrorText.Text = "기록을 불러오지 못했어요. 잠시 후 자동으로 다시 시도합니다.";
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

    private void SummaryGroups_Changed(object? sender, EventArgs e)
    {
        var counts = _notificationSummaries.AppCounts;
        var groups = BlockedNotificationItemsControl.Items.Cast<NotificationAppGroup>()
            .Concat(NotificationItemsControl.Items.Cast<NotificationAppGroup>()).ToArray();
        foreach (var group in groups) group.SetSummaryCount(counts.GetValueOrDefault(group.AppName));
        var existingApps = groups.Select(group => group.AppName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var old = SummaryAppItemsControl.Items.Cast<NotificationAppGroup>()
            .ToDictionary(group => group.AppName, StringComparer.OrdinalIgnoreCase);
        var extras = counts.Where(pair => !existingApps.Contains(pair.Key)).Select(pair =>
        {
            var group = old.GetValueOrDefault(pair.Key) ?? new NotificationAppGroup(pair.Key);
            group.SetSummaryCount(pair.Value);
            return group;
        }).ToArray();
        if (!SummaryAppItemsControl.Items.Cast<NotificationAppGroup>().SequenceEqual(extras))
            SummaryAppItemsControl.ItemsSource = extras;
        SummaryOnlyAppsPanel.Visibility = extras.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UnassignedSummariesButton.Content = $"앱 미지정 요약 {_notificationSummaries.UnassignedCount}개 보기";
        UnassignedSummariesButton.Visibility = _notificationSummaries.UnassignedCount == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReattachOpenSummary();
    }

    private void ReattachOpenSummary()
    {
        if (_summaryGroup is null) return;
        var lists = new[] { NotificationItemsControl, BlockedNotificationItemsControl, SummaryAppItemsControl };
        var available = lists.SelectMany(list => list.Items.Cast<NotificationAppGroup>()).ToArray();
        if (!available.Contains(_summaryGroup))
        {
            var replacement = available.FirstOrDefault(group =>
                string.Equals(group.AppName, _summaryGroup.AppName, StringComparison.OrdinalIgnoreCase));
            if (replacement is null) { CloseSummary(); return; }
            _summaryGroup.IsSummaryOpen = false;
            _summaryGroup = replacement;
            replacement.IsSummaryOpen = true;
        }
        ((FrameworkElement)Content).UpdateLayout();
        foreach (var list in lists)
        {
            int index = list.Items.IndexOf(_summaryGroup);
            if (index < 0) continue;
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is System.Windows.Controls.ContentPresenter presenter &&
                list.ItemTemplate.FindName("InlineSummaryHost", presenter) is ContentControl host)
                MoveSummaryTo(host);
            return;
        }
    }

    private void ShowAppSummary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: NotificationAppGroup group, Tag: ContentControl host }) return;
        if (ReferenceEquals(_summaryGroup, group) && group.IsSummaryOpen)
            CloseSummary();
        else
            ShowSummary(group.AppName, host, group);
    }

    private void ShowUnassignedSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryGroup is null && NotificationSummariesHost.Visibility == Visibility.Visible) CloseSummary();
        else ShowSummary(null, NotificationSummariesHost);
    }

    private void ShowSummary(string? appName, ContentControl host, NotificationAppGroup? group = null)
    {
        if (_summaryGroup is not null) _summaryGroup.IsSummaryOpen = false;
        _summaryGroup = group;
        if (group is not null) group.IsSummaryOpen = true;
        MoveSummaryTo(host);
        _notificationSummaries.SelectApp(appName);
        NotificationSummariesHost.Visibility = ReferenceEquals(host, NotificationSummariesHost)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // Item containers can be regenerated by an arrival/removal. Reattach the same
    // summary view so pending edits survive without retaining a detached visual tree.
    private void InlineSummaryHost_Loaded(object sender, RoutedEventArgs e) => AttachOpenSummary(sender);
    private void InlineSummaryHost_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachOpenSummary(sender);
    private void AttachOpenSummary(object sender)
    {
        if (sender is ContentControl { DataContext: NotificationAppGroup group } host &&
            ReferenceEquals(group, _summaryGroup) && group.IsSummaryOpen) MoveSummaryTo(host);
    }

    private void MoveSummaryTo(ContentControl host)
    {
        if (ReferenceEquals(_summaryHost, host)) return;
        if (_summaryHost is not null) _summaryHost.Content = null;
        _summaryHost = host;
        host.Content = _notificationSummaries;
    }

    private void CloseSummary()
    {
        if (_summaryGroup is not null) _summaryGroup.IsSummaryOpen = false;
        _summaryGroup = null;
        NotificationSummariesHost.Visibility = Visibility.Collapsed;
        MoveSummaryTo(NotificationSummariesHost);
    }

    private void SummaryBack_Requested(object? sender, EventArgs e) => CloseSummary();

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
            ? "선택 해제" : "표시된 항목 모두 선택";
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
            NotificationHistoryStatusText.Text = $"알림 {ids.Length}개를 목록에서 제거했습니다.";
            NotificationHistoryStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                NotificationHistoryStatusText.Text = "목록에서 제거하지 못했습니다. 잠시 후 다시 시도해 주세요.";
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
        HomeLayout.Width = Math.Max(740, HomeScroll.ActualWidth - 8);
        HomeMainPanel.Margin = HomeScroll.ActualWidth < 740 ? new Thickness(0) : new Thickness(0, 32, 0, 0);
    }

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DashboardTabs) || DashboardTabs.SelectedIndex < 0)
            return;
        int index = DashboardTabs.SelectedIndex;
        string[] titles = ["대시보드", "알림 확인", "오늘의 집중 활동", "설정", "캘린더"];
        PageTitleText.Text = titles[index];
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
        ? "타이머 사용 안 함 · 직접 켜고 끄기"
        : settings.BreakMinutes == 0
            ? $"집중 {settings.FocusMinutes}분 · 휴식 타이머 없음"
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
            ? "시간을 입력하면 루틴을 미리 볼 수 있어요."
            : settings.BreakMinutes == 0
                ? $"집중 {settings.FocusMinutes}분 → 완료 알림"
                : $"집중 {settings.FocusMinutes}분 → 휴식 {settings.BreakMinutes}분 → 반복";
        SetSettingsFeedback(valid
            ? settings == _focusSettings.Current
                ? "다음 집중모드부터 적용됩니다."
                : "변경한 루틴을 저장해 주세요. 다음 집중모드부터 적용됩니다."
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
            SetSettingsFeedback("루틴을 저장했어요. 다음 집중모드부터 적용됩니다.", false);
            UpdateClock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetSettingsFeedback("설정을 저장하지 못했어요. 기존 시간은 유지됩니다. 잠시 후 다시 시도해 주세요.", true);
        }
    }

    private void SetSettingsFeedback(string message, bool isError)
    {
        TimerSettingsFeedbackText.Text = message;
        TimerSettingsFeedbackText.Foreground = isError
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 172, 172))
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
        HomeFocusHintText.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(164, 228, 218))
            : (System.Windows.Media.Brush)FindResource("MutedText");
        HomeFocusStatusDot.Fill = enabled ? new SolidColorBrush(Color.FromRgb(126, 213, 203))
            : (System.Windows.Media.Brush)FindResource("MutedText");
        HomeFocusStatusBadge.Background = new SolidColorBrush(enabled
            ? Color.FromRgb(36, 61, 57) : Color.FromRgb(40, 38, 46));
        HomeFocusStatusBadge.BorderBrush = new SolidColorBrush(enabled
            ? Color.FromRgb(59, 118, 105) : Color.FromRgb(72, 66, 79));
        FocusModeToggleButton.Content = enabled ? "집중 모드 끄기" : "집중 모드 켜기";
        FocusModeToggleButton.IsEnabled = _focusModeCoordinator is not null;
        ShowRoutineReminderButton.IsEnabled = enabled && _showRoutineReminder is not null;
        WindowsDndStatusText.Text = _focusModeCoordinator is null
            ? "Windows 방해 금지 상태를 확인할 수 없습니다."
            : GetWindowsNotificationModeDescription(_focusModeCoordinator.WindowsState);
    }

    private static string GetWindowsNotificationModeDescription(WindowsNotificationModeState state)
    {
        return state.Kind switch
        {
            WindowsNotificationModeKind.Unrestricted => "Windows 방해 금지: 꺼짐",
            WindowsNotificationModeKind.PriorityOnly or WindowsNotificationModeKind.AlarmsOnly => "Windows 방해 금지: 켜짐",
            WindowsNotificationModeKind.Unsupported => "현재 Windows에서는 방해 금지 연동을 지원하지 않습니다.",
            WindowsNotificationModeKind.Error => "Windows 방해 금지 상태를 확인할 수 없습니다.",
            _ => "Windows 방해 금지 상태를 확인하는 중입니다.",
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

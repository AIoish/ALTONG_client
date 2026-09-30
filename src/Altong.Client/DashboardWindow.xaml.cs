using System.Windows;
using System.Windows.Media;
using System.Globalization;
using System.IO;
using Altong.Client.Models;
using Altong.Client.Data.Models;
using Altong.Client.Services;

namespace Altong.Client;

public partial class DashboardWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly FocusSettingsStore _focusSettings;
    private readonly FocusRoutineService _focusRoutine;
    private readonly IActiveWindowTracker _activeWindowTracker;
    private readonly FocusModeService? _focusModeService;
    private readonly FocusModeCoordinator? _focusModeCoordinator;
    private readonly ActivitySessionService? _activitySession;
    private readonly Func<Task>? _startActivity;
    private readonly Func<Task>? _completeActivity;
    private readonly Action? _showRoutineReminder;
    private readonly Action? _shutdownApplication;
    private bool _settingsReady;
    private bool _activityBusy;
    public SessionResultsService Results { get; }
    private bool _dashboardDataLoading;
    private bool _dashboardDataRefreshPending;
    private bool _isClosed;
    private string? _displayedActivityId;
    private DateTime _lastNotificationsRefresh = DateTime.MinValue;

    public DashboardWindow(
        FocusSettingsStore focusSettings,
        FocusRoutineService focusRoutine,
        SessionResultsService results,
        IActiveWindowTracker activeWindowTracker,
        FocusModeService? focusModeService = null,
        FocusModeCoordinator? focusModeCoordinator = null,
        ActivitySessionService? activitySession = null,
        Func<Task>? startActivity = null,
        Func<Task>? completeActivity = null,
        Action? showRoutineReminder = null,
        Action? shutdownApplication = null)
    {
        Results = results;
        _focusSettings = focusSettings;
        _focusRoutine = focusRoutine;
        _activeWindowTracker = activeWindowTracker;
        _focusModeService = focusModeService;
        _focusModeCoordinator = focusModeCoordinator;
        _activitySession = activitySession;
        _startActivity = startActivity;
        _completeActivity = completeActivity;
        _showRoutineReminder = showRoutineReminder;
        _shutdownApplication = shutdownApplication;
        InitializeComponent();
        _activeWindowTracker.ContextChanged += ActiveWindowTracker_ContextChanged;
        if (_focusModeService is not null)
            _focusModeService.StateChanged += FocusMode_StateChanged;
        if (_focusModeCoordinator is not null)
            _focusModeCoordinator.StateChanged += FocusMode_StateChanged;
        if (_activitySession is not null)
            _activitySession.StateChanged += ActivitySession_StateChanged;
        FocusSessionDurationSetting.Text = _focusSettings.Current.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        BreakDurationSetting.Text = _focusSettings.Current.BreakMinutes.ToString(CultureInfo.InvariantCulture);
        _settingsReady = true;
        if (_focusSettings.LoadWarning is { } warning)
            SetSettingsFeedback(warning, true);
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        AppVersionText.Text = $"ALTONG · {typeof(DashboardWindow).Assembly.GetName().Version}";
        UpdateFocusModeView();
        UpdateActivitySessionView();
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
            _clockTimer.Stop();
            _activeWindowTracker.ContextChanged -= ActiveWindowTracker_ContextChanged;
            if (_focusModeService is not null)
                _focusModeService.StateChanged -= FocusMode_StateChanged;
            if (_focusModeCoordinator is not null)
                _focusModeCoordinator.StateChanged -= FocusMode_StateChanged;
            if (_activitySession is not null)
                _activitySession.StateChanged -= ActivitySession_StateChanged;
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
        UpdateActivitySessionView();
        FocusRoutineSettingsText.Text = _focusRoutine.StatusText;
        UpdateFocusTimerView();
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
        switch (_focusRoutine.Phase)
        {
            case FocusRoutinePhase.Focus:
                FocusTimerTitleText.Text = "집중 시간";
                FocusDurationText.Text = FormatRemaining(_focusRoutine.Remaining);
                FocusTimerCaptionText.Text = "집중 진행 중";
                FocusRoutineOverviewText.Text = $"집중 · {FormatRemaining(_focusRoutine.Remaining)} 남음";
                break;

            case FocusRoutinePhase.Break:
                FocusTimerTitleText.Text = "휴식 시간";
                FocusDurationText.Text = FormatRemaining(_focusRoutine.Remaining);
                FocusTimerCaptionText.Text = "휴식 진행 중";
                FocusRoutineOverviewText.Text = $"휴식 · {FormatRemaining(_focusRoutine.Remaining)} 남음";
                break;

            default:
                FocusTimerTitleText.Text = "집중 시간";
                FocusDurationText.Text = $"{_focusSettings.Current.FocusMinutes:00}:00";
                FocusTimerCaptionText.Text = "설정된 집중 시간";
                FocusRoutineOverviewText.Text = $"집중 {_focusSettings.Current.FocusMinutes}분 · 휴식 {_focusSettings.Current.BreakMinutes}분";
                break;
        }
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        long seconds = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
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
        _lastNotificationsRefresh = DateTime.UtcNow;
        try
        {
            var activity = _activitySession?.Current;
            int page = DashboardTabs.SelectedIndex;
            bool loadNotifications = page != 2;
            bool loadActivity = page != 1;
            var from = activity?.StartedAt ?? DateTime.Today.ToUniversalTime();
            var to = DateTime.UtcNow;
            var context = activity is null || !loadActivity ? null : _activeWindowTracker.CaptureNow();
            var captureId = activity is not null && _activitySession?.UsesFocusCapture == true
                ? activity.ActivitySessionId : null;
            DataPeriodText.Text = activity is null ? "기록 대기 중" :
                captureId is not null ? "현재 활동의 집중모드 ON 기록" : "기존 활동 기록";
            ActivityJournalEmptyText.Visibility = activity is null ? Visibility.Visible : Visibility.Collapsed;
            if (activity is null)
            {
                JournalItemsControl.ItemsSource = Array.Empty<ActivityJournalEntry>();
                AppUsageItemsControl.ItemsSource = Array.Empty<SessionAppUsage>();
            }
            // SQLite executes much of its async API synchronously. Keep reads and aggregation off the UI thread.
            var data = await Task.Run(async () =>
            {
                var journalTask = activity is null || !loadActivity
                    ? Task.FromResult<IReadOnlyList<ActivityJournalEntry>>(Array.Empty<ActivityJournalEntry>())
                    : Results.ReadActivityJournalAsync(from, to, captureId);
                var notificationTask = loadNotifications
                    ? Results.ReadNotificationsAsync(captureId is null ? DateTime.Today.ToUniversalTime() : from, to, captureId)
                    : Task.FromResult<IReadOnlyList<NotificationRecord>>(Array.Empty<NotificationRecord>());
                var usageTask = activity is null || !loadActivity
                    ? Task.FromResult<IReadOnlyList<SessionAppUsage>>(Array.Empty<SessionAppUsage>())
                    : Results.ReadAppUsageAsync(from, to, context, captureId);
                await Task.WhenAll(notificationTask, usageTask, journalTask);
                return (Journal: await journalTask, Notifications: await notificationTask, Usage: await usageTask);
            });
            if (_isClosed) return;
            if (activity?.ActivitySessionId != _activitySession?.Current?.ActivitySessionId ||
                page != DashboardTabs.SelectedIndex)
            {
                _dashboardDataRefreshPending = true;
                return;
            }
            if (loadNotifications)
            {
                var rows = data.Notifications.Select(record => new NotificationDisplayItem(record)).ToArray();
                var blocked = rows.Where(row => row.Record.IsPassed == false).Reverse().ToArray();
                var passed = rows.Where(row => row.Record.IsPassed == true).Reverse().ToArray();
                BlockedNotificationItemsControl.ItemsSource = blocked;
                NotificationItemsControl.ItemsSource = passed;
                BlockedNotificationCountText.Text = blocked.Length.ToString(CultureInfo.InvariantCulture);
                PassedNotificationCountText.Text = passed.Length.ToString(CultureInfo.InvariantCulture);
            }

            if (loadActivity)
            {
                var usage = data.Usage;
                double maximum = usage.Count == 0 ? 1 : usage.Max(item => item.Seconds);
                AppUsageItemsControl.ItemsSource = usage
                    .Select(item => item with { Percentage = item.Seconds / maximum * 100 })
                    .ToArray();
                JournalItemsControl.ItemsSource = data.Journal;
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
            if (_dashboardDataRefreshPending && !_isClosed)
            {
                _dashboardDataRefreshPending = false;
                _ = RefreshDashboardDataAsync();
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

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, DashboardTabs) || DashboardTabs.SelectedIndex < 0)
            return;
        int index = DashboardTabs.SelectedIndex;
        string[] titles = ["홈", "알림 확인", "활동 일지", "설정"];
        PageTitleText.Text = titles[index];
        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_settingsReady && index is 1 or 2)
            _ = RefreshDashboardDataAsync();
    }

    private void OpenLatestResult()
    {
        if (Results.Latest is not { } result)
            return;

        var report = new SessionReportWindow(result) { Owner = this };
        report.ShowDialog();
    }

    private bool TryReadTimerSettings(out FocusTimerSettings settings)
    {
        settings = new();
        if (!int.TryParse(FocusSessionDurationSetting.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int focus) ||
            !int.TryParse(BreakDurationSetting.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int rest))
            return false;
        settings = new FocusTimerSettings(focus, rest);
        return settings.IsValid;
    }

    private void TimerSetting_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_settingsReady) return;
        bool valid = TryReadTimerSettings(out var settings);
        SetSettingsFeedback(valid
            ? settings == _focusSettings.Current
                ? "저장한 시간은 다음 집중 모드 시작부터 적용됩니다."
                : $"집중 {settings.FocusMinutes}분 · 휴식 {settings.BreakMinutes}분 — 저장하면 다음 집중 모드부터 적용됩니다."
            : "1분 이상의 정수로 입력해 주세요. 집중은 최대 180분, 휴식은 최대 60분입니다.", !valid);
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadTimerSettings(out var settings))
        {
            SetSettingsFeedback("1분 이상의 정수로 입력해 주세요. 집중은 최대 180분, 휴식은 최대 60분입니다.", true);
            return;
        }

        try
        {
            _focusSettings.Save(settings);
            SetSettingsFeedback($"집중 {settings.FocusMinutes}분 · 휴식 {settings.BreakMinutes}분을 저장했어요. 다음 집중 모드부터 적용됩니다.", false);
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
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(182, 57, 67))
            : (System.Windows.Media.Brush)FindResource("MutedText");
    }

    private void FocusModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusModeCoordinator?.RequestToggle();
    }

    private async void ActivityToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activitySession is null || _startActivity is null || _completeActivity is null)
            return;

        if (_activityBusy) return;
        ActivityErrorText.Text = "";
        _activityBusy = true;
        ActivityToggleButton.IsEnabled = false;
        try
        {
            if (_activitySession.IsRecording)
            {
                await _completeActivity();
                await RefreshDashboardDataAsync();
                OpenLatestResult();
            }
            else
            {
                await _startActivity();
                _lastNotificationsRefresh = DateTime.MinValue;
                await RefreshDashboardDataAsync();
            }
        }
        catch (Exception ex)
        {
            ActivityErrorText.Text = $"기록 처리 실패: {ex.GetBaseException().Message}";
        }
        finally
        {
            _activityBusy = false;
            UpdateActivitySessionView();
        }
    }

    private async void ShowLatestActivityResult_Click(object sender, RoutedEventArgs e)
    {
        var activitySession = _activitySession;
        var completed = activitySession?.LastCompleted;
        if (_activityBusy || activitySession is null || completed is null || completed.EndedAt is null)
            return;
        DateTime endedAt = completed.EndedAt.Value;

        _activityBusy = true;
        ActivityErrorText.Text = "";
        UpdateActivitySessionView();
        try
        {
            // Rebuild from persisted data so late notification classifications are reflected on reopen.
            TimeSpan? recordedDuration = await activitySession.GetRecordedDurationAsync(completed.ActivitySessionId);
            await Results.BuildActivityResultAsync(
                completed.StartedAt,
                endedAt,
                CurrentContext.Empty,
                recordedDuration: recordedDuration,
                activitySessionId: await activitySession.UsesFocusCaptureAsync(completed.ActivitySessionId)
                    ? completed.ActivitySessionId : null);
            OpenLatestResult();
        }
        catch (Exception ex)
        {
            ActivityErrorText.Text = $"결과를 불러오지 못했어요. 다시 시도해 주세요. ({ex.GetBaseException().Message})";
        }
        finally
        {
            _activityBusy = false;
            UpdateActivitySessionView();
        }
    }

    private void ActivitySession_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateActivitySessionView);
            return;
        }
        UpdateActivitySessionView();
    }

    private void UpdateActivitySessionView()
    {
        var activityId = _activitySession?.Current?.ActivitySessionId;
        if (_displayedActivityId != activityId)
        {
            _displayedActivityId = activityId;
            JournalItemsControl.ItemsSource = Array.Empty<ActivityJournalEntry>();
            AppUsageItemsControl.ItemsSource = Array.Empty<SessionAppUsage>();
            _lastNotificationsRefresh = DateTime.MinValue;
        }
        if (activityId is null)
        {
            DataPeriodText.Text = "기록 대기 중";
            ActivityJournalEmptyText.Visibility = Visibility.Visible;
        }
        if (_activitySession?.Current is not null)
        {
            TimeSpan elapsed = _activitySession.GetElapsedDuration(DateTime.UtcNow);
            string elapsedText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            ActivityStatusText.Text = _activitySession.UsesFocusCapture
                ? $"활동 기록 중 · {elapsedText} · 집중모드 {(_focusModeService?.IsEnabled == true ? "ON" : "OFF")}"
                : $"기존 활동 기록 중 · {elapsedText}";
            ActivityToggleButton.Content = "기록 마치기";

        }
        else
        {
            ActivityStatusText.Text = "기록을 시작하고 집중모드를 켜면 앱 사용시간을 수집합니다.";
            ActivityToggleButton.Content = "기록 시작";
            ActivityResultButton.Visibility = _activitySession?.LastCompleted is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            ActivityResultButton.IsEnabled = !_activityBusy;
        }
        if (_activitySession?.Current is not null)
            ActivityResultButton.Visibility = Visibility.Collapsed;
        ActivityToggleButton.IsEnabled = !_activityBusy && _activitySession is not null &&
                                         _startActivity is not null &&
                                         _completeActivity is not null;
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
                UpdateActivitySessionView();
            });
            return;
        }
        UpdateFocusModeView();
        UpdateActivitySessionView();
    }

    private void UpdateFocusModeView()
    {
        bool enabled = _focusModeService?.IsEnabled == true;
        FocusModeStatusText.Text = enabled ? "집중 모드 켜짐" : "집중 모드 꺼짐";
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

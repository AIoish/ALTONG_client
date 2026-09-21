using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Globalization;
using System.IO;
using Altong.Client.Services;

namespace Altong.Client;

/// <summary>
/// 집중 세션 상태와 세션 종료 리포트를 표시할 대시보드 화면 뼈대.
/// 실제 데이터는 공통 계약과 저장 계층이 확정된 뒤 연결한다.
/// </summary>
public partial class DashboardWindow : Window
{
    private readonly HashSet<FrameworkElement> _motionSurfaces = new();
    private bool _ambientMotionRunning;
    private bool _motionSettingsSubscribed;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly FocusSettingsStore _focusSettings;
    private readonly FocusRoutineService _focusRoutine;
    private readonly IActiveWindowTracker _activeWindowTracker;
    private bool _settingsReady;
    public SessionResultsService Results { get; }
    private bool _dashboardDataLoading;
    private DateTime _lastNotificationsRefresh = DateTime.MinValue;
    private SessionResultWindow? _resultWindow;

    public DashboardWindow(
        FocusSettingsStore focusSettings,
        FocusRoutineService focusRoutine,
        SessionResultsService results,
        IActiveWindowTracker activeWindowTracker)
    {
        Results = results;
        _focusSettings = focusSettings;
        _focusRoutine = focusRoutine;
        _activeWindowTracker = activeWindowTracker;
        InitializeComponent();
        FocusSessionDurationSetting.Text = _focusSettings.Current.FocusMinutes.ToString(CultureInfo.InvariantCulture);
        BreakDurationSetting.Text = _focusSettings.Current.BreakMinutes.ToString(CultureInfo.InvariantCulture);
        _settingsReady = true;
        if (_focusSettings.LoadWarning is { } warning)
            SetSettingsFeedback(warning, true);
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        AppVersionText.Text = $"ALTONG · {typeof(DashboardWindow).Assembly.GetName().Version}";
        SourceInitialized += (_, _) => FitToWorkArea();
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            UpdateMotion();
        };
        Loaded += (_, _) =>
        {
            _clockTimer.Start();
            if (!_motionSettingsSubscribed)
            {
                SystemParameters.StaticPropertyChanged += MotionSettingsChanged;
                _motionSettingsSubscribed = true;
            }
            UpdateMotion();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateClock(); _clockTimer.Start(); }
            else _clockTimer.Stop();
            UpdateMotion();
        };
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            if (_motionSettingsSubscribed)
                SystemParameters.StaticPropertyChanged -= MotionSettingsChanged;
            _motionSettingsSubscribed = false;
            StopMotion();
            _motionSurfaces.Clear();
            _resultWindow?.Close();
        };
    }

    // Only presentation concerns belong here; session/DB/AI contracts remain unchanged.
    private void UpdateClock()
    {
        DashboardClockText.Text = DateTime.Now.ToString("HH:mm");
        DashboardDateText.Text = DateTime.Now.ToString("M월 d일 dddd");
        FocusRoutineSettingsText.Text = _focusRoutine.StatusText;
        FocusRoutineOverviewText.Text = _focusRoutine.Phase == FocusRoutinePhase.Idle
            ? $"집중 {_focusSettings.Current.FocusMinutes}분 · 휴식 {_focusSettings.Current.BreakMinutes}분"
            : _focusRoutine.StatusText;
        var context = _activeWindowTracker.CurrentContext;
        CurrentApplicationText.Text = string.IsNullOrWhiteSpace(context.ActiveProcess)
            ? "추적 대기 중"
            : context.ActiveProcess;
        if (IsVisible &&
            DateTime.UtcNow - _lastNotificationsRefresh >= TimeSpan.FromSeconds(5))
            _ = RefreshDashboardDataAsync();
    }

    private async Task RefreshDashboardDataAsync()
    {
        if (_dashboardDataLoading) return;
        _dashboardDataLoading = true;
        _lastNotificationsRefresh = DateTime.UtcNow;
        try
        {
            var from = DateTime.Today.ToUniversalTime();
            var to = DateTime.UtcNow;
            var context = _activeWindowTracker.CaptureNow();
            var notificationTask = Results.ReadNotificationsAsync(from, to);
            var usageTask = Results.ReadAppUsageAsync(from, to, context);
            var workAppsTask = Results.ReadFocusedAppNamesAsync(
                from, to, _focusRoutine.Phase == FocusRoutinePhase.Idle ? null : context);
            await Task.WhenAll(notificationTask, usageTask, workAppsTask);
            var records = await notificationTask;
            var rows = records.Select(record => new NotificationDisplayItem(record)).ToArray();
            var blocked = rows.Where(row => row.Record.IsPassed == false).Reverse().ToArray();
            var passed = rows.Where(row => row.Record.IsPassed == true).Reverse().ToArray();
            BlockedNotificationItemsControl.ItemsSource = blocked;
            NotificationItemsControl.ItemsSource = passed;
            BlockedNotificationCountText.Text = blocked.Length.ToString(CultureInfo.InvariantCulture);
            PassedNotificationCountText.Text = passed.Length.ToString(CultureInfo.InvariantCulture);

            var usage = await usageTask;
            double maximum = usage.Count == 0 ? 1 : usage.Max(item => item.Seconds);
            AppUsageItemsControl.ItemsSource = usage
                .Select(item => item with { Percentage = item.Seconds / maximum * 100 })
                .ToArray();
            TodayWorkAppsItemsControl.ItemsSource = await workAppsTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Dashboard] {ex.Message}");
        }
        finally { _dashboardDataLoading = false; }
    }

    private void OpenBlockedNotifications_Click(object sender, RoutedEventArgs e)
    {
        DashboardTabs.SelectedIndex = 1;
        _ = RefreshDashboardDataAsync();
        Dispatcher.BeginInvoke(() => BlockedNotificationsSection.BringIntoView());
    }

    private void OpenSessionResult_Click(object sender, RoutedEventArgs e)
    {
        if (Results.Latest is not { } result) return;
        if (_resultWindow is not null && !ReferenceEquals(_resultWindow.DataContext, result))
            _resultWindow.Close();
        if (_resultWindow is null)
        {
            _resultWindow = new SessionResultWindow(result) { Owner = this };
            _resultWindow.Closed += (_, _) => _resultWindow = null;
        }
        _resultWindow.Show();
        if (_resultWindow.WindowState == WindowState.Minimized)
            _resultWindow.WindowState = WindowState.Normal;
        _resultWindow.Activate();
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

    private void OpenReport_Click(object sender, RoutedEventArgs e) => DashboardTabs.SelectedIndex = 2;

    private void DashboardPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer viewer ||
            viewer.Content is not System.Windows.Controls.Grid grid) return;
        // Fit normal desktop sizes. Preserve access with page scrolling only on very small work areas.
        grid.Height = viewer.ActualWidth < 740 ? grid.Children.Count * 282 : Math.Max(320, viewer.ActualHeight);
        ArrangeDashboardPage(grid);
    }

    private void DashboardPageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Grid grid) ArrangeDashboardPage(grid);
    }

    private static void ArrangeDashboardPage(System.Windows.Controls.Grid grid)
    {
        if (grid.ActualWidth <= 0 || grid.Children.Count == 0) return;
        bool narrow = grid.ActualWidth < 740;
        string kind = grid.Tag?.ToString() ?? "";
        double[] columns = kind == "Overview" ? new[] { 1.75, 1.12, 1.08 } :
            kind == "Report" ? new[] { 1.5, 1.0, 0.0 } : new[] { 1.0, 1.0, 1.0 };
        if (kind == "Settings") columns = new[] { 1.0, 1.0, 0.0 };
        for (int c = 0; c < 3; c++)
            grid.ColumnDefinitions[c].Width = new GridLength(narrow ? (c == 0 ? 1 : 0) : columns[c], GridUnitType.Star);
        grid.RowDefinitions.Clear();
        if (narrow)
        {
            for (int i = 0; i < grid.Children.Count; i++)
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(kind == "Settings" ? .42 : 1, GridUnitType.Star) });
        }
        for (int i = 0; i < grid.Children.Count; i++)
        {
            if (grid.Children[i] is not FrameworkElement card) continue;
            int row = 0, column = 0, span = 1, rowSpan = 1;
            if (narrow) row = i;
            else if (kind is "Overview" or "Notifications") { column = i; rowSpan = 2; }
            else if (kind == "Report") { column = i; rowSpan = 2; }
            else { row = i < 2 ? 0 : 1; column = i < 2 ? i : 0; span = i == 2 ? 2 : 1; }
            System.Windows.Controls.Grid.SetRow(card, row);
            System.Windows.Controls.Grid.SetColumn(card, column);
            System.Windows.Controls.Grid.SetColumnSpan(card, span);
            System.Windows.Controls.Grid.SetRowSpan(card, rowSpan);
            card.Width = double.NaN;
            card.Height = narrow ? 270 : double.NaN;
            card.Margin = new Thickness(0, 0, !narrow && column + span < (kind == "Report" || kind == "Settings" ? 2 : 3) ? 14 : 0, rowSpan == 2 ? 0 : 12);
        }
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
        MinWidth = Math.Min(900, width);
        MinHeight = Math.Min(600, height);
        Width = Math.Min(1100, width);
        Height = Math.Min(720, height);
    }

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, DashboardTabs))
            return;

        UpdateMotion();
        if (DashboardTabs.SelectedIndex is 0 or 1)
            _ = RefreshDashboardDataAsync();
        if (DashboardTabs.SelectedContent is UIElement selectedContent && MotionAllowed)
            FadeIn(selectedContent, 200);
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

    private bool MotionAllowed => IsLoaded && IsVisible &&
        WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;

    private void MotionSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.Invoke(UpdateMotion);
    }

    private void UpdateMotion()
    {
        if (!MotionAllowed || DashboardTabs?.SelectedIndex != 0)
        {
            StopMotion();
            return;
        }
        if (_ambientMotionRunning)
            return;

        _ambientMotionRunning = true;
        var rotation = new RotateTransform();
        SessionOrbit.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(32))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
        SessionGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(.18, .5, TimeSpan.FromSeconds(3.2))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void FadeIn(UIElement element, int milliseconds)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void Surface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateSurface(sender, -2);
    }

    private void Surface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateSurface(sender, 0);
    }

    private void AnimateSurface(object sender, double offset)
    {
        if (!MotionAllowed || sender is not FrameworkElement surface ||
            Equals(surface.Tag, "CaptionControl") || !surface.IsEnabled)
            return;
        if (surface.RenderTransform is not TranslateTransform translation)
        {
            translation = new TranslateTransform();
            surface.RenderTransform = translation;
        }
        _motionSurfaces.Add(surface);
        // Set the resting value before animating; replace clocks on rapid pointer movement.
        double current = translation.Y;
        translation.Y = offset;
        translation.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(current, offset, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void Details_Expanded(object sender, RoutedEventArgs e)
    {
        if (MotionAllowed && sender is System.Windows.Controls.Expander expander &&
            expander.Template.FindName("ExpandedContent", expander) is UIElement content)
            FadeIn(content, 220);
    }

    private void StopMotion()
    {
        _ambientMotionRunning = false;
        if (SessionOrbit?.RenderTransform is RotateTransform rotation)
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SessionGlow?.BeginAnimation(OpacityProperty, null);
        DashboardContent?.BeginAnimation(OpacityProperty, null);
        foreach (var surface in _motionSurfaces)
        {
            if (surface.RenderTransform is TranslateTransform translation)
            {
                translation.BeginAnimation(TranslateTransform.YProperty, null);
                translation.Y = 0;
            }
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Altong.Client;

public partial class NotificationDockWindow : Window
{
    private const double RightMargin = 16;
    private const double VerticalPositionRatio = 0.6;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private Func<string>? _statusProvider;
    private bool _hideAfterReminder;
    private bool _reminderPending;
    private readonly DockPositionStore _positionStore = new();
    private Point _pressScreenPoint;
    private double _pressTop;
    private bool _isPressed;
    private bool _isDragging;
    private readonly DockNotificationState _notifications;
    private NotificationFlyoutWindow? _notificationFlyout;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isClosed;
    private bool _backgroundEngaged;

    public NotificationDockWindow() : this(new DockNotificationState()) { }

    public NotificationDockWindow(DockNotificationState notifications)
    {
        _notifications = notifications;
        InitializeComponent();
        _statusTimer.Tick += (_, _) => RefreshReminderStatus();
        _hoverTimer.Tick += HoverTimer_Tick;
        _leaveTimer.Tick += LeaveTimer_Tick;
        _notifications.Changed += Notifications_Changed;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        Closed += NotificationDockWindow_Closed;
        IsVisibleChanged += Dock_IsVisibleChanged;
        LocationChanged += Dock_LocationChanged;
        UpdateNotificationVisuals();
    }

    public event EventHandler? ToggleDashboardRequested;

    public bool IsReminderVisible => ReminderBubble.Visibility == Visibility.Visible;

    public void BeginNotificationSession(string sessionId) => _notifications.BeginSession(sessionId);

    public void EndNotificationSession()
    {
        StopHoverTimers();
        _notifications.EndSession();
    }

    public void ReceiveNotification(NotificationRecord notification)
    {
        if (_isClosed) return;
        bool added = false;
        if (_notificationFlyout is { IsVisible: true } flyout)
            flyout.PreserveScrollPosition(() => added = _notifications.Receive(notification));
        else
            added = _notifications.Receive(notification);
        if (added) PlayNotificationPulse();
    }

    public void PositionOnPrimaryWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - RightMargin;
        Top = _positionStore.VerticalRatio is { } ratio
            ? DockPositioning.FromRatio(ratio, workArea.Top, workArea.Height, Height)
            : DockPositioning.ClampTop(
                workArea.Top + (workArea.Height * VerticalPositionRatio) - (Height / 2),
                workArea.Top, workArea.Height, Height);
    }

    public void ShowRoutineReminder(string title, Func<string> statusProvider, string message)
    {
        // 루틴 전환이 사용자가 읽거나 고정한 알림 패널을 닫지 않도록 안내를 보류한다.
        _hideAfterReminder = false;
        _statusProvider = statusProvider;
        ReminderTitleText.Text = title;
        ReminderMessageText.Text = message;
        RefreshReminderStatus();
        _reminderPending = _notifications.IsPanelOpen || _notifications.IsDragging;
        ReminderBubble.Visibility = _reminderPending ? Visibility.Collapsed : Visibility.Visible;
        PositionOnPrimaryWorkArea();
        if (!IsVisible) Show();
        Topmost = false;
        Topmost = true;
        PlayActivationAnimation();
        _statusTimer.Start();
    }

    public void KeepVisible()
    {
        _hideAfterReminder = false;
        PositionOnPrimaryWorkArea();
        if (!IsVisible) Show();
    }

    public void HideAfterCurrentReminder()
    {
        if (IsReminderVisible)
        {
            _hideAfterReminder = true;
            return;
        }

        Hide();
    }

    private void RefreshReminderStatus()
    {
        if (_statusProvider is not null)
            ReminderStatusText.Text = _statusProvider();
    }

    private void HideReminderBubble()
    {
        _statusTimer.Stop();
        _reminderPending = false;
        ReminderBubble.Visibility = Visibility.Collapsed;
        if (_hideAfterReminder)
        {
            _hideAfterReminder = false;
            Hide();
        }
    }

    public void PlayActivationAnimation()
    {
        ActivationHalo.BeginAnimation(UIElement.OpacityProperty, null);
        ActivationGlow.BeginAnimation(UIElement.OpacityProperty, null);
        ActivationGlow.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        ActivationGlow.StrokeDashOffset = 0;

        if (!SystemParameters.ClientAreaAnimation)
        {
            ActivationHalo.Opacity = 0;
            ActivationGlow.Opacity = 0;
            return;
        }

        var orbit = new DoubleAnimation(0, -198, TimeSpan.FromMilliseconds(1050))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1050) };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        var haloPulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1050) };
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.7, KeyTime.FromPercent(0.12)));
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromPercent(0.7)));
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        ActivationHalo.BeginAnimation(UIElement.OpacityProperty, haloPulse);
        ActivationGlow.BeginAnimation(Shape.StrokeDashOffsetProperty, orbit);
        ActivationGlow.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void DockSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!DockSurface.CaptureMouse())
            return;

        _pressScreenPoint = PointToScreen(e.GetPosition(this));
        _pressTop = Top;
        _isPressed = true;
        _isDragging = false;
        _hoverTimer.Stop();
        e.Handled = true;
    }

    private void DockSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPressed)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPointerInteraction();
            return;
        }

        var delta = GetPointerDelta(e);

        if (!_isDragging)
        {
            _isDragging = DockPositioning.IsDrag(
                delta.X, delta.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance);
            if (_isDragging)
            {
                StopHoverTimers();
                _notifications.BeginDrag();
            }
        }

        if (!_isDragging)
            return;

        var workArea = SystemParameters.WorkArea;
        Top = DockPositioning.ClampTop(
            _pressTop + delta.Y, workArea.Top, workArea.Height, Height);
        e.Handled = true;
    }

    private void DockSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPressed)
            return;

        // 마지막 MouseMove가 오지 않은 상태에서 놓아도 드래그를 클릭으로 오인하지 않는다.
        var delta = GetPointerDelta(e);
        var dragged = _isDragging || DockPositioning.IsDrag(
            delta.X, delta.Y,
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance);

        if (dragged)
        {
            if (!_notifications.IsDragging) _notifications.BeginDrag();
            var workArea = SystemParameters.WorkArea;
            Top = DockPositioning.ClampTop(
                _pressTop + delta.Y, workArea.Top, workArea.Height, Height);
            try
            {
                _positionStore.Save(DockPositioning.ToRatio(
                    Top, workArea.Top, workArea.Height, Height));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("미니바 위치를 저장하지 못했습니다.");
            }
        }

        CancelPointerInteraction();
        if (!dragged)
        {
            PlayClickFeedback();
            ToggleDashboardRequested?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void PlayClickFeedback()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;

        var press = new DoubleAnimation(
            fromValue: 1,
            toValue: 0.97,
            duration: TimeSpan.FromMilliseconds(75))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        ClickScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        ClickScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
    }

    private Vector GetPointerDelta(MouseEventArgs e)
    {
        // 화면 픽셀로 얻은 마우스 이동량을 WPF 창 좌표(DIP)로 변환한다.
        var deviceDelta = PointToScreen(e.GetPosition(this)) - _pressScreenPoint;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform?.Transform(deviceDelta) ?? deviceDelta;
    }

    private void DockSurface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isPressed = false;
        _isDragging = false;
        _notifications.EndDrag();
        UpdatePointerState();
    }

    private void CancelPointerInteraction()
    {
        _isPressed = false;
        _isDragging = false;
        if (DockSurface.IsMouseCaptured)
            DockSurface.ReleaseMouseCapture();
        _notifications.EndDrag();
        UpdatePointerState();
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(SystemParameters.WorkArea))
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(PositionOnPrimaryWorkArea);
            return;
        }

        if (!_isPressed)
            PositionOnPrimaryWorkArea();
    }

    private void NotificationDockWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _statusTimer.Stop();
        StopHoverTimers();
        _hoverTimer.Tick -= HoverTimer_Tick;
        _leaveTimer.Tick -= LeaveTimer_Tick;
        _notifications.Changed -= Notifications_Changed;
        if (_notificationFlyout is not null)
        {
            _notificationFlyout.MouseEnter -= Flyout_MouseEnter;
            _notificationFlyout.MouseLeave -= Flyout_MouseLeave;
            _notificationFlyout.Close();
            _notificationFlyout = null;
        }
        IsVisibleChanged -= Dock_IsVisibleChanged;
        LocationChanged -= Dock_LocationChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        Closed -= NotificationDockWindow_Closed;
    }

    private void CloseReminder_Click(object sender, RoutedEventArgs e)
    {
        HideReminderBubble();
        e.Handled = true;
    }

    private bool IsPointerOverNotifications => DockSurface.IsMouseOver ||
        _notificationFlyout?.IsMouseOver == true || IsPointerOverPanelBridge();

    private bool IsPointerOverPanelBridge()
    {
        if (!IsVisible || _notificationFlyout is not { IsVisible: true } flyout) return false;
        // 두 HWND 사이의 좁은 빈틈도 패널 영역으로 취급한다. 좌표는 모두 화면 픽셀이다.
        var dockTop = DockSurface.PointToScreen(new Point());
        var dockBottom = DockSurface.PointToScreen(new Point(DockSurface.ActualWidth, DockSurface.ActualHeight));
        var panelTop = flyout.PointToScreen(new Point(8, 8));
        var panelBottom = flyout.PointToScreen(new Point(flyout.ActualWidth - 8, flyout.ActualHeight - 8));
        double top = Math.Max(dockTop.Y, panelTop.Y);
        double bottom = Math.Min(dockBottom.Y, panelBottom.Y);
        if (bottom <= top || dockTop.X < panelBottom.X) return false;
        var pointer = System.Windows.Forms.Cursor.Position;
        return pointer.X >= panelBottom.X && pointer.X <= dockTop.X && pointer.Y >= top && pointer.Y <= bottom;
    }

    private void DockSurface_MouseEnter(object sender, MouseEventArgs e) => UpdatePointerState();
    private void DockSurface_MouseLeave(object sender, MouseEventArgs e) => UpdatePointerState();
    private void Flyout_MouseEnter(object sender, MouseEventArgs e) => UpdatePointerState();
    private void Flyout_MouseLeave(object sender, MouseEventArgs e) => UpdatePointerState();

    private void UpdatePointerState()
    {
        if (_isClosed) return;
        UpdateBackground();
        if (!_notifications.IsActive || _isPressed || _notifications.IsDragging || !IsVisible)
        {
            StopHoverTimers();
            return;
        }
        // 빈틈에서는 새 MouseLeave가 오지 않으므로 닫힘 타이머로 계속 위치를 확인한다.
        if (DockSurface.IsMouseOver || _notificationFlyout?.IsMouseOver == true)
        {
            _leaveTimer.Stop();
            if (!_notifications.IsPanelOpen && !_hoverTimer.IsEnabled) _hoverTimer.Start();
        }
        else
        {
            _hoverTimer.Stop();
            if (_notifications.IsPanelOpen && !_notifications.IsPinned && !_leaveTimer.IsEnabled)
                _leaveTimer.Start();
        }
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_isClosed || !IsVisible || !DockSurface.IsMouseOver || _isPressed || !_notifications.IsActive) return;
        HideReminderBubble();
        _notifications.OpenPanel();
    }

    private void LeaveTimer_Tick(object? sender, EventArgs e)
    {
        if (_notifications.IsPinned || !_notifications.IsPanelOpen)
        {
            _leaveTimer.Stop();
            return;
        }
        if (IsPointerOverNotifications) return;
        _leaveTimer.Stop();
        _notifications.ClosePanel();
    }

    private void StopHoverTimers()
    {
        _hoverTimer.Stop();
        _leaveTimer.Stop();
    }

    private void Notifications_Changed(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        UpdateNotificationVisuals();
        if (_notifications.IsPanelOpen && IsVisible)
        {
            if (_notificationFlyout is null)
            {
                _notificationFlyout = new NotificationFlyoutWindow(_notifications) { Owner = this };
                _notificationFlyout.MouseEnter += Flyout_MouseEnter;
                _notificationFlyout.MouseLeave += Flyout_MouseLeave;
            }
            _notificationFlyout.Refresh();
            PositionNotificationFlyout();
            if (!_notificationFlyout.IsVisible) _notificationFlyout.Show();
        }
        else
        {
            _notificationFlyout?.Refresh();
            _notificationFlyout?.Hide();
            if (_reminderPending && !_notifications.IsDragging)
            {
                _reminderPending = false;
                ReminderBubble.Visibility = Visibility.Visible;
            }
        }
        UpdateBackground();
        // 핀 해제 시 커서가 이미 바깥이라면 지연 닫기를 시작한다.
        if (_notifications.IsPanelOpen) UpdatePointerState();
    }

    private void PositionNotificationFlyout()
    {
        if (_notificationFlyout is null || !IsVisible) return;
        var workArea = SystemParameters.WorkArea;
        _notificationFlyout.Height = Math.Min(480, Math.Max(1, workArea.Height - 16));
        var dockOrigin = DockSurface.TranslatePoint(new Point(), this);
        // 창의 투명 여백을 고려해 실제 카드와 캡슐 사이 간격은 8 DIP로 맞춘다.
        _notificationFlyout.Left = Left + dockOrigin.X - _notificationFlyout.Width;
        _notificationFlyout.Top = DockPositioning.ClampTop(
            Top + dockOrigin.Y - 8, workArea.Top + 8, workArea.Height - 16, _notificationFlyout.Height);
    }

    private void Dock_LocationChanged(object? sender, EventArgs e) => PositionNotificationFlyout();

    private void Dock_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            StopHoverTimers();
            _notifications.ClosePanel();
        }
    }

    private void UpdateNotificationVisuals()
    {
        // 새 판정·읽음 처리는 이전 펄스를 교체한다. 애니메이션을 누적하지 않는다.
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        StatusDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        var appearance = DockNotificationAppearance.For(_notifications.HighestUrgency);
        var color = (Color)ColorConverter.ConvertFromString(appearance.Color);
        StatusDotBrush.Color = StatusDotGlow.Color = color;
        StatusDotGlow.Opacity = appearance.GlowOpacity;
        StatusDotGlow.BlurRadius = appearance.BlurRadius;
        UnreadBadge.Visibility = _notifications.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnreadBadgeText.Text = _notifications.BadgeText;
        System.Windows.Automation.AutomationProperties.SetItemStatus(DockSurface,
            _notifications.UnreadCount > 0 ? $"미확인 중요 알림 {_notifications.UnreadCount}개" : "미확인 알림 없음");
    }

    private void PlayNotificationPulse()
    {
        if (!SystemParameters.ClientAreaAnimation || _notifications.UnreadCount == 0) return;
        var pulse = new DoubleAnimation(1, 1.45, TimeSpan.FromMilliseconds(160))
        {
            AutoReverse = true, FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        var brighten = new ColorAnimation(StatusDotBrush.Color, Colors.White, TimeSpan.FromMilliseconds(160))
        {
            AutoReverse = true, FillBehavior = FillBehavior.Stop,
        };
        StatusDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, brighten, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateBackground()
    {
        bool engaged = IsVisible && (DockSurface.IsMouseOver || _notifications.IsPanelOpen);
        if (_backgroundEngaged == engaged) return;
        _backgroundEngaged = engaged;
        var target = (Color)ColorConverter.ConvertFromString(engaged
            ? DockNotificationAppearance.EngagedBackground : DockNotificationAppearance.Background);
        if (SystemParameters.ClientAreaAnimation)
            DockBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(160)), HandoffBehavior.SnapshotAndReplace);
        else
        {
            DockBackground.BeginAnimation(SolidColorBrush.ColorProperty, null);
            DockBackground.Color = target;
        }
    }
}

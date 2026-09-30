using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Altong.Client.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

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
    private readonly DockPositionStore _positionStore = new();
    private Point _pressScreenPoint;
    private double _pressTop;
    private bool _isPressed;
    private bool _isDragging;

    public NotificationDockWindow()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => RefreshReminderStatus();
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        Closed += NotificationDockWindow_Closed;
    }

    public event EventHandler? ToggleDashboardRequested;

    public bool IsReminderVisible => ReminderBubble.Visibility == Visibility.Visible;

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
        _hideAfterReminder = false;
        _statusProvider = statusProvider;
        ReminderTitleText.Text = title;
        ReminderMessageText.Text = message;
        RefreshReminderStatus();
        ReminderBubble.Visibility = Visibility.Visible;
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
            ActivationHalo.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550)));
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
    }

    private void CancelPointerInteraction()
    {
        _isPressed = false;
        _isDragging = false;
        if (DockSurface.IsMouseCaptured)
            DockSurface.ReleaseMouseCapture();
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
        _statusTimer.Stop();
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        Closed -= NotificationDockWindow_Closed;
    }

    private void CloseReminder_Click(object sender, RoutedEventArgs e)
    {
        HideReminderBubble();
        e.Handled = true;
    }
}

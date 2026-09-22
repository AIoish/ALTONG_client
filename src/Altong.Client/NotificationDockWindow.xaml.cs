using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

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

    public NotificationDockWindow()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => RefreshReminderStatus();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
        };
    }

    public event EventHandler? ToggleDashboardRequested;

    public bool IsReminderVisible => ReminderBubble.Visibility == Visibility.Visible;

    public void PositionOnPrimaryWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - RightMargin;
        Top = workArea.Top + (workArea.Height * VerticalPositionRatio) - (Height / 2);
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

    private void DockSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ToggleDashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseReminder_Click(object sender, RoutedEventArgs e)
    {
        HideReminderBubble();
        e.Handled = true;
    }
}

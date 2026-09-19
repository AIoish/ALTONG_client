using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Altong.Client;

public partial class NotificationDockWindow : Window
{
    private const double RightMargin = 16;
    private const double VerticalPositionRatio = 0.6;

    public NotificationDockWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? ToggleMainWindowRequested;

    public void PositionOnPrimaryWorkArea()
    {
        var workArea = SystemParameters.WorkArea;

        Left = workArea.Right - Width - RightMargin;
        Top = workArea.Top + (workArea.Height * VerticalPositionRatio) - (Height / 2);
    }

    public void PlayActivationAnimation()
    {
        ActivationHalo.BeginAnimation(UIElement.OpacityProperty, null);
        ActivationGlow.BeginAnimation(UIElement.OpacityProperty, null);
        ActivationGlow.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        ActivationGlow.StrokeDashOffset = 0;

        if (!SystemParameters.ClientAreaAnimation)
        {
            var reducedMotionPulse = new DoubleAnimation(
                fromValue: 1,
                toValue: 0,
                duration: TimeSpan.FromMilliseconds(550));
            ActivationHalo.BeginAnimation(UIElement.OpacityProperty, reducedMotionPulse);
            return;
        }

        var orbit = new DoubleAnimation(
            fromValue: 0,
            toValue: -198,
            duration: TimeSpan.FromMilliseconds(1050))
        {
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseInOut,
            },
        };

        var fade = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(1050),
        };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        var haloPulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(1050),
        };
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
        ToggleMainWindowRequested?.Invoke(this, EventArgs.Empty);
    }
}

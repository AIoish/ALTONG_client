using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;
using Control = System.Windows.Controls.Control;

namespace Altong.Client;

public partial class SummaryBlendView : UserControl
{
    private static readonly DependencyProperty MotionAllowedProperty = DependencyProperty.Register(
        "MotionAllowed", typeof(bool), typeof(SummaryBlendView),
        new PropertyMetadata(true, (view, _) => ((SummaryBlendView)view).UpdatePlayback()));

    private readonly Storyboard _animation = new()
    {
        Duration = TimeSpan.FromSeconds(2.8), RepeatBehavior = RepeatBehavior.Forever
    };
    private bool _requested;
    public bool IsAnimating { get; private set; }

    public SummaryBlendView()
    {
        InitializeComponent();
        Timeline.SetDesiredFrameRate(_animation, 30);
        AddFlight(LeftNotification, 0.3);
        AddFlight(MiddleNotification, 0.8);
        AddFlight(RightNotification, 1.3);
        SetResourceReference(MotionAllowedProperty, SystemParameters.ClientAreaAnimationKey);
        Loaded += (_, _) => UpdatePlayback();
        IsVisibleChanged += (_, _) => UpdatePlayback();
        Unloaded += (_, _) => Stop();
    }

    private void AddFlight(Control notification, double delay)
    {
        double startX = ((TranslateTransform)notification.RenderTransform).X;
        if (startX != 0) Add("RenderTransform.X", startX, 0, delay, 0.9);
        Add("RenderTransform.Y", 0, 80, delay, 0.9);
        Add("Opacity", 1, 0, delay + 0.5, 0.4);

        void Add(string property, double from, double to, double start, double duration)
        {
            var movement = new DoubleAnimation(from, to, TimeSpan.FromSeconds(duration))
            {
                BeginTime = TimeSpan.FromSeconds(start),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(movement, notification);
            Storyboard.SetTargetProperty(movement, new PropertyPath(property));
            _animation.Children.Add(movement);
        }
    }

    public void Start()
    {
        _requested = true;
        UpdatePlayback();
    }

    public void Stop()
    {
        _requested = false;
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        bool play = _requested && IsLoaded && IsVisible && (bool)GetValue(MotionAllowedProperty);
        if (play == IsAnimating) return;
        if (play) _animation.Begin(this, true);
        else _animation.Remove(this);
        IsAnimating = play;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace Altong.Client;

/// <summary>
/// AI 요약이 도는 동안 차단된 알림 카드들이 하나씩 가운데 요약 카드로 모여 섞이는 모습을 보여 준다.
/// 실제 요약 진행률과는 무관한 대기 연출이며, 동작 줄이기에서는 카드를 움직이지 않고 놓아 둔다.
/// </summary>
public partial class SummaryBlendView : UserControl
{
    public const int MaxChips = 6;
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan Travel = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(900);

    private readonly List<(Border Chip, TranslateTransform Move, ScaleTransform Scale)> _chips = [];
    private bool _running;
    private double _laidOutWidth;

    public SummaryBlendView()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            // 창 너비가 바뀌면 카드 자리와 경로를 다시 잡는다.
            if (_running && Math.Abs(e.NewSize.Width - _laidOutWidth) > 1) Restart();
        };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>카드가 실제로 움직이는 중인지. 동작 줄이기에서는 false다.</summary>
    public bool IsAnimating { get; private set; }
    public int ChipCount => _chips.Count;
    internal IReadOnlyList<Border> Chips => _chips.Select(chip => chip.Chip).ToArray();

    /// <summary>섞일 알림을 정한다. 최신 차단 알림부터 최대 6개를 쓴다.</summary>
    public void SetNotifications(IEnumerable<NotificationRecord> notifications)
    {
        Stop();
        ChipLayer.Children.Clear();
        _chips.Clear();
        foreach (var notification in notifications.OrderByDescending(item => item.ReceivedAt).Take(MaxChips).Reverse())
        {
            var chip = CreateChip(notification);
            var move = new TranslateTransform();
            var scale = new ScaleTransform(1, 1);
            chip.RenderTransformOrigin = new Point(0.5, 0.5);
            chip.RenderTransform = new TransformGroup { Children = { scale, move } };
            ChipLayer.Children.Add(chip);
            _chips.Add((chip, move, scale));
        }
    }

    public void Start()
    {
        _running = true;
        if (IsLoaded) Restart();
        else Loaded += StartWhenLoaded;
    }

    public void Stop()
    {
        _running = false;
        Loaded -= StartWhenLoaded;
        IsAnimating = false;
        foreach (var (chip, move, scale) in _chips)
        {
            chip.BeginAnimation(OpacityProperty, null);
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        TargetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TargetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        foreach (var line in new[] { SkeletonLine1, SkeletonLine2, SkeletonLine3 })
            line.BeginAnimation(OpacityProperty, null);
    }

    private void StartWhenLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= StartWhenLoaded;
        if (_running) Restart();
    }

    private void Restart()
    {
        bool running = _running;
        Stop();
        _running = running;
        if (_chips.Count == 0) return;
        UpdateLayout();
        double width = ActualWidth > 0 ? ActualWidth : 480;
        _laidOutWidth = width;
        var slots = Slots(width);
        bool motion = TryFindResource(SystemParameters.ClientAreaAnimationKey) is bool allowed ? allowed : SystemParameters.ClientAreaAnimation;
        IsAnimating = motion;
        double targetY = Stage.Height - SummaryTarget.Height - 4;

        var cycle = Lead + TimeSpan.FromTicks(Stagger.Ticks * slots.Count(slot => slot is not null)) + Travel + Rest;
        var targetPulse = Pulse(cycle);
        for (int i = 0; i < _chips.Count; i++)
        {
            var (chip, move, scale) = _chips[i];
            chip.Visibility = slots[i] is null ? Visibility.Collapsed : Visibility.Visible;
            if (slots[i] is not { } start) continue;
            move.X = start.X;
            move.Y = start.Y;
            chip.Opacity = 1;
            if (!motion) continue;
            int order = i - slots.Count(slot => slot is null);
            var leave = Lead + TimeSpan.FromTicks(Stagger.Ticks * order);
            var arrive = leave + Travel;
            var size = chip.DesiredSize;
            // 요약 카드 한가운데로 들어가며 작아지고 사라진다.
            var end = new Point((width - size.Width) / 2, targetY + (SummaryTarget.Height - size.Height) / 2);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            move.BeginAnimation(TranslateTransform.XProperty, Path(cycle, start.X, end.X, leave, arrive, ease));
            move.BeginAnimation(TranslateTransform.YProperty, Path(cycle, start.Y, end.Y, leave, arrive, ease));
            var shrink = Path(cycle, 1, 0.45, leave, arrive, ease);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            chip.BeginAnimation(OpacityProperty, Fade(cycle, leave, arrive));
            // 카드가 들어올 때마다 요약 카드가 살짝 부푼다.
            targetPulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(arrive - TimeSpan.FromMilliseconds(60))));
            targetPulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.045, KeyTime.FromTimeSpan(arrive + TimeSpan.FromMilliseconds(80)),
                new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            targetPulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(arrive + TimeSpan.FromMilliseconds(320)),
                new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        }
        if (!motion) return;
        TargetScale.BeginAnimation(ScaleTransform.ScaleXProperty, targetPulse);
        TargetScale.BeginAnimation(ScaleTransform.ScaleYProperty, targetPulse);
        int index = 0;
        foreach (var line in new[] { SkeletonLine1, SkeletonLine2, SkeletonLine3 })
        {
            line.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0.8, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(180 * index++),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }

    /// <summary>
    /// 카드 시작 자리. 위쪽 두 줄에 실제 카드 너비로 고르게 나눠 놓는다.
    /// 창이 좁아 다 들어가지 않으면 오래된 카드부터 숨긴다(null). 아랫줄은 반 칸 밀어 지그재그로 둔다.
    /// </summary>
    private List<Point?> Slots(double width)
    {
        const double pad = 4, rowHeight = 50, minGap = 8;
        foreach (var (chip, _, _) in _chips) chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double widest = _chips.Max(chip => chip.Chip.DesiredSize.Width);
        int perRow = Math.Clamp((int)((width - pad * 2 + minGap) / (widest + minGap)), 1, 3);
        int shown = Math.Min(_chips.Count, perRow * 2);
        int hidden = _chips.Count - shown;
        var slots = new List<Point?>(new Point?[hidden]);
        var visible = _chips.Skip(hidden).Select(chip => chip.Chip.DesiredSize.Width).ToArray();
        for (int row = 0; row * perRow < shown; row++)
        {
            var widths = visible.Skip(row * perRow).Take(perRow).ToArray();
            double gap = Math.Max(minGap, (width - pad * 2 - widths.Sum()) / (widths.Length + 1));
            double x = pad + gap * (row == 0 ? 1 : 0.5);
            foreach (var chipWidth in widths)
            {
                slots.Add(new Point(Math.Min(x, Math.Max(pad, width - pad - chipWidth)), pad + row * rowHeight));
                x += chipWidth + gap;
            }
        }
        return slots;
    }

    private static DoubleAnimationUsingKeyFrames Path(TimeSpan cycle, double from, double to, TimeSpan leave, TimeSpan arrive, IEasingFunction ease) =>
        new()
        {
            Duration = cycle, RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(leave)),
                new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(arrive), ease),
                new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(cycle))
            }
        };

    private static DoubleAnimationUsingKeyFrames Fade(TimeSpan cycle, TimeSpan leave, TimeSpan arrive) =>
        new()
        {
            Duration = cycle, RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(leave + TimeSpan.FromTicks(Travel.Ticks * 6 / 10))),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(arrive)),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(cycle))
            }
        };

    private static DoubleAnimationUsingKeyFrames Pulse(TimeSpan cycle) =>
        new()
        {
            Duration = cycle, RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)) }
        };

    private Border CreateChip(NotificationRecord notification)
    {
        string from = NotificationSenderResolver.Resolve(notification.AppName, notification.Sender, notification.Title)
            ?? (string.IsNullOrWhiteSpace(notification.Title) ? AppNameDisplayConverter.Format(notification.AppName) : notification.Title.Trim());
        string body = KakaoNotificationText.IsUnreadBody(notification.Body) ? notification.Title : notification.Body;
        body = string.Join(" ", (body ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (body == from) body = "";
        var icon = new Image
        {
            Width = 18, Height = 18, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top,
            Source = (ImageSource?)new AppIconConverter().Convert(notification.AppName, typeof(ImageSource), null!, CultureInfo.InvariantCulture)
        };
        var text = new StackPanel { MaxWidth = 132 };
        text.Children.Add(new TextBlock
        {
            Text = from, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("BlackText"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (body.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = body, FontSize = 11, Foreground = (Brush)FindResource("BlackMuted"), Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        return new Border
        {
            Child = row, Padding = new Thickness(10, 7, 12, 7), CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("BlackSurface"), BorderBrush = (Brush)FindResource("BlackRule"),
            BorderThickness = new Thickness(1), Opacity = 1,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)FindResource("BlackShadowColor"), Opacity = 0.35, BlurRadius = 10, ShadowDepth = 2, Direction = 270
            }
        };
    }
}

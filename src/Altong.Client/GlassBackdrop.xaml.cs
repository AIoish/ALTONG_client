using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Altong.Client;

/// <summary>집중이 켜지고 꺼질 때 창 뒤 빛을 바꾸는 방식.</summary>
public enum FocusAmbienceTransition
{
    /// <summary>바로 바꾼다(창이 숨어 있거나 처음 그릴 때).</summary>
    Instant,
    /// <summary>짧게 겹쳐 바꾼다(Windows 애니메이션 끔).</summary>
    Fade,
    /// <summary>불씨가 번지듯 켜진다.</summary>
    Ignite
}

/// <summary>흑연 유리 창의 배경. 창 안에 직접 그린 번진 빛 위로 반투명 판이 올라간다.</summary>
public partial class GlassBackdrop : System.Windows.Controls.UserControl
{
    // 고정(Freeze)해 두어야 다른 UI 스레드의 창에서도 같이 쓸 수 있다.
    private static readonly IEasingFunction Arrive = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 });

    private static ExponentialEase Frozen(ExponentialEase ease)
    {
        ease.Freeze();
        return ease;
    }
    private bool _focusOn;

    public GlassBackdrop() => InitializeComponent();

    internal double EmberLevel => EmberLayer.Opacity;

    /// <summary>
    /// 집중이 켜지면 타이머 쪽(왼쪽 위)에서 호박빛 불씨가 창 뒤로 번진다. 꺼지면 그보다 빨리 식는다.
    /// </summary>
    public void ShowFocus(bool on, FocusAmbienceTransition transition, bool breathe)
    {
        _focusOn = on;
        SetBreathing(false);
        double target = on ? 1 : 0;
        if (transition == FocusAmbienceTransition.Instant)
        {
            EmberLayer.BeginAnimation(OpacityProperty, null);
            EmberScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            EmberScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            EmberLayer.Opacity = target;
            EmberScale.ScaleX = EmberScale.ScaleY = 1;
            SetBreathing(on && breathe);
            return;
        }
        var duration = TimeSpan.FromMilliseconds(transition == FocusAmbienceTransition.Fade ? 200 : on ? 700 : 350);
        var fade = new DoubleAnimation(target, duration) { EasingFunction = Arrive };
        fade.Completed += (_, _) => { if (_focusOn == on) SetBreathing(on && breathe); };
        EmberLayer.BeginAnimation(OpacityProperty, fade);
        if (on && transition == FocusAmbienceTransition.Ignite)
        {
            // 작은 불씨가 번지듯: 70%에서 제 크기로.
            var grow = new DoubleAnimation(.7, 1, duration) { EasingFunction = Arrive };
            EmberScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            EmberScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>
    /// 집중하는 동안 불씨가 아주 느리게 숨 쉰다(6초 주기). 창이 보일 때만 돌고, 1초에 20번만 그려 가볍게 둔다.
    /// </summary>
    public void SetBreathing(bool enabled)
    {
        if (!enabled || !_focusOn)
        {
            EmberCore.BeginAnimation(OpacityProperty, null);
            EmberCore.Opacity = 1;
            return;
        }
        var breath = new DoubleAnimation(1, .8, TimeSpan.FromSeconds(3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(breath, 20);
        EmberCore.BeginAnimation(OpacityProperty, breath);
    }
}

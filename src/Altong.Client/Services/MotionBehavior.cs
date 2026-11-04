using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Path = System.Windows.Shapes.Path;

namespace Altong.Client.Services;

/// <summary>Non-blocking feedback; Windows' animation setting controls every transition.</summary>
public static class MotionBehavior
{
    private static readonly DependencyPropertyDescriptor PressedDescriptor =
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    internal static bool? AnimationOverride { get; set; }
    private static bool MotionAllowed => AnimationOverride ?? SystemParameters.ClientAreaAnimation;
    private static readonly Duration FeedbackDuration = TimeSpan.FromMilliseconds(160);
    private static readonly Duration TransitionDuration = TimeSpan.FromMilliseconds(190);

    public static readonly DependencyProperty ButtonFeedbackProperty = DependencyProperty.RegisterAttached(
        "ButtonFeedback", typeof(bool), typeof(MotionBehavior), new PropertyMetadata(false, ButtonFeedbackChanged));
    public static bool GetButtonFeedback(DependencyObject element) => (bool)element.GetValue(ButtonFeedbackProperty);
    public static void SetButtonFeedback(DependencyObject element, bool value) => element.SetValue(ButtonFeedbackProperty, value);

    public static readonly DependencyProperty FadeOnRevealProperty = DependencyProperty.RegisterAttached(
        "FadeOnReveal", typeof(bool), typeof(MotionBehavior), new PropertyMetadata(false, FadeOnRevealChanged));
    public static bool GetFadeOnReveal(DependencyObject element) => (bool)element.GetValue(FadeOnRevealProperty);
    public static void SetFadeOnReveal(DependencyObject element, bool value) => element.SetValue(FadeOnRevealProperty, value);

    public static readonly DependencyProperty RotateOnToggleProperty = DependencyProperty.RegisterAttached(
        "RotateOnToggle", typeof(bool), typeof(MotionBehavior), new PropertyMetadata(false, RotateOnToggleChanged));
    public static bool GetRotateOnToggle(DependencyObject element) => (bool)element.GetValue(RotateOnToggleProperty);
    public static void SetRotateOnToggle(DependencyObject element, bool value) => element.SetValue(RotateOnToggleProperty, value);

    public static readonly DependencyProperty TextFeedbackProperty = DependencyProperty.RegisterAttached(
        "TextFeedback", typeof(bool), typeof(MotionBehavior), new PropertyMetadata(false, TextFeedbackChanged));
    public static bool GetTextFeedback(DependencyObject element) => (bool)element.GetValue(TextFeedbackProperty);
    public static void SetTextFeedback(DependencyObject element, bool value) => element.SetValue(TextFeedbackProperty, value);

    private static void ButtonFeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ButtonBase button) return;
        if ((bool)args.NewValue)
        {
            button.Loaded += ButtonLoaded;
            button.Unloaded += ButtonUnloaded;
            button.MouseEnter += ButtonMouseEnter;
            button.MouseLeave += ButtonMouseLeave;
            button.IsEnabledChanged += ButtonEnabledChanged;
            if (button.IsLoaded) ButtonLoaded(button, new RoutedEventArgs());
        }
        else
        {
            ButtonUnloaded(button, new RoutedEventArgs());
            button.Loaded -= ButtonLoaded;
            button.Unloaded -= ButtonUnloaded;
            button.MouseEnter -= ButtonMouseEnter;
            button.MouseLeave -= ButtonMouseLeave;
            button.IsEnabledChanged -= ButtonEnabledChanged;
        }
    }

    private static void ButtonLoaded(object sender, RoutedEventArgs args)
    {
        var button = (ButtonBase)sender;
        PressedDescriptor.RemoveValueChanged(button, ButtonPressedChanged);
        PressedDescriptor.AddValueChanged(button, ButtonPressedChanged);
    }

    private static void ButtonUnloaded(object sender, RoutedEventArgs args)
    {
        var button = (ButtonBase)sender;
        PressedDescriptor.RemoveValueChanged(button, ButtonPressedChanged);
        button.BeginAnimation(UIElement.OpacityProperty, null);
        button.SetValue(UIElement.OpacityProperty, 1d);
    }

    private static void ButtonMouseEnter(object sender, System.Windows.Input.MouseEventArgs args) => SetButtonState((ButtonBase)sender, true);
    private static void ButtonMouseLeave(object sender, System.Windows.Input.MouseEventArgs args) => SetButtonState((ButtonBase)sender, false);
    private static void ButtonPressedChanged(object? sender, EventArgs args)
    {
        if (sender is ButtonBase button) SetButtonState(button, button.IsMouseOver);
    }
    private static void ButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        SetButtonState((ButtonBase)sender, ((ButtonBase)sender).IsMouseOver);

    private static void SetButtonState(ButtonBase button, bool hovered)
    {
        double target = !button.IsEnabled ? 1 : button.IsPressed ? .82 : hovered ? .92 : 1;
        double current = button.Opacity;
        button.BeginAnimation(UIElement.OpacityProperty, null);
        button.SetValue(UIElement.OpacityProperty, target);
        if (MotionAllowed && button.IsLoaded && Math.Abs(target - current) > .001)
            button.BeginAnimation(UIElement.OpacityProperty, Animation(current, target, FeedbackDuration));
    }

    private static void FadeOnRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)args.NewValue) element.IsVisibleChanged += RevealVisibilityChanged;
        else element.IsVisibleChanged -= RevealVisibilityChanged;
    }

    private static void RevealVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if ((bool)args.NewValue && element.IsLoaded && MotionAllowed)
            element.BeginAnimation(UIElement.OpacityProperty, Animation(.82, 1, TransitionDuration));
    }

    private static void TextFeedbackChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not TextBlock text) return;
        if ((bool)args.NewValue)
        {
            text.Loaded += TextLoaded;
            text.Unloaded += TextUnloaded;
            if (text.IsLoaded) TextLoaded(text, new RoutedEventArgs());
        }
        else
        {
            TextUnloaded(text, new RoutedEventArgs());
            text.Loaded -= TextLoaded;
            text.Unloaded -= TextUnloaded;
        }
    }

    private static void TextLoaded(object sender, RoutedEventArgs args)
    {
        TextDescriptor.RemoveValueChanged(sender, TextChanged);
        TextDescriptor.AddValueChanged(sender, TextChanged);
    }

    private static void TextUnloaded(object sender, RoutedEventArgs args)
    {
        TextDescriptor.RemoveValueChanged(sender, TextChanged);
        ((TextBlock)sender).BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static void TextChanged(object? sender, EventArgs args)
    {
        if (sender is TextBlock text) Acknowledge(text);
    }

    private static void RotateOnToggleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Path path) return;
        if ((bool)args.NewValue) { path.Loaded += ChevronLoaded; path.Unloaded += ChevronUnloaded; }
        else { ChevronUnloaded(path, new RoutedEventArgs()); path.Loaded -= ChevronLoaded; path.Unloaded -= ChevronUnloaded; }
    }

    private static void ChevronLoaded(object sender, RoutedEventArgs args)
    {
        var path = (Path)sender;
        if (path.TemplatedParent is not ToggleButton toggle) return;
        toggle.Checked -= ChevronChanged;
        toggle.Unchecked -= ChevronChanged;
        toggle.Checked += ChevronChanged;
        toggle.Unchecked += ChevronChanged;
        path.RenderTransform = new RotateTransform(toggle.IsChecked == true ? 180 : 0);
    }

    private static void ChevronUnloaded(object sender, RoutedEventArgs args)
    {
        if (((Path)sender).TemplatedParent is not ToggleButton toggle) return;
        toggle.Checked -= ChevronChanged;
        toggle.Unchecked -= ChevronChanged;
    }

    private static void ChevronChanged(object sender, RoutedEventArgs args)
    {
        var toggle = (ToggleButton)sender;
        if (toggle.Template.FindName("Chevron", toggle) is not Path { RenderTransform: RotateTransform transform }) return;
        double target = toggle.IsChecked == true ? 180 : 0;
        double current = transform.Angle;
        transform.BeginAnimation(RotateTransform.AngleProperty, null);
        transform.SetValue(RotateTransform.AngleProperty, target);
        if (MotionAllowed && toggle.IsLoaded && Math.Abs(target - current) > .001)
            transform.BeginAnimation(RotateTransform.AngleProperty, Animation(current, target, TransitionDuration));
    }

    public static void Acknowledge(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.IsLoaded && MotionAllowed)
            element.BeginAnimation(UIElement.OpacityProperty, Animation(.85, 1, TransitionDuration));
    }

    private static DoubleAnimation Animation(double from, double to, Duration duration) => new(from, to, duration)
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Altong.Client.Services;

/// <summary>Uses a small pixel step without changing keyboard, scrollbar or touch scrolling.</summary>
public static class ScrollWheelBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollWheelBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        if ((bool)args.NewValue) viewer.PreviewMouseWheel += OnMouseWheel;
        else viewer.PreviewMouseWheel -= OnMouseWheel;
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || args.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
        // Preview events run outside-in. Only the nearest viewer that can move owns the wheel.
        // At a boundary its parent can take over, without applying the same delta twice.
        for (var node = args.OriginalSource as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (node is not ScrollViewer viewer || !GetIsEnabled(viewer)) continue;
            if (viewer.ScrollableHeight <= 0 ||
                (args.Delta > 0 ? viewer.VerticalOffset <= 0 : viewer.VerticalOffset >= viewer.ScrollableHeight)) continue;
            if (!ReferenceEquals(viewer, sender)) return;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - args.Delta / 120d * 24);
            args.Handled = true;
            return;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node is Visual
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}

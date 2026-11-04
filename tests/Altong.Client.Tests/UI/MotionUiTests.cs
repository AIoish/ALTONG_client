using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class MotionUiTests
{
    [TestMethod]
    public void Disclosure_UpdatesImmediately_WhileChevronAndRevealFollowMotionPreference() => RunSta(() =>
    {
        var report = new SessionReportWindow(new SessionResult(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, [], [], true));
        try
        {
            MotionBehavior.AnimationOverride = true;
            report.Show();
            Pump(30);
            var expander = (Expander)report.FindName("QuickRepliesExpander");
            var header = (ToggleButton)expander.Template.FindName("DisclosureHeader", expander);
            var chevron = (Path)header.Template.FindName("Chevron", header);
            var transform = (RotateTransform)chevron.RenderTransform;
            var content = (FrameworkElement)expander.Template.FindName("ExpandedContent", expander);

            expander.IsExpanded = true;
            Pump(30);
            Assert.AreEqual(Visibility.Visible, content.Visibility, "내용은 애니메이션을 기다리지 않고 열려야 합니다.");
            Assert.IsTrue(transform.HasAnimatedProperties, "펼침 방향 표시가 전환되어야 합니다.");
            Assert.IsTrue(content.HasAnimatedProperties, "열린 내용에 짧은 전환을 적용해야 합니다.");
            SavePreview(report, "disclosure-opening-600.png");
            expander.IsExpanded = false;
            Pump(20);
            Assert.AreEqual(Visibility.Collapsed, content.Visibility, "닫기는 즉시 반영되어야 합니다.");
            expander.IsExpanded = true;
            Pump(240);
            Assert.AreEqual(180, transform.Angle, .1, $"빠르게 반복해도 최종 상태에 맞아야 합니다. toggle={header.IsChecked}, base={transform.GetAnimationBaseValue(RotateTransform.AngleProperty)}, animated={transform.HasAnimatedProperties}");
            SavePreview(report, "disclosure-open-600.png");

            MotionBehavior.AnimationOverride = false;
            expander.IsExpanded = false;
            Pump(20);
            Assert.AreEqual(Visibility.Collapsed, content.Visibility);
            Assert.AreEqual(0, transform.Angle, .1);
            Assert.IsFalse(transform.HasAnimatedProperties, "동작 줄이기에서는 회전을 즉시 적용해야 합니다.");
            expander.IsExpanded = true;
            Pump(20);
            Assert.AreEqual(180, transform.Angle, .1);
            Assert.IsFalse(content.HasAnimatedProperties);
            Assert.AreEqual(Visibility.Visible, content.Visibility);
            report.Width = 480;
            report.Height = 380;
            Pump(30);
            SavePreview(report, "disclosure-reduced-480.png");
        }
        finally { report.Close(); MotionBehavior.AnimationOverride = null; }
    });

    [TestMethod]
    public void ButtonFeedback_DoesNotDelayClick_AndReducedMotionIsImmediate() => RunSta(() =>
    {
        var report = new SessionReportWindow(new SessionResult(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, [], [], true));
        try
        {
            MotionBehavior.AnimationOverride = true;
            report.Show();
            Pump(30);
            var button = (Button)report.FindName("SkipSaveButton");
            Assert.IsTrue(MotionBehavior.GetButtonFeedback(button));
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.IsTrue(button.HasAnimatedProperties);
            Pump(210);
            Assert.AreEqual(.92, button.Opacity, .01, $"base={button.GetAnimationBaseValue(UIElement.OpacityProperty)}, animated={button.HasAnimatedProperties}, hovered={button.IsMouseOver}");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Pump(210);
            Assert.AreEqual(1, button.Opacity, .01);

            MotionBehavior.AnimationOverride = false;
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            Assert.AreEqual(.92, button.Opacity, .01);
            Assert.IsFalse(button.HasAnimatedProperties);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(report.IsVisible, "버튼 동작은 전환 완료를 기다리지 않아야 합니다.");
        }
        finally { if (report.IsVisible) report.Close(); MotionBehavior.AnimationOverride = null; }
    });

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SavePreview(Window window, string filename)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content;
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(System.IO.Path.Combine(directory, filename));
        encoder.Save(file);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }
}

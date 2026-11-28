using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SummaryBlendUiTests
{
    [TestMethod]
    public void ThreeNotificationShapes_MoveTowardSummary_AndRespectReducedMotion() => RunSta(() =>
    {
        var blend = new SummaryBlendView();
        var host = new Window
        {
            Width = 480, Height = 240, Content = blend, ShowActivated = false,
            Background = (Brush)blend.FindResource("BlackPaper"), WindowStyle = WindowStyle.None
        };
        try
        {
            blend.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            blend.Start();
            host.Show();
            Assert.IsFalse(blend.IsAnimating);
            var shapes = new[] { "LeftNotification", "MiddleNotification", "RightNotification" }
                .Select(name => (Control)blend.FindName(name)).ToArray();
            Assert.IsTrue(shapes.All(shape => shape.IsVisible && shape.ActualWidth > 0));
            var left = (TranslateTransform)shapes[0].RenderTransform;
            double startX = left.X;
            SavePreview(host, "summary-blend-start.png");

            blend.Resources[SystemParameters.ClientAreaAnimationKey] = true;
            Assert.IsTrue(blend.IsAnimating);
            Pump(TimeSpan.FromMilliseconds(850));
            Assert.IsTrue(left.X > startX && left.Y > 0);
            SavePreview(host, "summary-blend-moving.png");
            host.Width = 380;
            Assert.IsTrue(shapes.All(shape => shape.IsVisible));
            blend.Stop();
            Pump(TimeSpan.FromMilliseconds(60));
            Assert.IsFalse(blend.IsAnimating);
            Assert.AreEqual(startX, left.X);
            Assert.AreEqual(0d, left.Y);
        }
        finally { host.Close(); }
    });

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        do
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
    }

    private static void SavePreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }
}

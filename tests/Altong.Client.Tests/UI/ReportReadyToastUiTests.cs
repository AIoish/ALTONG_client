using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ReportReadyToastUiTests
{
    [DataTestMethod]
    [DataRow(true, "AI 요약이 끝났어요")]
    [DataRow(false, "AI 요약을 만들지 못했어요")]
    public void Present_ShowsOutcome_NearTrayWithoutTakingFocus(bool succeeded, string title) => RunSta(() =>
    {
        var toast = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.Zero };
        try
        {
            toast.Present(succeeded);
            Pump();
            Assert.IsTrue(toast.IsVisible);
            Assert.IsFalse(toast.IsActive, "말풍선이 작업 중인 창의 포커스를 가져가면 안 됩니다.");
            Assert.AreEqual(title, ((TextBlock)toast.FindName("TitleText")).Text);
            var area = SystemParameters.WorkArea;
            Assert.AreEqual(SystemParameters.PrimaryScreenWidth / 2, toast.Left + toast.ActualWidth / 2, 1, "집중 제안 카드와 같은 화면 위 가운데에 떠야 합니다.");
            Assert.AreEqual(Math.Max(0, area.Top) + 10, toast.Top, 1, "작업 영역 위쪽에 붙어 나옵니다.");
            SavePreview(toast, succeeded ? "report-toast-success.png" : "report-toast-failure.png");
        }
        finally { toast.Close(); }
    });

    [TestMethod]
    public void OpenButton_RaisesOpenRequested_AndCloses() => RunSta(() =>
    {
        var toast = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.Zero };
        int opened = 0;
        toast.OpenRequested += (_, _) => opened++;
        toast.Present(succeeded: true);
        ((Button)toast.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => !toast.IsVisible);
        Assert.AreEqual(1, opened);
    });

    [TestMethod]
    public void OpenButton_PressedTwiceWhileFading_OpensOnce() => RunSta(() =>
    {
        var toast = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.Zero };
        int opened = 0;
        toast.OpenRequested += (_, _) => opened++;
        toast.Present(succeeded: true);
        var open = (Button)toast.FindName("OpenButton");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => !toast.IsVisible);
        Assert.AreEqual(1, opened, "사라지는 중에 다시 눌러도 리포트는 한 번만 열려야 합니다.");
    });

    [TestMethod]
    public void NewToast_ReplacesPrevious_InsteadOfStacking() => RunSta(() =>
    {
        var first = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.Zero };
        var second = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.Zero };
        var other = new Window { Width = 360, Height = 120, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            first.Present(succeeded: true);
            double originalTop = first.Top;
            other.Show();
            Altong.Client.Services.TopBanner.Place(other);
            Assert.IsTrue(other.Top > originalTop);
            second.Present(succeeded: false);
            Assert.AreEqual(originalTop, second.Top, 0.5, "이전 알림이 차지했던 자리에 표시해야 합니다.");
            var third = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.Zero };
            try
            {
                second.Dismiss();
                third.Present(succeeded: true);
                Assert.AreEqual(originalTop, third.Top, 0.5, "퇴장 중에 새로고침해도 위치가 내려가면 안 됩니다.");
            }
            finally { third.Close(); }
            Until(() => !first.IsVisible);
            Assert.IsFalse(second.IsVisible);
        }
        finally { first.Close(); second.Close(); other.Close(); }
    });

    [TestMethod]
    public void ClosesByItself_AfterTimeout() => RunSta(() =>
    {
        var toast = new ReportReadyToastWindow { AutoCloseAfter = TimeSpan.FromMilliseconds(200) };
        toast.Present(succeeded: true);
        Assert.IsTrue(toast.IsVisible);
        Until(() => !toast.IsVisible);
    });

    private static void SavePreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content;
        // 등장 애니메이션 중간이 아니라 다 나타난 모습을 찍는다.
        root.BeginAnimation(UIElement.OpacityProperty, null);
        root.Opacity = 1;
        if (root.RenderTransform is System.Windows.Media.TranslateTransform slide)
        {
            slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            slide.Y = 0;
        }
        root.UpdateLayout();
        int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width * 2, height * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        var backdrop = new System.Windows.Media.DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x6B, 0x80)), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }
        Assert.IsTrue(ready(), "말풍선 상태 전환 대기 시간 초과");
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

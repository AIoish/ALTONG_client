using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class AutoDndSuggestionUiTests
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [TestMethod]
    public void Present_ShowsAtTopCenterWithoutTakingFocus_AndDismissHides()
    {
        RunOnSta(() =>
        {
            var window = new AutoDndSuggestionWindow();
            try
            {
                window.Present(TimeSpan.FromSeconds(15));
                Pump(TimeSpan.FromMilliseconds(260));

                Assert.IsTrue(window.IsVisible);
                Assert.IsFalse(window.IsActive, "제안 카드가 작업 중인 창의 포커스를 가져가면 안 된다.");
                int style = GetWindowLong(new WindowInteropHelper(window).Handle, GWL_EXSTYLE);
                Assert.AreNotEqual(0, style & WS_EX_NOACTIVATE);
                Assert.AreNotEqual(0, style & WS_EX_TOOLWINDOW, "Alt+Tab 목록에 나오지 않는다.");
                Assert.IsTrue(window.Topmost);
                Assert.AreEqual(SystemParameters.PrimaryScreenWidth / 2, window.Left + window.ActualWidth / 2, 1.0);
                // 작업 영역 맨 위에서 조금 띄워 나온다. 상단 띠·작업표시줄이 위쪽을 예약하면 그 아래에 둔다.
                Assert.AreEqual(Math.Max(0, SystemParameters.WorkArea.Top) + 10, window.Top, 1.0, "작업 영역 상단에 붙어 나온다.");
                SavePreview(window, "autodnd-suggestion.png");

                window.Dismiss();
                Pump(TimeSpan.FromMilliseconds(300));
                Assert.IsFalse(window.IsVisible);

                // 같은 창을 다시 쓸 수 있다.
                window.Present(TimeSpan.FromSeconds(15));
                Pump(TimeSpan.FromMilliseconds(260));
                Assert.IsTrue(window.IsVisible);

                // [집중 시작] 직후에는 애니메이션 없이 즉시 숨는다 (이어지는 집중 켜기가 UI 스레드를 막아도 남지 않게).
                window.HideImmediately();
                Assert.IsFalse(window.IsVisible, "펌프 없이도 바로 숨어야 한다.");
                window.Present(TimeSpan.FromSeconds(15));
                Pump(TimeSpan.FromMilliseconds(260));
                Assert.IsTrue(window.IsVisible, "즉시 숨긴 뒤에도 다시 보여 줄 수 있다.");
            }
            finally { window.CloseForShutdown(); }
        });
    }

    [TestMethod]
    public void Buttons_RaiseAcceptedAndDeclined()
    {
        RunOnSta(() =>
        {
            var window = new AutoDndSuggestionWindow();
            try
            {
                int accepted = 0, declined = 0;
                window.Accepted += (_, _) => accepted++;
                window.Declined += (_, _) => declined++;
                window.Present(TimeSpan.FromSeconds(15));

                var accept = (Button)window.FindName("AcceptButton");
                var decline = (Button)window.FindName("DeclineButton");
                Assert.AreEqual("집중 시작", accept.Content);
                Assert.AreEqual("나중에", decline.Content);
                Assert.IsFalse(accept.Focusable, "버튼도 키보드 포커스를 가져가지 않는다.");

                // 같은 카드로 타이머 종료를 제안한다.
                window.Present(TimeSpan.FromSeconds(15), AutoDndPrompt.TimerFinished);
                Assert.AreEqual(AutoDndPrompt.TimerFinished, window.Prompt);
                Assert.AreEqual("집중 종료", accept.Content);
                Assert.AreEqual("계속하기", decline.Content);
                Assert.AreEqual("설정한 집중 시간이 끝났어요", ((TextBlock)window.FindName("TitleText")).Text);
                Pump(TimeSpan.FromMilliseconds(260));
                SavePreview(window, "autodnd-timer-finished.png");

                accept.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                decline.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.AreEqual(1, accepted);
                Assert.AreEqual(1, declined);
            }
            finally { window.CloseForShutdown(); }
        });
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SavePreview(Window window, string name)
    {
        // 시각 검증이 필요할 때만 렌더링한다. 기본 테스트는 파일을 만들지 않는다.
        var directory = Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        window.UpdateLayout();
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "WPF 테스트가 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

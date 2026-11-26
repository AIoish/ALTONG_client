using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SummaryBlendUiTests
{
    [TestMethod]
    public void Blend_ShowsSenderAndBody_AndCardsMoveTowardSummary() => RunSta(() =>
    {
        var now = DateTime.UtcNow;
        var notifications = new[]
        {
            new NotificationRecord("1", "Slack", "김팀장", "주간 기획 회의", "내일 오후 3시 회의실 A에서 진행합니다", now.AddMinutes(-9), false),
            new NotificationRecord("2", "KakaoTalk.exe", null, "개발팀 단톡방", "배포 끝나면 알려주세요", now.AddMinutes(-8), false),
            new NotificationRecord("3", "Discord", "민준", "general", "PR 리뷰 부탁해요", now.AddMinutes(-7), false),
            new NotificationRecord("4", "Outlook", "인사팀", "연말정산 안내", "12월 5일까지 제출", now.AddMinutes(-6), false),
            new NotificationRecord("5", "Slack", "박대리", "자료 공유", "기획서 v2 올렸습니다", now.AddMinutes(-5), false),
            new NotificationRecord("6", "KakaoTalk.exe", null, "엄마", "(내용을 읽지 못함)", now.AddMinutes(-4), false),
            new NotificationRecord("7", "Slack", "오래된", "오래된 알림", "안 보여야 함", now.AddMinutes(-30), false)
        };
        var blend = new SummaryBlendView();
        var host = new Window
        {
            Width = 560, Height = 300, Content = new Border { Padding = new Thickness(20), Child = blend },
            Background = (Brush)blend.FindResource("BlackPaper"), ShowActivated = false, WindowStyle = WindowStyle.None
        };
        try
        {
            blend.SetNotifications(notifications);
            Assert.AreEqual(SummaryBlendView.MaxChips, blend.ChipCount, "최신 알림 6개까지만 섞습니다.");
            var texts = blend.Chips.SelectMany(chip => Descendants(chip).OfType<TextBlock>()).Select(text => text.Text).ToArray();
            CollectionAssert.Contains(texts, "김팀장", "발신자를 보여 줍니다.");
            CollectionAssert.Contains(texts, "내일 오후 3시 회의실 A에서 진행합니다", "발신자만이 아니라 내용도 보여 줍니다.");
            CollectionAssert.DoesNotContain(texts, "(내용을 읽지 못함)", "읽지 못한 본문 대신 제목을 씁니다.");
            CollectionAssert.DoesNotContain(texts, "안 보여야 함");
            CollectionAssert.Contains(texts, "개발팀 단톡방", "카카오톡은 방 이름을 발신처로 씁니다.");
            CollectionAssert.DoesNotContain(texts, "KakaoTalk");

            host.Show();
            blend.Start();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, blend.IsAnimating);
            SavePreview(host, "summary-blend-start.png");
            var shown = blend.Chips.Where(chip => chip.Visibility == Visibility.Visible).ToArray();
            Assert.IsTrue(shown.Length is >= 2 and <= SummaryBlendView.MaxChips);
            for (int i = 0; i < shown.Length; i++)
                for (int j = i + 1; j < shown.Length; j++)
                    Assert.IsFalse(Bounds(shown[i]).IntersectsWith(Bounds(shown[j])), "시작 자리에서 카드가 겹치면 안 됩니다.");
            if (blend.IsAnimating)
            {
                var first = (TransformGroup)shown[0].RenderTransform;
                double startY = ((TranslateTransform)first.Children[1]).Y;
                Pump(TimeSpan.FromMilliseconds(1000));
                SavePreview(host, "summary-blend-moving.png");
                Assert.IsTrue(((TranslateTransform)first.Children[1]).Y > startY + 20, "첫 카드는 아래 요약 카드 쪽으로 내려가야 합니다.");
                Pump(TimeSpan.FromMilliseconds(1600));
                SavePreview(host, "summary-blend-late.png");
            }
            blend.Stop();
            Assert.IsFalse(blend.IsAnimating);
        }
        finally { host.Close(); }
    });

    private static Rect Bounds(FrameworkElement chip)
    {
        var move = (TranslateTransform)((TransformGroup)chip.RenderTransform).Children[1];
        var rect = new Rect(move.X, move.Y, chip.DesiredSize.Width, chip.DesiredSize.Height);
        rect.Inflate(-1, -1);
        return rect;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
        if (root is Decorator { Child: { } decorated } && VisualTreeHelper.GetChildrenCount(root) == 0)
        {
            yield return decorated;
            foreach (var descendant in Descendants(decorated)) yield return descendant;
        }
        if (root is System.Windows.Controls.Panel panel && VisualTreeHelper.GetChildrenCount(root) == 0)
            foreach (UIElement child in panel.Children)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
    }

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

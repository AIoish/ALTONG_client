using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Altong.Client.Tests;

[TestClass]
public class OcrTests
{
    [TestMethod]
    public async Task TestWindowsOcrAvailability()
    {
        var ocr = OcrEngine.TryCreateFromUserProfileLanguages();
        Assert.IsNotNull(ocr, "Windows.Media.Ocr.OcrEngine must be available on Windows 10/11.");

        // Create a test bitmap with text (Using GenericSansSerif and universal ASCII text for CI compatibility)
        using var bmp = new Bitmap(350, 100);
        // WPF UI 테스트가 먼저 실행되어 프로세스 DPI가 바뀌어도 글자가 잘리지 않도록 고정한다.
        bmp.SetResolution(96, 96);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(FontFamily.GenericSansSerif, 14, FontStyle.Bold);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString("AltongSender", font, brush, new PointF(10, 10));
            g.DrawString("Important Notification Message", font, brush, new PointF(10, 45));
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(ras))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await ocr.RecognizeAsync(softwareBitmap);
        Assert.IsTrue(result.Lines.Count >= 2, $"OCR should recognize multiple lines. Recognized: {result.Lines.Count}");
        string fullText = string.Join(" ", result.Lines.Select(l => l.Text));
        StringAssert.Contains(fullText, "Notification");
    }

    [TestMethod]
    public async Task TestKoreanSmallTextWithUpscaling()
    {
        var koLang = new Windows.Globalization.Language("ko-KR");
        if (!OcrEngine.IsLanguageSupported(koLang))
        {
            Assert.Inconclusive("Korean OCR language pack (ko-KR) is not installed on this system.");
            return;
        }

        var ocr = OcrEngine.TryCreateFromLanguage(koLang);
        if (ocr == null)
        {
            Assert.Inconclusive("Korean OCR is not installed or available on this system.");
            return;
        }

        // Small 10pt text mimicking Kakao notification (280x90)
        using var bmp = new Bitmap(280, 90);
        bmp.SetResolution(96, 96);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Malgun Gothic", 10, FontStyle.Regular);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString("스푼 흰나방", font, brush, new PointF(10, 8));
            g.DrawString("긁적이는 춘식이", font, brush, new PointF(10, 30));
            g.DrawString("오늘", font, brush, new PointF(10, 56));
        }

        // 2.5x Bicubic upscale
        const float upscale = 2.5f;
        int scaledW = (int)Math.Round(bmp.Width * upscale);
        int scaledH = (int)Math.Round(bmp.Height * upscale);
        using var scaledBmp = new Bitmap(scaledW, scaledH);
        using (var g = Graphics.FromImage(scaledBmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(bmp, new Rectangle(0, 0, scaledW, scaledH), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
        }

        using var ms = new MemoryStream();
        scaledBmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(ras))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await ocr.RecognizeAsync(softwareBitmap);
        string fullText = string.Join(" ", result.Lines.Select(l => l.Text));

        // Ensure "오늘" was recognized successfully
        Assert.IsTrue(fullText.Contains("오늘"), $"OCR should recognize '오늘'. Recognized lines: '{fullText}'");
    }
}

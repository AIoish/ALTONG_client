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
}

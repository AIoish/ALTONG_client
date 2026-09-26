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

        // Create a test bitmap with text "홍길동\n안녕하세요 반갑습니다"
        using var bmp = new Bitmap(300, 80);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Malgun Gothic", 12);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString("홍길동", font, brush, new PointF(10, 10));
            g.DrawString("안녕하세요 반갑습니다", font, brush, new PointF(10, 35));
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
        Assert.IsTrue(result.Lines.Count >= 2, "OCR should recognize multiple lines.");
        string fullText = string.Join(" ", result.Lines.Select(l => l.Text));
        StringAssert.Contains(fullText, "안녕하세요");
    }
}

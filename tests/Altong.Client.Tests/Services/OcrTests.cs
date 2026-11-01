using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Altong.Client.Services.Notifications;

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
            g.DrawString("알통 알림", font, brush, new PointF(10, 8));
            g.DrawString("집중 모드 테스트", font, brush, new PointF(10, 30));
            g.DrawString("오늘 회의", font, brush, new PointF(10, 50));
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

    [TestMethod]
    public void TestPaddleOcrAvailability()
    {
        var recognizer = PaddleOcrLineRecognizer.Instance;
        if (!recognizer.IsAvailable)
        {
            Assert.Inconclusive("PaddleOCR ONNX model or dict not found. Skipping on-device inference test.");
            return;
        }

        Assert.IsTrue(recognizer.IsAvailable, "PaddleOcrLineRecognizer must be available when models/ocr files exist.");
    }

    [TestMethod]
    public void TestIsolatedKoreanJamoRecognition()
    {
        var recognizer = PaddleOcrLineRecognizer.Instance;
        if (!recognizer.IsAvailable)
        {
            Assert.Inconclusive("PaddleOCR ONNX model or dict not found.");
            return;
        }

        var cases = new (string Input, string Expected)[]
        {
            ("ㄷㄷㄷ", "ㄷㄷㄷ"),
            ("ㅋㅋㅋ", "ㅋㅋㅋ"),
            ("ㅠㅠ", "ㅠㅠ"),
            ("ㅡㅡ", "ㅡㅡ")
        };

        foreach (var (input, expected) in cases)
        {
            using var bmp = RenderTextLineBitmap(input, fontSize: 18);
            string? recognized = recognizer.RecognizeLine(bmp);
            Assert.IsNotNull(recognized, $"Recognition result for '{input}' must not be null.");
            Assert.AreEqual(expected, recognized.Trim(), $"Input '{input}' should be recognized as '{expected}'.");
        }
    }

    [TestMethod]
    public void TestMixedKoreanSentenceRecognition()
    {
        var recognizer = PaddleOcrLineRecognizer.Instance;
        if (!recognizer.IsAvailable)
        {
            Assert.Inconclusive("PaddleOCR ONNX model or dict not found.");
            return;
        }

        var cases = new[]
        {
            ("ㅋㅋ 진짜 ㄷㄷ하네 ㅠㅠ", "진짜"),
            ("내일 14:00 회의", "14:00"),
            ("알통 데스크톱 알림", "알통")
        };

        foreach (var (input, keyword) in cases)
        {
            using var bmp = RenderTextLineBitmap(input, fontSize: 18);
            string? recognized = recognizer.RecognizeLine(bmp);

            Assert.IsNotNull(recognized, $"Recognition result for '{input}' must not be null.");
            StringAssert.Contains(recognized, keyword, $"Recognized text '{recognized}' should contain '{keyword}'.");
        }
    }

    [TestMethod]
    public void TestJamoNormalizationAndConfusionMappings()
    {
        // 1. Conjoining Hangul Jamo (U+1100..U+11FF) -> Compatibility Jamo (U+3131..U+318E)
        string conjoiningDiget = "\u1103\u1103\u1103";
        string normalizedDiget = PaddleOcrLineRecognizer.NormalizeJamo(conjoiningDiget);
        Assert.AreEqual("ㄷㄷㄷ", normalizedDiget);

        string conjoiningKieuk = "\u110F\u110F\u110F";
        string normalizedKieuk = PaddleOcrLineRecognizer.NormalizeJamo(conjoiningKieuk);
        Assert.AreEqual("ㅋㅋㅋ", normalizedKieuk);

        // 2. OCR glyph visual confusion mappings
        Assert.AreEqual("ㄷㄷㄷ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("CCC"));
        Assert.AreEqual("ㄷㄷㄷ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("ㄷCC"));
        Assert.AreEqual("ㄷㄷㄷ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("ㄷㄷC"));
        Assert.AreEqual("ㄷㄷㄷ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("ccc"));
        Assert.AreEqual("ㅠㅠ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("TT"));
        Assert.AreEqual("ㅠㅠ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("TTTT"));
        Assert.AreEqual("ㅡㅡ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("--"));
        Assert.AreEqual("ㅡㅡ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("-—"));
        Assert.AreEqual("ㅡㅡ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("__"));
        Assert.AreEqual("ㅋㅋㅋ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("==="));
        Assert.AreEqual("ㅋㅋㅋ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("ㅋ=="));

        // 3. Regular non-confusable text & single English/operator characters should remain strictly UNCHANGED
        Assert.AreEqual("C", PaddleOcrLineRecognizer.NormalizeJamoConfusion("C"));
        Assert.AreEqual("c", PaddleOcrLineRecognizer.NormalizeJamoConfusion("c"));
        Assert.AreEqual("CC", PaddleOcrLineRecognizer.NormalizeJamoConfusion("CC"));
        Assert.AreEqual("T", PaddleOcrLineRecognizer.NormalizeJamoConfusion("T"));
        Assert.AreEqual("-", PaddleOcrLineRecognizer.NormalizeJamoConfusion("-"));
        Assert.AreEqual("=", PaddleOcrLineRecognizer.NormalizeJamoConfusion("="));
        Assert.AreEqual("==", PaddleOcrLineRecognizer.NormalizeJamoConfusion("=="));
        Assert.AreEqual("오늘 회의 14:00", PaddleOcrLineRecognizer.NormalizeJamoConfusion("오늘 회의 14:00"));
        Assert.AreEqual("좋은데요 ㅎㅎ", PaddleOcrLineRecognizer.NormalizeJamoConfusion("좋은데요 ㅎㅎ"));
    }

    [TestMethod]
    public void TestDarkModeRecognition()
    {
        var recognizer = PaddleOcrLineRecognizer.Instance;
        if (!recognizer.IsAvailable)
        {
            Assert.Inconclusive("PaddleOCR ONNX model or dict not found.");
            return;
        }

        // Dark background (#252525) with bright white text (#FFFFFF) mimicking Kakao dark theme
        var cases = new (string Input, string Expected)[]
        {
            ("알통 데스크톱", "알통"),
            ("ㄷㄷㄷ", "ㄷㄷㄷ"),
            ("ㅋㅋㅋ", "ㅋㅋㅋ")
        };

        foreach (var (input, expected) in cases)
        {
            using var bmp = RenderDarkModeTextLineBitmap(input, fontSize: 18);
            string? recognized = recognizer.RecognizeLine(bmp);

            Assert.IsNotNull(recognized, $"Recognition result for dark mode '{input}' must not be null.");
            StringAssert.Contains(recognized, expected, $"Dark mode recognized text '{recognized}' should contain '{expected}'.");
        }
    }

    [TestMethod]
    public void TestDetectBodyLineBand_LightAndDarkMode()
    {
        // 1. Light mode text band detection
        using var lightBmp = new Bitmap(300, 150, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(lightBmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Malgun Gothic", 12);
            using var brush = new SolidBrush(Color.Black);
            g.DrawString("ㅡㅡ", font, brush, new PointF(30, 80));
        }

        var lightBand = LiveKakaoWindowOperator.DetectBodyLineBand(lightBmp, 10, 60, 250, 140);
        Assert.IsTrue(lightBand.HasValue, "DetectBodyLineBand should detect line in light mode.");
        Assert.IsTrue(lightBand.Value.Y >= 60 && lightBand.Value.Y <= 90, $"Band Y ({lightBand.Value.Y}) should match drawn text line.");

        // 2. Dark mode text band detection
        using var darkBmp = new Bitmap(300, 150, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(darkBmp))
        {
            g.Clear(Color.FromArgb(37, 37, 37));
            using var font = new Font("Malgun Gothic", 12);
            using var brush = new SolidBrush(Color.White);
            g.DrawString("ㅡㅡ", font, brush, new PointF(30, 80));
        }

        var darkBand = LiveKakaoWindowOperator.DetectBodyLineBand(darkBmp, 10, 60, 250, 140);
        Assert.IsTrue(darkBand.HasValue, "DetectBodyLineBand should detect line in dark mode.");
        Assert.IsTrue(darkBand.Value.Y >= 60 && darkBand.Value.Y <= 90, $"Dark mode band Y ({darkBand.Value.Y}) should match drawn text line.");

        // 3. Blank bitmap should return null
        using var blankBmp = new Bitmap(300, 150, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(blankBmp))
        {
            g.Clear(Color.White);
        }

        var blankBand = LiveKakaoWindowOperator.DetectBodyLineBand(blankBmp, 10, 60, 250, 140);
        Assert.IsFalse(blankBand.HasValue, "DetectBodyLineBand must return null on blank canvas.");
    }

    [TestMethod]
    public void TestGracefulFallbackWhenModelAbsent()
    {
        using var missingRecognizer = new PaddleOcrLineRecognizer("non_existent_model.onnx", "non_existent_dict.txt");
        Assert.IsFalse(missingRecognizer.IsAvailable, "Recognizer must report IsAvailable=false when files are missing.");

        using var bmp = new Bitmap(100, 30);
        string? result = missingRecognizer.RecognizeLine(bmp);
        Assert.IsNull(result, "RecognizeLine must return null when model is absent.");
    }

    private static Bitmap RenderTextLineBitmap(string text, float fontSize = 18)
    {
        using var dummy = new Bitmap(10, 10);
        using var gDummy = Graphics.FromImage(dummy);
        using var font = new Font("Malgun Gothic", fontSize, FontStyle.Regular);
        var size = gDummy.MeasureString(text, font);

        int pad = 4;
        int w = Math.Max(20, (int)Math.Ceiling(size.Width) + pad * 2);
        int h = Math.Max(20, (int)Math.Ceiling(size.Height) + pad * 2);

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var brush = new SolidBrush(Color.Black);
        g.DrawString(text, font, brush, new PointF(pad, pad));

        return bmp;
    }

    private static Bitmap RenderDarkModeTextLineBitmap(string text, float fontSize = 18)
    {
        using var dummy = new Bitmap(10, 10);
        using var gDummy = Graphics.FromImage(dummy);
        using var font = new Font("Malgun Gothic", fontSize, FontStyle.Regular);
        var size = gDummy.MeasureString(text, font);

        int pad = 8;
        int w = Math.Max(20, (int)Math.Ceiling(size.Width) + pad * 2);
        int h = Math.Max(20, (int)Math.Ceiling(size.Height) + pad * 2);

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(37, 37, 37)); // Kakao dark theme background
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var brush = new SolidBrush(Color.White);
        g.DrawString(text, font, brush, new PointF(pad, pad));

        return bmp;
    }
}


using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// korean_PP-OCRv5_mobile_rec ONNX 기반 온디바이스 라인 OCR 인식기.
/// Windows.Media.Ocr의 한글 자모(ㄷㄷㄷ, ㅋㅋㅋ, ㅠㅠ, ㅡㅡ) 인식 한계를 보완하고 초경량(~5ms)으로 추론합니다.
/// </summary>
public sealed class PaddleOcrLineRecognizer : IDisposable
{
    private static readonly Lazy<PaddleOcrLineRecognizer> _instance = new(() => new PaddleOcrLineRecognizer());
    public static PaddleOcrLineRecognizer Instance => _instance.Value;

    private readonly InferenceSession? _session;
    private readonly string[] _dict = Array.Empty<string>();
    private readonly object _inferenceLock = new();
    private bool _disposed;

    public bool IsAvailable => _session != null && _dict.Length > 0 && !_disposed;

    // 초성(U+1100..U+1112) -> 호환 자모(U+3131..U+314E) 매핑 테이블 (19개)
    private static readonly char[] ChoseongToCompat =
    [
        '\u3131', '\u3132', '\u3134', '\u3137', '\u3138', '\u3139', '\u3141', '\u3142',
        '\u3143', '\u3145', '\u3146', '\u3147', '\u3148', '\u3149', '\u314A', '\u314B',
        '\u314C', '\u314D', '\u314E'
    ];

    // 종성(U+11A8..U+11C2) -> 호환 자모 매핑 테이블 (27개)
    private static readonly char[] JongseongToCompat =
    [
        '\u3131', '\u3132', '\u3133', '\u3134', '\u3135', '\u3136', '\u3137', '\u3139',
        '\u313A', '\u313B', '\u313C', '\u313D', '\u313E', '\u313F', '\u3140', '\u3141',
        '\u3142', '\u3144', '\u3145', '\u3146', '\u3147', '\u3148', '\u314A', '\u314B',
        '\u314C', '\u314D', '\u314E'
    ];

    public PaddleOcrLineRecognizer(string? modelPath = null, string? dictPath = null)
    {
        try
        {
            modelPath ??= ResolveModelPath("korean_PP-OCRv5_mobile_rec.onnx");
            dictPath ??= ResolveModelPath("ppocrv5_korean_dict.txt");

            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath) ||
                string.IsNullOrEmpty(dictPath) || !File.Exists(dictPath))
            {
                AppLogger.Warn($"[PaddleOCR] 모델 또는 사전을 찾을 수 없어 Graceful Fallback 모드로 동작합니다. (Model: {modelPath ?? "null"}, Dict: {dictPath ?? "null"})");
                return;
            }

            var lines = File.ReadAllLines(dictPath, Encoding.UTF8);
            _dict = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                _dict[i] = NormalizeJamo(lines[i].TrimEnd('\r', '\n'));
            }

            var sessionOptions = new SessionOptions
            {
                IntraOpNumThreads = 2,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            };

            _session = new InferenceSession(modelPath, sessionOptions);
            AppLogger.Info($"[PaddleOCR] PP-OCRv5 모델 로드 완료: {Path.GetFileName(modelPath)} (사전 {lines.Length}자)");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[PaddleOCR] 모델 초기화 실패, Graceful Fallback으로 전환: {ex.Message}");
            _session = null;
            _dict = Array.Empty<string>();
        }
    }

    /// <summary>
    /// 라인 비트맵 영역에서 텍스트를 인식합니다.
    /// </summary>
    /// <param name="source">전체 또는 라인 비트맵</param>
    /// <param name="cropRect">크롭할 영역 (null이면 전체 비트맵 사용)</param>
    /// <returns>인식된 텍스트 (실패 시 null 또는 빈 문자열)</returns>
    public string? RecognizeLine(Bitmap source, Rectangle? cropRect = null)
    {
        var result = RecognizeLineWithConfidence(source, cropRect);
        return result?.Text;
    }

    /// <summary>
    /// 라인 비트맵 영역에서 텍스트 및 평균 신뢰도를 인식합니다.
    /// </summary>
    public OcrLineResult? RecognizeLineWithConfidence(Bitmap source, Rectangle? cropRect = null)
    {
        if (!IsAvailable || _session == null)
        {
            return null;
        }

        Rectangle bounds;
        if (cropRect.HasValue)
        {
            const int margin = 2;
            int x = Math.Max(0, cropRect.Value.X - margin);
            int y = Math.Max(0, cropRect.Value.Y - margin);
            int w = Math.Min(source.Width - x, cropRect.Value.Width + margin * 2);
            int h = Math.Min(source.Height - y, cropRect.Value.Height + margin * 2);
            if (w <= 0 || h <= 0) return null;
            bounds = new Rectangle(x, y, w, h);
        }
        else
        {
            bounds = new Rectangle(0, 0, source.Width, source.Height);
        }

        bounds = GetTightContentBounds(source, bounds, out bool isDarkBackground);

        const int targetH = 48;
        float ratio = (float)bounds.Width / bounds.Height;
        int resizedW = Math.Max(1, (int)Math.Ceiling(targetH * ratio));
        // PP-OCRv5 supports dynamic width (-1, 3, 48, -1).
        // Enforce minimum width of 32 to guarantee valid convolutional feature map strides.
        int targetW = Math.Max(32, resizedW);

        using var prepBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var gPrep = Graphics.FromImage(prepBmp))
        {
            gPrep.DrawImage(source, new Rectangle(0, 0, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        }

        if (isDarkBackground)
        {
            var pData = prepBmp.LockBits(
                new Rectangle(0, 0, bounds.Width, bounds.Height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppRgb);
            try
            {
                int pStride = Math.Abs(pData.Stride);
                int pBytes = pStride * bounds.Height;
                byte[] pBuffer = new byte[pBytes];
                Marshal.Copy(pData.Scan0, pBuffer, 0, pBytes);
                for (int i = 0; i < pBytes; i += 4)
                {
                    pBuffer[i] = (byte)(255 - pBuffer[i]);
                    pBuffer[i + 1] = (byte)(255 - pBuffer[i + 1]);
                    pBuffer[i + 2] = (byte)(255 - pBuffer[i + 2]);
                }
                Marshal.Copy(pBuffer, 0, pData.Scan0, pBytes);
            }
            finally
            {
                prepBmp.UnlockBits(pData);
            }
        }

        using var resizedBmp = new Bitmap(targetW, targetH, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(resizedBmp))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(prepBmp, new Rectangle(0, 0, resizedW, targetH), 0, 0, bounds.Width, bounds.Height, GraphicsUnit.Pixel);
        }

        // PaddleOCR 표준 전처리: H=48, W=targetW, BGR 정규화 (x/255 - 0.5)/0.5, 우측 여백은 흰색(1.0f) 유지
        var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
        var bmpData = resizedBmp.LockBits(
            new Rectangle(0, 0, targetW, targetH),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);

        try
        {
            int stride = Math.Abs(bmpData.Stride);
            byte[] rgbValues = new byte[stride * targetH];
            Marshal.Copy(bmpData.Scan0, rgbValues, 0, rgbValues.Length);

            for (int y = 0; y < targetH; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < targetW; x++)
                {
                    int pxOffset = rowOffset + (x * 4);
                    byte b = rgbValues[pxOffset];
                    byte gr = rgbValues[pxOffset + 1];
                    byte r = rgbValues[pxOffset + 2];

                    tensor[0, 0, y, x] = (b / 255.0f - 0.5f) / 0.5f;
                    tensor[0, 1, y, x] = (gr / 255.0f - 0.5f) / 0.5f;
                    tensor[0, 2, y, x] = (r / 255.0f - 0.5f) / 0.5f;
                }
            }
        }
        finally
        {
            resizedBmp.UnlockBits(bmpData);
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor("x", tensor) };
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs;
        lock (_inferenceLock)
        {
            outputs = _session.Run(inputs);
        }

        using (outputs)
        {
            var outputTensor = outputs.First().AsTensor<float>();
            int seqLen = outputTensor.Dimensions[1];
            int numClasses = outputTensor.Dimensions[2];

            var sb = new StringBuilder();
            var confidences = new List<float>();
            int prevIdx = 0;

            for (int t = 0; t < seqLen; t++)
            {
                int maxIdx = 0;
                float maxScore = float.MinValue;
                for (int c = 0; c < numClasses; c++)
                {
                    float val = outputTensor[0, t, c];
                    if (val > maxScore)
                    {
                        maxScore = val;
                        maxIdx = c;
                    }
                }

                if (maxIdx != 0 && maxIdx != prevIdx)
                {
                    if (maxIdx == numClasses - 1)
                    {
                        sb.Append(' ');
                        confidences.Add(maxScore);
                    }
                    else if (maxIdx - 1 < _dict.Length)
                    {
                        sb.Append(_dict[maxIdx - 1]);
                        confidences.Add(maxScore);
                    }
                }
                prevIdx = maxIdx;
            }

            string raw = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new OcrLineResult(string.Empty, 0.0f);
            }

            float avgConf = confidences.Count > 0 ? confidences.Average() : 0.0f;
            string normalized = NormalizeJamo(raw);
            string cleaned = NormalizeJamoConfusion(normalized);

            return new OcrLineResult(cleaned, avgConf);
        }
    }

    /// <summary>
    /// 조합형 유니코드 한글 자모(U+1100..U+11FF)를 호환용 자모(U+3131..U+318E)로 통일하고 NFKC 정규화합니다.
    /// </summary>
    public static string NormalizeJamo(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string nfkc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (char c in nfkc)
        {
            if (c >= 0x1100 && c <= 0x1112)
            {
                sb.Append(ChoseongToCompat[c - 0x1100]);
            }
            else if (c >= 0x1161 && c <= 0x1175)
            {
                sb.Append((char)(0x314F + (c - 0x1161)));
            }
            else if (c >= 0x11A8 && c <= 0x11C2)
            {
                sb.Append(JongseongToCompat[c - 0x11A8]);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// OCR 폰트 렌더링 글리프 유사성으로 인해 발생하는 단독 자모 영문/기호 혼동을 보정합니다.
    /// - ㄷㄷㄷ -> CCC / ㄷCC / CcC (길이 3 이상의 순수 C 또는 'ㄷ' 혼합 C)
    /// - ㅠㅠ -> TT / TTTT (2개 이상의 T)
    /// - ㅡㅡ -> -- / __ / — (2개 이상의 대시/하이픈 또는 'ㅡ' 혼합 대시)
    /// - ㅋㅋㅋ -> === (3개 이상의 등호 또는 'ㅋ' 혼합 등호)
    /// ※ 주의: 단일 "C", "T", "-", "=" 및 "==" 등 정상 영문/기호는 절대 오변환하지 않습니다.
    /// </summary>
    public static string NormalizeJamoConfusion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string trimmed = text.Trim();

        // 1. [ㄷㄷㄷ 혼동] C/c 및 ㄷ로만 이루어진 문자열
        // - 'ㄷ'와 'C'가 섞여 있는 경우 (예: "ㄷCC", "Cㄷ"): 명백한 OCR 글리프 혼동이므로 치환
        // - 순수 C/c만 있는 경우: 최소 3글자 이상 (예: "CCC", "CCCC")일 때만 메신저 자모 ㄷㄷㄷ로 인정 (단일 "C", "CC"는 영문 유지)
        bool allC = true;
        bool hasDiget = false;
        int cCount = 0;
        foreach (char ch in trimmed)
        {
            if (ch == 'ㄷ') hasDiget = true;
            else if (ch == 'C' || ch == 'c') cCount++;
            else if (ch != ' ')
            {
                allC = false;
                break;
            }
        }
        if (allC && cCount > 0 && (hasDiget || cCount >= 3))
        {
            return new string(text.Select(c => (c == 'C' || c == 'c') ? 'ㄷ' : c).ToArray());
        }

        // 2. [ㅠㅠ 혼동] T/t 및 ㅠ로만 이루어진 문자열
        // - 'T'가 2개 이상이거나(TT -> ㅠㅠ), 'ㅠ'와 'T'가 섞인 경우에만 치환 (단일 "T"는 MBTI/영문 유지)
        bool allT = true;
        bool hasYu = false;
        int tCount = 0;
        foreach (char ch in trimmed)
        {
            if (ch == 'ㅠ') hasYu = true;
            else if (ch == 'T' || ch == 't') tCount++;
            else if (ch != ' ')
            {
                allT = false;
                break;
            }
        }
        if (allT && (tCount >= 2 || (hasYu && tCount > 0)))
        {
            int total = tCount + (hasYu ? trimmed.Count(c => c == 'ㅠ') : 0);
            int yuCount = total switch
            {
                <= 1 => 1,
                2 => 2, // "TT" -> "ㅠㅠ" (한국어 메신저 눈물 표시)
                3 => 2,
                _ => total / 2 // "TTTT" (2.5배 업스케일링 시 ㅠ당 2개 T 검출) -> "ㅠㅠ"
            };
            return new string('ㅠ', Math.Max(1, yuCount));
        }

        // 3. [ㅡㅡ 혼동] 하이픈(-), 언더바(_), 대시(—, –, ―), 마이너스(기호 −), ㅡ로만 이루어진 문자열
        // - 2개 이상이거나, 'ㅡ'와 대시가 섞인 경우에만 치환 (단일 "-"는 글머리기호/영문 유지)
        bool allHyphen = true;
        bool hasEu = false;
        int dashCount = 0;
        foreach (char ch in trimmed)
        {
            if (ch == 'ㅡ') hasEu = true;
            else if (IsDashOrHyphen(ch)) dashCount++;
            else if (ch != ' ')
            {
                allHyphen = false;
                break;
            }
        }
        if (allHyphen && (dashCount >= 2 || (hasEu && dashCount > 0)))
        {
            int total = dashCount + (hasEu ? trimmed.Count(c => c == 'ㅡ') : 0);
            return new string('ㅡ', Math.Max(1, total));
        }

        // 4. [ㅋㅋㅋ 혼동] Windows.Media.Ocr 등에서 ㅋ의 3개 가로 획이 등호(=)로 치환된 경우
        // - 등호가 3개 이상이거나(=== -> ㅋㅋㅋ), 'ㅋ'와 '='가 섞인 경우에만 치환 (단일 "=", "=="는 수식/연산자 유지)
        bool allEquals = true;
        bool hasKieuk = false;
        int eqCount = 0;
        foreach (char ch in trimmed)
        {
            if (ch == 'ㅋ') hasKieuk = true;
            else if (ch == '=') eqCount++;
            else if (ch != ' ')
            {
                allEquals = false;
                break;
            }
        }
        if (allEquals && (eqCount >= 3 || (hasKieuk && eqCount > 0)))
        {
            int count = eqCount + (hasKieuk ? trimmed.Count(c => c == 'ㅋ') : 0);
            return new string('ㅋ', Math.Max(1, count));
        }

        return text;
    }

    private static bool IsDashOrHyphen(char ch)
    {
        return ch == '-' || ch == '_' || ch == '—' || ch == '–' || ch == '−' || ch == '―' || ch == '─' || ch == '━';
    }

    private static Rectangle GetTightContentBounds(Bitmap bmp, Rectangle rect, out bool isDarkBackground)
    {
        isDarkBackground = false;
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int w = Math.Min(bmp.Width - x, rect.Width);
        int h = Math.Min(bmp.Height - y, rect.Height);
        if (w <= 0 || h <= 0) return rect;

        try
        {
            var bmpData = bmp.LockBits(
                new Rectangle(x, y, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppRgb);

            try
            {
                int stride = Math.Abs(bmpData.Stride);
                byte[] buffer = new byte[stride * h];
                Marshal.Copy(bmpData.Scan0, buffer, 0, buffer.Length);

                // 4개 모서리 픽셀 휘도 샘플링을 통한 배경 밝기 판별
                int c0 = (buffer[0] * 114 + buffer[1] * 587 + buffer[2] * 299) / 1000;
                int c1 = (buffer[(w - 1) * 4] * 114 + buffer[(w - 1) * 4 + 1] * 587 + buffer[(w - 1) * 4 + 2] * 299) / 1000;
                int c2 = (buffer[(h - 1) * stride] * 114 + buffer[(h - 1) * stride + 1] * 587 + buffer[(h - 1) * stride + 2] * 299) / 1000;
                int c3 = (buffer[(h - 1) * stride + (w - 1) * 4] * 114 + buffer[(h - 1) * stride + (w - 1) * 4 + 1] * 587 + buffer[(h - 1) * stride + (w - 1) * 4 + 2] * 299) / 1000;
                isDarkBackground = (c0 + c1 + c2 + c3) / 4 < 120;

                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;

                for (int r = 0; r < h; r++)
                {
                    int rowOffset = r * stride;
                    for (int c = 0; c < w; c++)
                    {
                        int px = rowOffset + c * 4;
                        byte blue = buffer[px];
                        byte green = buffer[px + 1];
                        byte red = buffer[px + 2];

                        // Text stroke pixel
                        bool isStroke = isDarkBackground
                            ? (red > 110 || green > 110 || blue > 110)
                            : (red < 210 || green < 210 || blue < 210);

                        if (isStroke)
                        {
                            if (c < minX) minX = c;
                            if (c > maxX) maxX = c;
                            if (r < minY) minY = r;
                            if (r > maxY) maxY = r;
                        }
                    }
                }

                if (minX <= maxX && minY <= maxY)
                {
                    const int padX = 8;
                    const int padY = 3;
                    int contentW = maxX - minX + 1;
                    int contentH = maxY - minY + 1;

                    int tightX = Math.Max(0, x + minX - padX);
                    int tightW = Math.Min(bmp.Width - tightX, contentW + padX * 2);

                    // Ensure minimum line height so flat strokes (e.g. ㅡ, -, _) don't get vertically
                    // over-stretched when resized to targetH=48.
                    const int minH = 22;
                    int desiredH = Math.Max(minH, contentH + padY * 2);
                    int tightH = Math.Min(bmp.Height, desiredH);

                    int centerY = y + (minY + maxY) / 2;
                    int tightY = Math.Max(0, centerY - tightH / 2);
                    if (tightY + tightH > bmp.Height)
                    {
                        tightY = Math.Max(0, bmp.Height - tightH);
                        tightH = bmp.Height - tightY;
                    }

                    return new Rectangle(tightX, tightY, tightW, tightH);
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }
        catch
        {
            // Fallback to original rectangle if format cannot be locked as 32bppRgb
        }

        return rect;
    }

    public static string? ResolveModelPath(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 7; i++)
        {
            if (string.IsNullOrEmpty(dir)) break;
            string candidate = Path.Combine(dir, "models", "ocr", "ppocr-v5-ko", fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}

public sealed record OcrLineResult(string Text, float Confidence);

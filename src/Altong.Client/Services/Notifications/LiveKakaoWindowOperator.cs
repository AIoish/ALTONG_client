using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 실제 Windows OS 상에서 Win32 API 및 UI Automation을 통해
/// 카카오톡 알림 팝업 창을 식별, 제어(숨김/표시/닫기), 텍스트 추출하는 구현체.
/// </summary>
public sealed class LiveKakaoWindowOperator : IKakaoWindowOperator
{
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint WmClose = 0x0010;
    private const int GwlExStyle = -20;
    private const int WsExTopMost = 0x00000008;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsThickFrame = 0x00040000;
    private const uint WsPopup = 0x80000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoActivate = 0x0010;
    private const int WsChild = 0x40000000;
    private const uint WmSysCommand = 0x0112;
    private const nint ScClose = (nint)0xF060;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);
    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindow(nint hWnd, uint uCmd);
    private const uint GwOwner = 4;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    private static float GetWindowDpiScale(nint hWnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hWnd);
            if (dpi > 0)
            {
                return dpi / 96.0f;
            }
        }
        catch { }

        return 1.0f;
    }

    public bool IsNotificationWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        // 1. 클래스 이름 초고속(1ns) 검사 (카카오톡 고유 윈도우 클래스 EVA_Window 계열)
        // OS 내 수백 개의 무관한 창(Chrome, Explorer 등)을 0.1마이크로초 만에 즉시 배제하여 CPU 병목 방지
        var classBuffer = new StringBuilder(64);
        GetClassName(hWnd, classBuffer, classBuffer.Capacity);
        string className = classBuffer.ToString();

        if (!className.StartsWith("EVA_Window", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. 프로세스 이름이 KakaoTalk인지 검증
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!process.ProcessName.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        var titleBuffer = new StringBuilder(256);
        GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
        string title = titleBuffer.ToString().Trim();

        // 3. 카카오톡 메인 윈도우 절대 보호 (타이틀이 '카카오톡'인 메인 창은 어떠한 경우에도 알림 팝업으로 오인하지 않음)
        if (title.Equals("카카오톡", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 4. 자식 윈도우(WS_CHILD = 0x40000000) 절대 배제: 카카오톡 메인 창 내부 뷰(MoreView, ChatRoomListView 등) 파괴 원천 방지
        int style = GetWindowLong(hWnd, GwlStyle);
        if ((style & WsChild) != 0)
        {
            return false;
        }

        // 5. 부모 창이 존재하는 자식 윈도우 배제 (알림 토스트는 독립적인 최상위 윈도우)
        if (GetParent(hWnd) != nint.Zero)
        {
            return false;
        }

        // 6. 최상위 루트 윈도우가 자신이 아닌 경우 배제
        nint root = GetAncestor(hWnd, GaRoot);
        if (root != nint.Zero && root != hWnd)
        {
            return false;
        }

        // 7. 대화방(채팅창) 및 일반 크기 조절 윈도우 배제:
        // 카카오톡 대화방은 크기 조절 테두리(WS_THICKFRAME) 및 최소화/최대화 버튼(WS_MINIMIZEBOX/WS_MAXIMIZEBOX)을 가짐
        if ((style & WsThickFrame) != 0 || (style & WsMinimizeBox) != 0 || (style & WsMaximizeBox) != 0)
        {
            return false;
        }

        // 8. 카카오톡 알림 토스트는 반드시 팝업 윈도우(WS_POPUP)여야 함
        if ((style & unchecked((int)WsPopup)) == 0)
        {
            return false;
        }

        // 9. 소유자(Owner)가 메인 "카카오톡" 창인 내부 서브뷰/모달 배제
        nint owner = GetWindow(hWnd, GwOwner);
        if (owner != nint.Zero)
        {
            var ownerTitleBuffer = new StringBuilder(256);
            GetWindowText(owner, ownerTitleBuffer, ownerTitleBuffer.Capacity);
            if (ownerTitleBuffer.ToString().Trim().Equals("카카오톡", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 10. 내부 서브뷰 이름(MoreView 등)이 포함된 윈도우 배제
        if (title.StartsWith("MoreView", StringComparison.OrdinalIgnoreCase) ||
            title.EndsWith("View", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 11. 사용자가 카카오톡을 전면에 띄워 직접 조작/대화 중인 상태(Foreground == KakaoTalk) 배제
        nint fgHwnd = GetForegroundWindow();
        if (fgHwnd != nint.Zero)
        {
            GetWindowThreadProcessId(fgHwnd, out uint fgPid);
            if (fgPid == pid)
            {
                return false;
            }
        }

        // 12. 최소화된 창 배제
        if (IsIconic(hWnd))
        {
            return false;
        }

        GetWindowRect(hWnd, out Rect r);
        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;

        // 13. 창 크기 검증: DPI 스케일 반영 (200% High-DPI 등 대응)
        // 카카오톡 알림 토스트 논리 규격: 가로 약 300~320px, 세로 약 90~200px
        // 대화방 종료/퇴장 시 뜨는 날짜 배지(161x67 @ 200% -> 논리 80x33px) 및 0x0 더미 배제
        float scale = GetWindowDpiScale(hWnd);
        float logicalWidth = width / scale;
        float logicalHeight = height / scale;

        if (logicalWidth < 200 || logicalWidth > 800 || logicalHeight < 75 || logicalHeight > 800)
        {
            return false;
        }

        // 14. 가상 화면 영역 밖의 오프스크린 더미 창 배제
        int screenWidth = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        int screenHeight = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        int screenX = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int screenY = GetSystemMetrics(77); // SM_YVIRTUALSCREEN

        if (screenWidth <= 0) screenWidth = GetSystemMetrics(0);
        if (screenHeight <= 0) screenHeight = GetSystemMetrics(1);

        if (r.Right <= screenX || r.Left >= screenX + screenWidth ||
            r.Bottom <= screenY || r.Top >= screenY + screenHeight)
        {
            return false;
        }

        int exStyle = GetWindowLong(hWnd, GwlExStyle);
        AppLogger.Info($"[KakaoInterceptor] ✅ 카카오톡 순수 알림 팝업 창 확정: hWnd=0x{hWnd:X8}, Title='{title}', Pos=({r.Left},{r.Top}), Size=({width}x{height} [논리 {logicalWidth:F0}x{logicalHeight:F0}]), Style=0x{style:X8}, ExStyle=0x{exStyle:X8}");
        return true;
    }

    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : (nint)SetWindowLong32(hWnd, nIndex, (int)dwNewLong);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);
    private const uint PwRenderFullContent = 2;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private delegate bool EnumChildProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumChildProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, StringBuilder lParam);
    private const uint WmGetText = 0x000D;

    public bool HideWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        // 1. 화면 밖으로 던지지 않고 완전 투명화(Alpha = 1) + 클릭 관통(WsExTransparent) 적용
        // Alpha = 1 (0.39% 투명도)는 인간의 눈에 완전히 보이지 않으면서도
        // DWM/GDI 컴포지터가 윈도우 그래픽 렌더링을 정상 수행하므로 PrintWindow OCR이 온전히 동작함
        int exStyle = GetWindowLong(hWnd, GwlExStyle);
        SetWindowLongPtr(hWnd, GwlExStyle, exStyle | WsExLayered | WsExTransparent);
        SetWindowPos(hWnd, nint.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpNoActivate);
        SetLayeredWindowAttributes(hWnd, 0, 1, LwaAlpha);
        return true;
    }

    public bool ShowWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        // 2. 통과 알림 복원: 투명도 255(완전 불투명) 복원 및 클릭 관통 해제
        int exStyle = GetWindowLong(hWnd, GwlExStyle);
        SetWindowLongPtr(hWnd, GwlExStyle, (exStyle | WsExLayered) & ~WsExTransparent);
        SetWindowPos(hWnd, nint.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpNoActivate);
        SetLayeredWindowAttributes(hWnd, 0, 255, LwaAlpha);

        return ShowWindow(hWnd, SwShowNoActivate);
    }

    public bool CloseWindow(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return false;
        }

        // 3. 차단 알림 소멸:
        // 마우스 클릭 관통 상태를 유지한 채 화면 밖(-32000, -32000)으로 치우고 델파이/Win32 종료 메시지 복합 전송
        SetWindowPos(hWnd, nint.Zero, -32000, -32000, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        ShowWindow(hWnd, SwHide);
        PostMessage(hWnd, WmSysCommand, ScClose, nint.Zero);
        return PostMessage(hWnd, WmClose, nint.Zero, nint.Zero);
    }

    public KakaoNotificationText ExtractNotificationText(nint hWnd)
    {
        if (hWnd == nint.Zero)
        {
            return new KakaoNotificationText(null, "카카오톡", "(알림 내용 없음)");
        }

        try
        {
            // 1. 카카오톡 알림 팝업의 슬라이드 업 애니메이션 전개 대기
            // 카카오톡 토스트는 아래에서 위로 슬라이드되며 높이가 증가하고 본문이 렌더링됨
            int prevHeight = 0;
            float scale = GetWindowDpiScale(hWnd);

            for (int wait = 0; wait < 15; wait++)
            {
                GetWindowRect(hWnd, out Rect r);
                int h = r.Bottom - r.Top;
                float logH = h / scale;

                // 논리 높이가 최소 75px 이상 전개되었고, 직전 측정값과 동일하면 애니메이션 완료
                if (logH >= 75 && h == prevHeight)
                {
                    break;
                }
                prevHeight = h;
                Thread.Sleep(30);
            }
            Thread.Sleep(60); // GDI DrawText 렌더링 안정화 대기

            var titleBuffer = new StringBuilder(256);
            GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
            string windowTitle = titleBuffer.ToString().Trim();

            KakaoNotificationText? candidate = null;

            // 2. 카카오톡 그래픽 캔버스(GDI/Delphi) 텍스트를 Windows 내장 OCR로 추출 (최대 5회)
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var ocrText = TryExtractViaOcr(hWnd, windowTitle);
                if (ocrText != null)
                {
                    candidate = ocrText;
                    // 발신자와 본문이 모두 온전히 추출되었으면 즉시 확정
                    if (!string.IsNullOrWhiteSpace(ocrText.Body) && ocrText.Body != "(새 메시지)")
                    {
                        AppLogger.Info($"[KakaoInterceptor] ✅ OCR 텍스트 완벽 추출 성공 (시도 {attempt + 1}): 발신자='{ocrText.Sender}', 본문='{ocrText.Body}'");
                        return ocrText;
                    }
                }

                Thread.Sleep(50);
            }

            if (candidate != null)
            {
                AppLogger.Info($"[KakaoInterceptor] ✅ OCR 텍스트 추출 완료 (단일/후보): 발신자='{candidate.Sender}', 본문='{candidate.Body}'");
                return candidate;
            }

            // 3. OCR 실패 시 UI Automation 및 Win32 컨트롤 순회 (보조 fallback)
            var texts = new List<string>();
            try
            {
                var element = AutomationElement.FromHandle(hWnd);
                if (element != null)
                {
                    var textElements = element.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

                    foreach (AutomationElement item in textElements)
                    {
                        string name = item.Current.Name?.Trim() ?? string.Empty;
                        if (IsValidTextItem(name) && !texts.Contains(name))
                        {
                            texts.Add(name);
                        }
                    }

                    if (texts.Count == 0)
                    {
                        var allDescendants = element.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        foreach (AutomationElement item in allDescendants)
                        {
                            string name = item.Current.Name?.Trim() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                if (item.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern vp)
                                {
                                    name = vp.Current.Value?.Trim() ?? string.Empty;
                                }
                            }

                            if (IsValidTextItem(name) && !texts.Contains(name))
                            {
                                texts.Add(name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[KakaoInterceptor] UIA 텍스트 추출 예외: {ex.Message}");
            }

            if (texts.Count == 0)
            {
                EnumChildWindows(hWnd, (hChild, _) =>
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(hChild, sb, sb.Capacity);
                    string t = sb.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(t))
                    {
                        sb.Clear();
                        SendMessage(hChild, WmGetText, (nint)sb.Capacity, sb);
                        t = sb.ToString().Trim();
                    }

                    if (IsValidTextItem(t) && !texts.Contains(t))
                    {
                        texts.Add(t);
                    }
                    return true;
                }, nint.Zero);
            }

            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                var bodyLines = texts.Where(l => !l.Equals(windowTitle, StringComparison.OrdinalIgnoreCase)).ToList();
                string body = bodyLines.Count > 0 ? string.Join("\n", bodyLines) : "(새 메시지가 도착했습니다)";
                AppLogger.Info($"[KakaoInterceptor] 텍스트 추출 성공 (타이틀 연동): 발신자='{windowTitle}', 본문='{body}'");
                return new KakaoNotificationText(windowTitle, windowTitle, body);
            }

            if (texts.Count >= 2)
            {
                string sender = texts[0];
                string body = string.Join("\n", texts.Skip(1));
                AppLogger.Info($"[KakaoInterceptor] 텍스트 추출 성공: 발신자='{sender}', 본문='{body}'");
                return new KakaoNotificationText(sender, sender, body);
            }
            else if (texts.Count == 1)
            {
                AppLogger.Info($"[KakaoInterceptor] 단일 텍스트 추출: '{texts[0]}'");
                return new KakaoNotificationText(null, "카카오톡", texts[0]);
            }
            else
            {
                AppLogger.Warn($"[KakaoInterceptor] hWnd=0x{hWnd:X8}에서 텍스트를 추출하지 못하여 기본 알림으로 처리합니다.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] 텍스트 추출 예외: {ex.Message}");
        }

        return new KakaoNotificationText(null, "카카오톡", "(새 메시지가 도착했습니다)");
    }

    private static KakaoNotificationText? TryExtractViaOcr(nint hWnd, string? windowTitle = null)
    {
        try
        {
            if (hWnd == nint.Zero)
            {
                return null;
            }

            GetWindowRect(hWnd, out Rect r);
            int width = r.Right - r.Left;
            int height = r.Bottom - r.Top;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                nint hdc = gfx.GetHdc();
                try
                {
                    bool ok = PrintWindow(hWnd, hdc, PwRenderFullContent);
                    if (!ok)
                    {
                        PrintWindow(hWnd, hdc, 0);
                    }
                }
                finally
                {
                    gfx.ReleaseHdc(hdc);
                }
            }

            // [핵심 해결책] Alpha 채널 강제 불투명화 (Alpha = 255)
            // Layered Window (Alpha = 1)로 인해 캡처된 32bpp 비트맵의 알파 채널이 거의 0에 가까워
            // Windows OCR 엔진이 투명한 빈 캔버스로 인식하는 문제를 비트맵 메모리를 직접 조작하여 완벽히 해결!
            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb);
            try
            {
                int totalBytes = Math.Abs(bmpData.Stride) * height;
                byte[] rgbValues = new byte[totalBytes];
                Marshal.Copy(bmpData.Scan0, rgbValues, 0, totalBytes);
                for (int i = 3; i < totalBytes; i += 4)
                {
                    rgbValues[i] = 255; // 100% 완전 불투명화
                }
                Marshal.Copy(rgbValues, 0, bmpData.Scan0, totalBytes);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            try
            {
                string debugDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Altong");
                Directory.CreateDirectory(debugDir);
                bmp.Save(Path.Combine(debugDir, "last_kakao_capture.png"), ImageFormat.Png);
            }
            catch { }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Bmp);
            var bytes = ms.ToArray();

            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(ras))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            ras.Seek(0);

            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
            var softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();

            var ocr = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocr == null)
            {
                return null;
            }

            var result = ocr.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
            if (result.Lines.Count == 0)
            {
                return null;
            }

            var validLines = result.Lines
                .Select(l => l.Text.Trim())
                .Where(IsValidTextItem)
                .ToList();

            if (!string.IsNullOrWhiteSpace(windowTitle) && validLines.Count > 0)
            {
                var bodyLines = validLines.Where(l => !l.Equals(windowTitle, StringComparison.OrdinalIgnoreCase)).ToList();
                string body = bodyLines.Count > 0 ? string.Join("\n", bodyLines) : "(새 메시지)";
                return new KakaoNotificationText(windowTitle, windowTitle, body);
            }

            if (validLines.Count >= 2)
            {
                string sender = validLines[0];
                string body = string.Join("\n", validLines.Skip(1));
                return new KakaoNotificationText(sender, sender, body);
            }
            else if (validLines.Count == 1)
            {
                // 단일 라인만 검출된 경우: 1차적으로 발신자/방이름으로 취급
                string single = validLines[0];
                return new KakaoNotificationText(single, single, "(새 메시지)");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] OCR 텍스트 추출 중 예외: {ex.Message}");
        }

        return null;
    }

    private static bool IsValidTextItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 특수문자/구분선만 있는 라인(예: "-", "_", "•") 배제
        if (!text.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        // 불필요한 컨트롤 라벨 및 닫기 버튼 배제
        string lower = text.ToLowerInvariant().Replace(" ", "");
        return lower != "닫기" && lower != "x" && lower != "close" &&
               lower != "답장" && lower != "전송" && !lower.Contains("메시지입력") &&
               !lower.Contains("이콘표시") && !lower.Contains("아이콘표시");
    }
}

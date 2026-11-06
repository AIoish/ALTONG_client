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

    private static readonly Lazy<Windows.Media.Ocr.OcrEngine?> _cachedWindowsOcr = new(() =>
    {
        try
        {
            var koLang = new Windows.Globalization.Language("ko-KR");
            return Windows.Media.Ocr.OcrEngine.IsLanguageSupported(koLang)
                ? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(koLang)
                : Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] Windows OCR 엔진 초기화 실패: {ex.Message}");
            return null;
        }
    });

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

            var titleBuffer = new StringBuilder(256);
            GetWindowText(hWnd, titleBuffer, titleBuffer.Capacity);
            string windowTitle = titleBuffer.ToString().Trim();

            KakaoNotificationText? candidate = null;

            // 2. 카카오톡 그래픽 캔버스(GDI/Delphi) 텍스트를 Windows 내장 OCR로 추출 (최대 5회)
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var ocrText = TryExtractViaOcr(hWnd);
                if (ocrText != null)
                {
                    candidate = ocrText;
                    // 발신자와 본문이 모두 온전히 추출되었으면 즉시 확정
                    if (!string.IsNullOrWhiteSpace(ocrText.Body) &&
                        ocrText.Body != MissingBody &&
                        ocrText.Body != MediaBody)
                    {
                        string senderPart = ocrText.Sender == ocrText.Title ? ocrText.Sender : $"[{ocrText.Title}] {ocrText.Sender}";
                        AppLogger.Info($"[KakaoInterceptor] OCR 추출 완료 (시도 {attempt + 1}): {senderPart} - \"{ocrText.Body}\"");
                        return ocrText;
                    }
                }
            }

            if (candidate != null)
            {
                string candidateSenderPart = candidate.Sender == candidate.Title ? candidate.Sender : $"[{candidate.Title}] {candidate.Sender}";
                AppLogger.Info($"[KakaoInterceptor] OCR 추출 완료 (후보): {candidateSenderPart} - \"{candidate.Body}\"");
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

            if (texts.Count >= 3)
            {
                string chatTitle = texts[0];
                string senderName = texts[1];
                string body = string.Join("\n", texts.Skip(2));
                AppLogger.Info($"[KakaoInterceptor] UIA 텍스트 추출 성공: 방제='{chatTitle}', 발신자='{senderName}', 본문='{body}'");
                return new KakaoNotificationText(senderName, chatTitle, body);
            }
            else if (texts.Count == 2)
            {
                AppLogger.Info($"[KakaoInterceptor] UIA 2줄 텍스트 추출: 방제='{texts[0]}', 발신자='{texts[1]}'");
                return new KakaoNotificationText(texts[1], texts[0], "(새 메시지)");
            }
            else if (texts.Count == 1)
            {
                AppLogger.Info($"[KakaoInterceptor] UIA 단일 텍스트 추출: '{texts[0]}'");
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

    private record ParsedOcrLine(string Text, double X, double Y, double Width, double Height);

    // 팝업 카드의 논리 좌표(96 DPI 기준). 200% 캡처에서 잰 값: 방 이름 줄 위쪽 약 10, 발신자 줄 약 36,
    // 본문 줄 약 56. 프로필 사진은 x 15~53, y 27~68이고 글자 열은 x 65부터 시작한다.
    internal const double SenderRowTop = 24;
    internal const double BodyRowTop = 48;
    internal const double AvatarRight = 60;
    internal const double AvatarTop = 24;

    internal const string MissingBody = "(새 메시지)";
    internal const string MediaBody = "(이모티콘 또는 미디어)";

    public ulong CaptureContentHash(nint hWnd)
    {
        try
        {
            using var card = CaptureCard(hWnd, out _);
            return card is null ? 0 : ComputeContentHash(card);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] 팝업 내용 확인 실패: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 팝업을 캡처해 OCR용 카드 이미지를 만든다. 빠른 답장 알림창(논리 높이 105 이상)은 아래 38%가
    /// '메시지 입력'·'전송' 영역이라 잘라내어, 전송 버튼('저소' 오인식)이 OCR에 들어가지 않게 한다.
    /// </summary>
    private static Bitmap? CaptureCard(nint hWnd, out float scale)
    {
        scale = GetWindowDpiScale(hWnd);
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

        using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var gfx = Graphics.FromImage(full))
        {
            nint hdc = gfx.GetHdc();
            try
            {
                if (!PrintWindow(hWnd, hdc, PwRenderFullContent))
                {
                    PrintWindow(hWnd, hdc, 0);
                }
            }
            finally
            {
                gfx.ReleaseHdc(hdc);
            }
        }

        int cardHeight = height / scale >= 105 ? (int)Math.Round(height * 0.62) : height;
        var card = full.Clone(new Rectangle(0, 0, width, cardHeight), PixelFormat.Format32bppArgb);
        PrepareForOcr(card, scale);
        return card;
    }

    /// <summary>
    /// 1. 알파 채널 불투명화: 숨김(Alpha = 1) 상태로 캡처하면 알파가 0에 가까워 OCR이 빈 캔버스로 본다.
    /// 2. 다크 모드(어두운 배경 + 밝은 글씨)면 밝기를 반전한다.
    /// 3. 프로필 사진 영역을 배경색으로 덮는다. OCR이 사진을 '8', '©', '-' 같은 글자로 읽어
    ///    발신자·본문 줄에 섞거나 두 줄을 합친 엉뚱한 줄을 만들기 때문이다. 맨 위 방 이름 줄은 건드리지 않는다.
    /// </summary>
    internal static void PrepareForOcr(Bitmap card, float scale)
    {
        int width = card.Width;
        int height = card.Height;
        var data = card.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            long borderLumSum = 0;
            int borderSamples = 0;
            int topY = Math.Min(2, height - 1);
            int bottomY = Math.Max(0, height - 3);
            for (int x = 2; x < width - 2; x += 4)
            {
                borderLumSum += Luminance(pixels, topY * stride + x * 4);
                borderLumSum += Luminance(pixels, bottomY * stride + x * 4);
                borderSamples += 2;
            }

            bool isDarkMode = borderSamples > 0 && borderLumSum / borderSamples < 120;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (isDarkMode)
                {
                    pixels[i] = (byte)(255 - pixels[i]);
                    pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                    pixels[i + 2] = (byte)(255 - pixels[i + 2]);
                }
                pixels[i + 3] = 255;
            }

            int avatarTop = (int)Math.Round(AvatarTop * scale);
            int avatarRight = Math.Min(width, (int)Math.Round(AvatarRight * scale));
            if (avatarTop < height && avatarRight > 2)
            {
                // 왼쪽 여백(x = 2)은 카드 배경이다.
                int backgroundOffset = avatarTop * stride + 2 * 4;
                for (int y = avatarTop; y < height; y++)
                {
                    for (int x = 0; x < avatarRight; x++)
                    {
                        Buffer.BlockCopy(pixels, backgroundOffset, pixels, y * stride + x * 4, 4);
                    }
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            card.UnlockBits(data);
        }
    }

    private static int Luminance(byte[] pixels, int offset) =>
        (pixels[offset + 2] * 299 + pixels[offset + 1] * 587 + pixels[offset] * 114) / 1000;

    /// <summary>
    /// 팝업에 그려진 내용의 지문. OCR하는 동안 카카오톡이 같은 팝업에 다음 메시지를 덮어 그렸는지 확인하는 데 쓴다.
    /// 마우스를 올리면 바뀌는 오른쪽 위 벨·닫기 아이콘 영역은 뺀다. 0은 "확인 불가"라 쓰지 않는다.
    /// </summary>
    internal static ulong ComputeContentHash(Bitmap card)
    {
        int width = card.Width;
        int height = card.Height;
        var data = card.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            int toolbarLeft = (int)(width * 0.70);
            int toolbarBottom = (int)(height * 0.35);
            ulong hash = 14695981039346656037UL;
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int end = y < toolbarBottom ? toolbarLeft : width;
                for (int i = row; i < row + end * 4; i++)
                {
                    hash = (hash ^ pixels[i]) * 1099511628211UL;
                }
            }

            return hash == 0 ? 1 : hash;
        }
        finally
        {
            card.UnlockBits(data);
        }
    }

    private static KakaoNotificationText? TryExtractViaOcr(nint hWnd)
    {
        try
        {
            using var card = CaptureStableCard(hWnd, out float scale, out ulong contentHash);
            if (card is null)
            {
                return null;
            }

            var mapped = MapOcrLines(RecognizeLines(card, scale, saveDebugImages: true));
            return mapped is null ? null : mapped with { ContentHash = contentHash };
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] OCR 텍스트 추출 중 예외: {ex.Message}");
        }

        return null;
    }

    private const int MaxStabilityChecks = 6;

    /// <summary>
    /// 50ms 간격으로 캡처해 두 번 연속 같은 그림이 나오면 그 그림을 쓴다 (최대 약 0.3초 기다림).
    /// 팝업이 덜 그려진 상태에서 읽으면 "인식못하나봐요"가 "인식못하나봐"로 끝이 잘리고,
    /// 닫기 전 확인에서 그림이 달라져 같은 메시지가 두 번 들어간다 (11/04 실측).
    /// </summary>
    private static Bitmap? CaptureStableCard(nint hWnd, out float scale, out ulong contentHash)
    {
        Bitmap? card = CaptureCard(hWnd, out scale);
        contentHash = card is null ? 0 : ComputeContentHash(card);
        for (int check = 0; card is not null && check < MaxStabilityChecks; check++)
        {
            Thread.Sleep(50);
            var next = CaptureCard(hWnd, out scale);
            if (next is null)
            {
                break;
            }

            ulong nextHash = ComputeContentHash(next);
            card.Dispose();
            card = next;
            if (nextHash == contentHash)
            {
                break;
            }

            contentHash = nextHash;
        }

        return card;
    }

    /// <summary>
    /// 손질한 카드 이미지에서 OCR 줄과 각 줄의 위쪽 좌표(논리 px)를 읽는다.
    /// </summary>
    internal static List<(string Text, double LogicalTop)> RecognizeLines(Bitmap card, float scale, bool saveDebugImages = false)
    {
        int width = card.Width;
        int effectiveHeight = card.Height;

        // [핵심 성능 개선] 2.5배 고품질 Bicubic 업스케일링
        // 카카오톡 알림 팝업의 텍스트는 9~10pt(약 12~14px)로 매우 작아 Windows.Media.Ocr 엔진이
        // 복잡한 한글 자모('늘', '뭥' 등)를 인식하지 못하고 노이즈로 버리는 문제를 완벽히 해결!
        // 2.5배 확대 시 폰트 높이가 약 32~35px로 커져 OCR 권장 인식 범위(30~45px)에 정확히 진입함.
        const float OcrUpscaleFactor = 2.5f;
        int scaledWidth = (int)Math.Round(width * OcrUpscaleFactor);
        int scaledHeight = (int)Math.Round(effectiveHeight * OcrUpscaleFactor);

        using var scaledBmp = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaledBmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            // PixelOffsetMode.HighQuality는 내부적으로 -0.5px 하프 픽셀 쉬프트를 일으켜 좁은 한글 자간(예: '현우' -> '혀O')을 뭉개므로 배제
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(card, new Rectangle(0, 0, scaledWidth, scaledHeight), 0, 0, width, effectiveHeight, GraphicsUnit.Pixel);
        }

#if DEBUG
        if (saveDebugImages)
        {
            try
            {
                string debugDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Altong");
                Directory.CreateDirectory(debugDir);
                card.Save(Path.Combine(debugDir, "last_kakao_capture.png"), ImageFormat.Png);
                scaledBmp.Save(Path.Combine(debugDir, "last_kakao_capture_upscaled.png"), ImageFormat.Png);
            }
            catch { }
        }
#endif

        using var ms = new MemoryStream();
        scaledBmp.Save(ms, ImageFormat.Bmp);
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
        using var softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();

        var ocr = _cachedWindowsOcr.Value;
        var parsedLines = new List<ParsedOcrLine>();

        if (ocr != null)
        {
            var result = ocr.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
            double textAngle = result.TextAngle ?? 0;
            if (Math.Abs(textAngle) >= 0.5)
            {
                AppLogger.Info($"[KakaoInterceptor] Windows OCR이 기울기 {textAngle:0.0}도로 판단해 줄 좌표를 되돌림");
            }
            if (result.Lines != null)
            {
            foreach (var line in result.Lines)
            {
                string text = line.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                double rawMinX = line.Words.Count > 0 ? line.Words.Min(w => w.BoundingRect.X) : 0;
                double rawMinY = line.Words.Count > 0 ? line.Words.Min(w => w.BoundingRect.Y) : 0;
                double rawMaxX = line.Words.Count > 0 ? line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width) : 0;
                double rawMaxY = line.Words.Count > 0 ? line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height) : 0;
                (rawMinX, rawMinY, rawMaxX, rawMaxY) = UnrotateOcrBox(
                    rawMinX, rawMinY, rawMaxX, rawMaxY, textAngle, scaledWidth / 2.0, scaledHeight / 2.0);

                // 업스케일링 좌표를 원본 창 좌표계로 정규화 복원
                double minX = rawMinX / OcrUpscaleFactor;
                double minY = rawMinY / OcrUpscaleFactor;
                double maxX = rawMaxX / OcrUpscaleFactor;
                double maxY = rawMaxY / OcrUpscaleFactor;

                // 1. 우상단 툴바 영역 (알림 음소거 벨 아이콘 '0'/'O', 닫기 'X' 버튼) 배제
                // 카카오톡 토스트 우측 상단(X > 전체 너비의 70%, Y < 상단 35%)에 위치한 UI 아이콘 배제
                if (minX > width * 0.70 && minY < effectiveHeight * 0.35)
                {
                    continue;
                }

                // [PP-OCRv5 온디바이스 라인 정밀 인식 결합]
                // Windows.Media.Ocr가 검출한 라인 Y-Band 영역을 잘라내어 PP-OCRv5 ONNX 엔진으로 정밀 인식
                string lineText = text;
                if (PaddleOcrLineRecognizer.Instance.IsAvailable)
                {
                    int cropX = Math.Max(0, (int)Math.Floor(rawMinX));
                    int cropY = Math.Max(0, (int)Math.Floor(rawMinY));
                    int cropW = Math.Min(scaledWidth - cropX, (int)Math.Ceiling(rawMaxX - rawMinX));
                    int cropH = Math.Min(scaledHeight - cropY, (int)Math.Ceiling(rawMaxY - rawMinY));

                    if (cropW > 0 && cropH > 0)
                    {
                        var lineRect = new Rectangle(cropX, cropY, cropW, cropH);
                        string? recognized = PaddleOcrLineRecognizer.Instance.RecognizeLine(scaledBmp, lineRect);
                        if (!string.IsNullOrWhiteSpace(recognized))
                        {
                            lineText = recognized;
                        }
                    }
                }

                // PP-OCRv5 미사용 또는 잔여 폰트 글리프 혼동(ㄷㄷㄷ->CCC, ㅠㅠ->TT, ㅡㅡ->--) 보정
                lineText = PaddleOcrLineRecognizer.NormalizeJamoConfusion(lineText);

                // 2. 단독 기호 및 불필요한 컨트롤 라벨(전송, 메시지 입력 등) 배제
                // (ONNX 라인 인식 및 자모 혼동 보정 완료 후 검사하므로 '[[['나 '==='로 인한 조기 삭제 방지)
                if (!IsValidTextItem(lineText))
                {
                    continue;
                }

                // 3. 단일 숫자(0, 1 등) 또는 단일 영문 기호(o, x 등) 오인식 배제
                if (lineText.Length == 1 && (char.IsDigit(lineText[0]) || lineText.Equals("o", StringComparison.OrdinalIgnoreCase) || lineText.Equals("x", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                parsedLines.Add(new ParsedOcrLine(lineText, minX, minY, maxX - minX, maxY - minY));
            }
        }
        }

        // 4. [Body-Band 가로 픽셀 투영 Fallback]
        // Windows.Media.Ocr가 'ㅡㅡ' 등 순수 가로선 자모를 구분선 기호로 오인하여 본문 줄을 누락한 경우,
        // 본문 영역의 가로 픽셀 투영으로 Y-Band를 검출해 PP-OCRv5로 구조 복원
        bool hasBodyLine = parsedLines.Any(p => (p.Y / scale) >= BodyRowTop);
        if (!hasBodyLine && PaddleOcrLineRecognizer.Instance.IsAvailable)
        {
            int bodyStartY = (int)Math.Round(BodyRowTop * scale * OcrUpscaleFactor);
            int bodyEndY = scaledHeight - (int)Math.Round(4 * scale * OcrUpscaleFactor);
            int bodyStartX = parsedLines.Count > 0
                ? Math.Max(0, (int)Math.Floor(parsedLines.Min(p => p.X) * OcrUpscaleFactor) - 10)
                : (int)Math.Round(width * 0.15 * OcrUpscaleFactor);
            int bodyEndX = Math.Min(scaledWidth, (int)Math.Ceiling(width * 0.85 * OcrUpscaleFactor));

            var detectedBand = DetectBodyLineBand(scaledBmp, bodyStartX, bodyStartY, bodyEndX, bodyEndY);
            if (detectedBand.HasValue)
            {
                string? bodyText = PaddleOcrLineRecognizer.Instance.RecognizeLine(scaledBmp, detectedBand.Value);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    bodyText = PaddleOcrLineRecognizer.NormalizeJamoConfusion(bodyText);
                    if (IsValidTextItem(bodyText))
                    {
                        double bMinX = detectedBand.Value.X / OcrUpscaleFactor;
                        double bMinY = detectedBand.Value.Y / OcrUpscaleFactor;
                        double bW = detectedBand.Value.Width / OcrUpscaleFactor;
                        double bH = detectedBand.Value.Height / OcrUpscaleFactor;
                        parsedLines.Add(new ParsedOcrLine(bodyText, bMinX, bMinY, bW, bH));
                    }
                }
            }
        }

        return parsedLines.Select(p => (p.Text, p.Y / scale)).ToList();
    }

    /// <summary>
    /// Windows OCR은 글자가 기울었다고 판단하면(TextAngle) 기울기를 바로잡은 이미지 기준으로 단어 상자를 준다.
    /// 팝업 글자는 기울어 있지 않아도 줄이 적으면 각도를 잘못 잡는 일이 있어(-7.6도 사례), 상자를 원래 이미지 좌표로
    /// 되돌려야 PP-OCR이 글자를 온전히 잘라 읽고 줄 위치로 방 이름·발신자·본문을 제대로 나눈다.
    /// 이미지 중심을 기준으로 상자 네 모서리를 각도만큼 돌린 뒤 감싸는 상자를 돌려준다.
    /// </summary>
    internal static (double MinX, double MinY, double MaxX, double MaxY) UnrotateOcrBox(
        double minX, double minY, double maxX, double maxY, double angleDegrees, double centerX, double centerY)
    {
        if (angleDegrees == 0)
        {
            return (minX, minY, maxX, maxY);
        }

        double radians = angleDegrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        var corners = new[] { (minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY) }
            .Select(p => (
                X: centerX + (p.Item1 - centerX) * cos - (p.Item2 - centerY) * sin,
                Y: centerY + (p.Item1 - centerX) * sin + (p.Item2 - centerY) * cos))
            .ToArray();
        return (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
    }

    /// <summary>
    /// OCR 줄을 위치로 방 이름·발신자·본문에 나눈다. 1:1 대화도 맨 위 방 이름(상대 이름)과 발신자 줄이 따로 있다.
    /// 줄 수로 나누면 잡음 줄 하나에 전체가 한 칸씩 밀리므로 줄의 위쪽 좌표(논리 px)로 나눈다.
    /// 발신자 줄을 못 읽으면 방 이름을 발신자로 쓴다 (1:1 대화에서는 둘이 같다).
    /// </summary>
    internal static KakaoNotificationText? MapOcrLines(IReadOnlyList<(string Text, double LogicalTop)> lines)
    {
        var ordered = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.LogicalTop)
            .ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        string? Join(double top, double bottom, string separator)
        {
            var texts = ordered
                .Where(l => l.LogicalTop >= top && l.LogicalTop < bottom)
                .Select(l => l.Text.Trim())
                .ToList();
            return texts.Count == 0 ? null : string.Join(separator, texts);
        }

        string? title = Join(double.NegativeInfinity, SenderRowTop, " ");
        string? sender = Join(SenderRowTop, BodyRowTop, " ");
        string? body = Join(BodyRowTop, double.PositiveInfinity, "\n");

        return new KakaoNotificationText(
            Sender: sender ?? title,
            Title: title ?? sender ?? "카카오톡",
            Body: body ?? (sender is not null ? MediaBody : MissingBody));
    }

    internal static Rectangle? DetectBodyLineBand(Bitmap scaledBmp, int startX, int startY, int endX, int endY)
    {
        int w = endX - startX;
        int h = endY - startY;
        if (w <= 0 || h <= 0 || startX < 0 || startY < 0 || endX > scaledBmp.Width || endY > scaledBmp.Height)
        {
            return null;
        }

        var rect = new Rectangle(startX, startY, w, h);
        var bmpData = scaledBmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(bmpData.Stride);
            byte[] buffer = new byte[stride * h];
            Marshal.Copy(bmpData.Scan0, buffer, 0, buffer.Length);

            int firstActiveY = -1;
            int lastActiveY = -1;
            int minContentX = int.MaxValue;
            int maxContentX = int.MinValue;

            int c0 = (buffer[0] * 114 + buffer[1] * 587 + buffer[2] * 299) / 1000;
            int c1 = (buffer[(w - 1) * 4] * 114 + buffer[(w - 1) * 4 + 1] * 587 + buffer[(w - 1) * 4 + 2] * 299) / 1000;
            int c2 = (buffer[(h - 1) * stride] * 114 + buffer[(h - 1) * stride + 1] * 587 + buffer[(h - 1) * stride + 2] * 299) / 1000;
            int c3 = (buffer[(h - 1) * stride + (w - 1) * 4] * 114 + buffer[(h - 1) * stride + (w - 1) * 4 + 1] * 587 + buffer[(h - 1) * stride + (w - 1) * 4 + 2] * 299) / 1000;
            bool isDark = (c0 + c1 + c2 + c3) / 4 < 120;

            for (int y = 0; y < h; y++)
            {
                int rowOffset = y * stride;
                int strokePixelCount = 0;
                for (int x = 0; x < w; x++)
                {
                    int pxOffset = rowOffset + (x * 4);
                    byte b = buffer[pxOffset];
                    byte g = buffer[pxOffset + 1];
                    byte r = buffer[pxOffset + 2];

                    bool isStroke = isDark
                        ? (r > 110 || g > 110 || b > 110)
                        : (r < 200 || g < 200 || b < 200);

                    if (isStroke)
                    {
                        strokePixelCount++;
                        if (x < minContentX) minContentX = x;
                        if (x > maxContentX) maxContentX = x;
                    }
                }

                if (strokePixelCount >= 4)
                {
                    if (firstActiveY == -1) firstActiveY = y;
                    lastActiveY = y;
                }
            }

            int contentW = maxContentX - minContentX + 1;
            int contentH = lastActiveY - firstActiveY + 1;

            if (firstActiveY != -1 && contentW >= 6 && contentH >= 1)
            {
                int bandY = Math.Max(0, startY + firstActiveY - 2);
                int bandH = Math.Min(scaledBmp.Height - bandY, contentH + 4);
                int bandX = Math.Max(0, startX + minContentX - 2);
                int bandW = Math.Min(scaledBmp.Width - bandX, contentW + 4);
                return new Rectangle(bandX, bandY, bandW, bandH);
            }

            return null;
        }
        finally
        {
            scaledBmp.UnlockBits(bmpData);
        }
    }

    private static bool IsValidTextItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 특수문자/구분선만 있는 라인(예: "-", "_", "•") 배제
        // 단, 한글 자모 단독 메시지(예: "ㅠ", "ㅋ", "ㅎ", "ㅇ", "ㅠㅠ", "ㅋㅋㅋ")는 유효한 메시지 본문으로 허용
        if (!text.Any(char.IsLetterOrDigit) && !text.Any(c => c >= 0x3131 && c <= 0x318E))
        {
            return false;
        }

        // 불필요한 컨트롤 라벨 및 닫기 버튼 배제
        // (참고: '전송' 버튼 및 '메시지 입력' 필드는 상단에서 logH >= 105일 때 물리적으로 잘라내어 OCR 엔진에 넘기지 않으므로,
        //  실제 사용자가 보낸 "파일 전송 완료"나 단독 "전송" 메시지는 절대 잘리지 않고 100% 온전히 수신됨)
        string lower = text.ToLowerInvariant().Replace(" ", "");
        return lower != "닫기" && lower != "x" && lower != "close" &&
               lower != "답장" &&
               !lower.Contains("메시지입력") &&
               !lower.Contains("이콘표시") && !lower.Contains("아이콘표시");
    }
}

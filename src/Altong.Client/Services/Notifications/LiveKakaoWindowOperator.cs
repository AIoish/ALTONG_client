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
            return new KakaoNotificationText(null, "카카오톡", KakaoNotificationText.EmptyBody);
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

            // 2. 카카오톡은 글자를 직접 그리므로 팝업 그림을 OCR로 읽는다 (최대 5회).
            // 본문을 못 읽었으면 팝업이 덜 그려졌을 수 있어 다시 읽는다. 그림이 직전과 같으면 결과도 같으므로 그만둔다.
            ulong previousHash = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var ocrText = TryExtractViaOcr(hWnd);
                if (ocrText != null)
                {
                    candidate = ocrText;
                    if (!KakaoNotificationText.IsUnreadBody(ocrText.Body))
                    {
                        string senderPart = ocrText.Sender == ocrText.Title ? ocrText.Sender : $"[{ocrText.Title}] {ocrText.Sender}";
                        AppLogger.Info($"[KakaoInterceptor] OCR 추출 완료 (시도 {attempt + 1}): {senderPart} - \"{ocrText.Body}\"");
                        return ocrText;
                    }

                    if (ocrText.ContentHash != 0 && ocrText.ContentHash == previousHash)
                    {
                        break;
                    }

                    previousHash = ocrText.ContentHash;
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
                string body = bodyLines.Count > 0 ? string.Join("\n", bodyLines) : KakaoNotificationText.ArrivedBody;
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
                return new KakaoNotificationText(texts[1], texts[0], KakaoNotificationText.MissingBody);
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

        return new KakaoNotificationText(null, "카카오톡", KakaoNotificationText.ArrivedBody);
    }

    // 팝업 카드의 논리 좌표(96 DPI 기준). 200% 캡처에서 잰 값: 방 이름 줄 위쪽 약 10, 발신자 줄 약 36,
    // 본문 줄 약 56. 프로필 사진은 x 15~53, y 27~68이고 글자 열은 x 65부터 시작한다.
    internal const double SenderRowTop = 24;
    internal const double BodyRowTop = 48;
    internal const double AvatarRight = 60;
    internal const double AvatarTop = 24;

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

            var lines = RecognizeLines(card, scale, saveDebugImages: true);
            var mapped = MapOcrLines(lines);
            if (mapped is null || KakaoNotificationText.IsUnreadBody(mapped.Body))
            {
                ReportUnreadCard(card, contentHash, lines);
            }

            return mapped is null ? null : mapped with { ContentHash = contentHash };
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] OCR 텍스트 추출 중 예외: {ex.Message}");
        }

        return null;
    }

    private const int MaxUnreadCaptures = 20;

    /// <summary>
    /// 본문을 읽지 못한 팝업에서 읽은 줄과 줄 위치를 로그에 남긴다. 개발 빌드에서는 그 팝업 그림도 남겨
    /// 원인을 재현할 수 있게 한다. 같은 그림은 같은 파일에 덮어쓰고 최근 20장만 둔다.
    /// </summary>
    private static void ReportUnreadCard(Bitmap card, ulong contentHash, IReadOnlyList<(string Text, double LogicalTop)> lines)
    {
        string found = lines.Count == 0
            ? "없음"
            : string.Join(", ", lines.Select(l => $"'{l.Text}'({l.LogicalTop:0})"));
        AppLogger.Info($"[KakaoInterceptor] 본문 줄을 읽지 못함. 읽은 줄(위쪽 논리 px): {found}");

#if DEBUG
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Altong",
                "kakao_unread");
            Directory.CreateDirectory(directory);
            card.Save(Path.Combine(directory, $"{contentHash:x16}.png"), ImageFormat.Png);
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.png")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(MaxUnreadCaptures))
            {
                old.Delete();
            }
        }
        catch { }
#endif
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
    /// 손질한 카드 이미지에서 글자 줄과 각 줄의 위쪽 좌표(논리 px)를 읽는다.
    /// 줄 위치는 OCR 엔진에 맡기지 않고 픽셀로 직접 찾는다(<see cref="FindTextRows"/>).
    /// Windows OCR에 줄 찾기를 맡기면 자음만 있는 줄("ㅋㅋㅋ")과 기호만 있는 줄("?")을 놓치고,
    /// 글자가 적은 팝업은 기울었다고 잘못 판단해 줄을 빠뜨리거나 줄 상자를 어긋나게 준다.
    /// 찾은 줄은 PP-OCR로 읽고, PP-OCR 모델이 없으면 Windows OCR이 읽은 단어를 그 줄에 나눠 담는다.
    /// </summary>
    internal static List<(string Text, double LogicalTop)> RecognizeLines(
        Bitmap card, float scale, bool saveDebugImages = false, bool allowPaddle = true)
    {
        int width = card.Width;
        int effectiveHeight = card.Height;

        // 팝업 글자는 9~10pt(약 12~14px)로 작아 그대로는 OCR이 복잡한 한글("늘", "뭥")을 놓친다.
        // 2.5배로 키우면 글자 높이가 약 32~35px이 되어 OCR이 잘 읽는 크기에 들어온다.
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

        double pxPerLogical = scale * OcrUpscaleFactor;
        bool usePaddle = allowPaddle && PaddleOcrLineRecognizer.Instance.IsAvailable;
        var rows = FindTextRows(scaledBmp, pxPerLogical, out Color background);
        IReadOnlyList<string?> texts = usePaddle
            ? rows.Select(row => PaddleOcrLineRecognizer.Instance.RecognizeLine(scaledBmp, row)).ToList()
            : ReadRowsWithWindowsOcr(scaledBmp, rows, background);

        var lines = new List<(string Text, double LogicalTop)>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(texts[i]))
            {
                lines.Add((texts[i]!.Trim(), rows[i].Top / pxPerLogical));
            }
        }

        return lines;
    }

    /// <summary>
    /// 카드에서 글자가 있는 줄을 위에서 아래 순서로 찾는다. 카드는 배경이 한 가지 색이고 프로필 사진은 이미
    /// 덮여 있어서, 배경과 다른 픽셀이 있는 가로줄이 이어진 구간이 곧 글자 한 줄이다.
    /// - 창 테두리(가장자리 2 논리 px)와 오른쪽 위 벨·닫기 아이콘 영역은 보지 않는다.
    /// - 한 줄 안에서도 구간이 끊길 수 있다. "넵"은 받침 위에서, 물음표·쌍반점은 점 위에서 끊긴다.
    ///   틈이 아주 좁거나(2 논리 px 이하), 한쪽이 낮은 조각이고 틈이 좁으면(3.5 논리 px 이하) 한 줄로 합친다.
    ///   합친 높이가 한 줄 높이(15 논리 px)를 넘으면 서로 다른 줄이므로 합치지 않는다.
    /// </summary>
    internal static List<Rectangle> FindTextRows(Bitmap image, double pxPerLogical, out Color background)
    {
        const double EdgeMargin = 2;
        const double ToolbarLeftRatio = 0.80;
        const double ToolbarBottomRatio = 0.35;
        const double SplitGlyphGap = 2;
        const double FragmentHeight = 4;
        const double FragmentGap = 3.5;
        const double MaxLineHeight = 15;
        const int InkDifference = 48;
        const int MinInkPerRow = 2;

        int width = image.Width;
        int height = image.Height;
        var rows = new List<Rectangle>();
        var data = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            int backgroundOffset = FindBackgroundOffset(pixels, stride, width, height, pxPerLogical);
            byte backgroundB = pixels[backgroundOffset];
            byte backgroundG = pixels[backgroundOffset + 1];
            byte backgroundR = pixels[backgroundOffset + 2];
            background = Color.FromArgb(backgroundR, backgroundG, backgroundB);

            int margin = Math.Max(1, (int)Math.Round(EdgeMargin * pxPerLogical));
            int toolbarLeft = (int)(width * ToolbarLeftRatio);
            int toolbarBottom = (int)(height * ToolbarBottomRatio);

            bool IsInk(int x, int y)
            {
                int offset = y * stride + x * 4;
                return Math.Abs(pixels[offset] - backgroundB) > InkDifference ||
                       Math.Abs(pixels[offset + 1] - backgroundG) > InkDifference ||
                       Math.Abs(pixels[offset + 2] - backgroundR) > InkDifference;
            }

            int RowEnd(int y) => Math.Min(width - margin, y < toolbarBottom ? toolbarLeft : width);

            // 1. 글자 픽셀이 있는 가로줄이 이어진 구간을 찾는다.
            var runs = new List<(int Top, int Bottom)>();
            int runTop = -1;
            for (int y = margin; y <= height - margin; y++)
            {
                bool hasInk = false;
                if (y < height - margin)
                {
                    int count = 0;
                    int end = RowEnd(y);
                    for (int x = margin; x < end && !hasInk; x++)
                    {
                        if (IsInk(x, y) && ++count >= MinInkPerRow)
                        {
                            hasInk = true;
                        }
                    }
                }

                if (hasInk && runTop < 0)
                {
                    runTop = y;
                }
                else if (!hasInk && runTop >= 0)
                {
                    runs.Add((runTop, y - 1));
                    runTop = -1;
                }
            }

            // 2. 한 줄 안에서 끊긴 구간을 합친다.
            int splitGlyphGap = (int)Math.Round(SplitGlyphGap * pxPerLogical);
            int fragmentHeight = (int)Math.Round(FragmentHeight * pxPerLogical);
            int fragmentGap = (int)Math.Round(FragmentGap * pxPerLogical);
            int maxLineHeight = (int)Math.Round(MaxLineHeight * pxPerLogical);
            for (int i = 0; i < runs.Count - 1;)
            {
                var (upper, lower) = (runs[i], runs[i + 1]);
                int gap = lower.Top - upper.Bottom - 1;
                bool hasFragment = Math.Min(upper.Bottom - upper.Top, lower.Bottom - lower.Top) + 1 < fragmentHeight;
                bool sameLine = (gap <= splitGlyphGap || (hasFragment && gap <= fragmentGap)) &&
                                lower.Bottom - upper.Top + 1 <= maxLineHeight;
                if (!sameLine)
                {
                    i++;
                    continue;
                }

                runs[i] = (upper.Top, lower.Bottom);
                runs.RemoveAt(i + 1);
            }

            // 3. 구간마다 글자가 있는 가로 범위를 구한다.
            foreach (var (top, bottom) in runs)
            {
                int left = int.MaxValue;
                int right = int.MinValue;
                for (int y = top; y <= bottom; y++)
                {
                    int end = RowEnd(y);
                    for (int x = margin; x < end; x++)
                    {
                        if (IsInk(x, y))
                        {
                            if (x < left) left = x;
                            if (x > right) right = x;
                        }
                    }
                }

                if (left <= right)
                {
                    rows.Add(new Rectangle(left, top, right - left + 1, bottom - top + 1));
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }

        return rows;
    }

    /// <summary>
    /// 배경색 픽셀의 위치를 찾는다. 프로필 사진을 덮은 자리는 배경색으로 칠해져 있으므로 그 안의 몇 점 중
    /// 가장 많이 나온 색을 배경으로 본다.
    /// </summary>
    private static int FindBackgroundOffset(byte[] pixels, int stride, int width, int height, double pxPerLogical)
    {
        int x = Math.Clamp((int)Math.Round(AvatarRight / 2 * pxPerLogical), 0, width - 1);
        int top = Math.Clamp((int)Math.Round((AvatarTop + 2) * pxPerLogical), 0, height - 1);
        int bottom = Math.Clamp(height - 1 - (int)Math.Round(2 * pxPerLogical), top, height - 1);

        const int Samples = 9;
        var offsets = new int[Samples];
        for (int i = 0; i < Samples; i++)
        {
            int y = top + (bottom - top) * i / (Samples - 1);
            offsets[i] = y * stride + x * 4;
        }

        return offsets
            .GroupBy(o => (pixels[o], pixels[o + 1], pixels[o + 2]))
            .OrderByDescending(g => g.Count())
            .First()
            .First();
    }

    /// <summary>
    /// PP-OCR 모델이 없을 때 쓴다. 카드 전체를 Windows OCR로 읽고, 읽은 단어를 픽셀로 찾은 줄에 나눠 담는다.
    /// Windows OCR이 주는 줄 묶음은 쓰지 않는다. 글자가 적은 팝업은 기울었다고 잘못 판단하는 일이 있어,
    /// 그때는 단어 위치를 그 각도만큼 되돌려 원래 이미지의 줄과 맞춘다.
    /// 단어가 하나도 담기지 않은 줄은 그 줄만 잘라 한 번 더 읽는다.
    /// </summary>
    private static List<string?> ReadRowsWithWindowsOcr(Bitmap image, IReadOnlyList<Rectangle> rows, Color background)
    {
        var texts = new List<string?>(new string?[rows.Count]);
        var ocr = _cachedWindowsOcr.Value;
        if (ocr is null || rows.Count == 0)
        {
            return texts;
        }

        var result = RunWindowsOcr(ocr, image);
        double radians = (result.TextAngle ?? 0) * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double centerX = image.Width / 2.0;
        double centerY = image.Height / 2.0;

        var wordsByRow = new List<(double X, string Text)>[rows.Count];
        foreach (var word in (result.Lines ?? []).SelectMany(l => l.Words))
        {
            double dx = word.BoundingRect.X + word.BoundingRect.Width / 2 - centerX;
            double dy = word.BoundingRect.Y + word.BoundingRect.Height / 2 - centerY;
            double x = centerX + dx * cos - dy * sin;
            double y = centerY + dx * sin + dy * cos;
            for (int i = 0; i < rows.Count; i++)
            {
                if (y >= rows[i].Top && y <= rows[i].Bottom && x >= rows[i].Left && x <= rows[i].Right)
                {
                    (wordsByRow[i] ??= []).Add((x, word.Text));
                    break;
                }
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            string? text = wordsByRow[i] is { } words
                ? string.Join(" ", words.OrderBy(w => w.X).Select(w => w.Text))
                : ReadRowCropWithWindowsOcr(ocr, image, rows[i], background);

            // ㄷㄷㄷ→CCC, ㅠㅠ→TT, ㅡㅡ→-- 같은 글리프 혼동은 PP-OCR 경로와 같게 되돌린다.
            texts[i] = text is null ? null : PaddleOcrLineRecognizer.NormalizeJamoConfusion(text);
        }

        return texts;
    }

    private static string? ReadRowCropWithWindowsOcr(Windows.Media.Ocr.OcrEngine ocr, Bitmap image, Rectangle row, Color background)
    {
        // Windows OCR은 가장자리에 붙은 글자를 놓치고, 너무 작은 이미지는 받지 않는다.
        int pad = Math.Max(24, row.Height / 2);
        int canvasWidth = Math.Max(96, row.Width + pad * 2);
        int canvasHeight = Math.Max(96, row.Height + pad * 2);
        using var canvas = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(background);
            g.DrawImage(
                image,
                new Rectangle(pad, (canvasHeight - row.Height) / 2, row.Width, row.Height),
                row,
                GraphicsUnit.Pixel);
        }

        var lines = RunWindowsOcr(ocr, canvas).Lines;
        string text = string.Join(" ", (lines ?? []).Select(l => l.Text.Trim()).Where(t => t.Length > 0));
        return text.Length == 0 ? null : text;
    }

    private static Windows.Media.Ocr.OcrResult RunWindowsOcr(Windows.Media.Ocr.OcrEngine ocr, Bitmap image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Bmp);
        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(ras))
        {
            writer.WriteBytes(ms.ToArray());
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        ras.Seek(0);

        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        using var softwareBitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
        return ocr.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
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
            Body: body ?? (sender is not null ? KakaoNotificationText.UnreadBody : KakaoNotificationText.MissingBody));
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

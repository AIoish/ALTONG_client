namespace Altong.Client.Services;

/// <summary>
/// 상단 노치와 화면 맨 위 띠의 크기·색·위치 규칙. 화면 객체 없이 검증할 수 있도록 분리한다.
/// 띠는 AppBar로 예약해 다른 창이 그 줄에 들어오지 않고, 노치는 띠 가운데에 붙는다.
/// </summary>
public static class TopNotchLayout
{
    /// <summary>띠 높이(DIP). 노치 높이와 같아 노치가 띠 안에 들어간다.</summary>
    public const double BandHeight = 30;
    public const double NotchHeight = BandHeight;
    /// <summary>호버·패널이 열린 동안 보이는 노치 윤곽선의 불투명도. 검은 띠에서 노치 범위만 알 수 있을 정도로 둔다.</summary>
    public const double OutlineOpacity = .55;
    /// <summary>
    /// 노치 위쪽(띠에 닿는 쪽) 전체 폭. 양옆은 짧은 오목 어깨에서 비스듬히 내려와 둥글게 바닥으로 이어진다.
    /// 팀 시안 이미지의 윤곽을 측정해 한쪽당 3차 베지어 2개로 맞춘 값이다(오차 약 0.3px).
    /// 모양은 NotificationDockWindow.xaml의 TopNotchSurface 경로와 같다.
    /// </summary>
    public const double NotchSurfaceWidth = 360.2;
    /// <summary>노치 아래쪽 평평한 부분의 폭.</summary>
    public const double NotchBottomWidth = 235.4;
    /// <summary>노치 창 크기. 노치 아래 투명 영역은 안내 말풍선 자리이며 클릭은 아래 앱으로 전달된다.</summary>
    public const double WindowWidth = 420;
    public const double WindowHeight = 168;

    public const string BlackBand = "#000000";
    /// <summary>바탕화면 그림 위쪽 중 평균색을 낼 높이의 비율.</summary>
    public const double WallpaperSampleFraction = .06;

    /// <summary>띠 색. 배경색 동기화는 바탕화면 평균색을 쓰고, 그 색을 얻지 못하면 검정으로 둔다.</summary>
    public static string BandColor(TopBandStyle style, string? wallpaperColor) =>
        style == TopBandStyle.Wallpaper && !string.IsNullOrEmpty(wallpaperColor) ? wallpaperColor : BlackBand;

    /// <summary>
    /// BGRA 픽셀 배열에서 위쪽 <paramref name="fraction"/> 높이의 평균색(#RRGGBB). 띠가 덮는 바탕화면 윗부분의 색과 맞춘다.
    /// </summary>
    public static string AverageTopColor(byte[] bgra, int width, int height, int stride, double fraction = WallpaperSampleFraction)
    {
        if (width <= 0 || height <= 0) return BlackBand;
        int rows = Math.Clamp((int)Math.Round(height * fraction), 1, height);
        long blue = 0, green = 0, red = 0;
        for (int y = 0; y < rows; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                blue += bgra[i];
                green += bgra[i + 1];
                red += bgra[i + 2];
            }
        }
        long count = (long)rows * width;
        return $"#{red / count:X2}{green / count:X2}{blue / count:X2}";
    }


    /// <summary>띠 높이를 모니터 배율에 맞춘 물리 픽셀로 바꾼다.</summary>
    public static int BandHeightPixels(double dpiScaleY) => (int)Math.Round(BandHeight * Math.Max(1, dpiScaleY));

    /// <summary>노치 창의 왼쪽 위치. 화면 가운데에서 좌우로 옮긴 거리를 더하고 화면 안으로 제한한다.</summary>
    public static double WindowLeft(double screenLeft, double screenWidth, double windowWidth, double centerOffset)
    {
        double centered = screenLeft + (screenWidth - windowWidth) / 2 + centerOffset;
        return Math.Clamp(centered, screenLeft, Math.Max(screenLeft, screenLeft + screenWidth - windowWidth));
    }

    /// <summary>창 위치를 화면 가운데에서 옮긴 거리로 바꾼다. 작업 영역이 바뀌어도 같은 거리를 유지한다.</summary>
    public static double CenterOffset(double windowLeft, double screenLeft, double screenWidth, double windowWidth) =>
        windowLeft - (screenLeft + (screenWidth - windowWidth) / 2);

    /// <summary>알림 목록 패널의 폭. 패널이 열리면 노치 바닥이 이 폭까지 넓어져 패널과 한 덩어리로 이어진다.</summary>
    public const double PanelWidth = 316;
    /// <summary>알림 패널 창의 투명 여백. 패널 카드는 창 안쪽으로 이만큼 들어가 있다.</summary>
    public const double FlyoutInset = 8;
    /// <summary>패널이 열릴 때 노치 바닥이 패널 폭까지 넓어지는 시간.</summary>
    public const int OpenMorphMilliseconds = 160;
    /// <summary>패널을 펼치기 시작하는 시점. 노치가 거의 다 넓어진 뒤라 좁은 노치 아래 넓은 패널이 보이지 않는다.</summary>
    public const int PanelRevealDelayMilliseconds = 120;
    /// <summary>패널이 위에서 아래로 펼쳐지는 시간.</summary>
    public const int PanelRevealMilliseconds = 260;
    /// <summary>노치와 패널 사이 이음매가 보이지 않도록 패널을 노치 바닥에 겹치는 양.</summary>
    public const double PanelOverlap = 1;

    /// <summary>
    /// 패널 창의 왼쪽 위치. 목록 패널(창 오른쪽 끝)의 가운데를 노치 가운데에 맞춘다. 상세가 열리면 왼쪽으로 넓어진다.
    /// 화면 밖으로 나가면 작업 영역 안으로 민다.
    /// </summary>
    public static double PanelFlyoutLeft(double notchCenterX, double flyoutWidth, double workLeft, double workRight) =>
        Math.Clamp(notchCenterX - (flyoutWidth - FlyoutInset - PanelWidth / 2),
            workLeft, Math.Max(workLeft, workRight - flyoutWidth));

    /// <summary>닫힌 노치의 왼쪽 옆선 제어점(노치 왼쪽 위 기준). 3차 베지어 두 개: 0→3, 3→6.</summary>
    private static readonly (double X, double Y)[] ClosedLeftSide =
    {
        (0, 0), (3.4, 0), (25.7, 5.3), (31.4, 14.5), (34.2, 19), (47.2, 30), (62.4, 30),
    };

    /// <summary>
    /// 패널이 열린 노치의 왼쪽 옆선 제어점. 띠에서 수평으로 시작해 아래에서 수직으로 끝나는 오목한 4분의 1 타원
    /// (가로 22.1, 세로 30)을 베지어 두 개로 나눈 것이다. 끝이 수직이라 패널 옆면과 꺾임 없이 한 곡선으로 이어진다.
    /// </summary>
    private static readonly (double X, double Y)[] OpenLeftSide =
    {
        (0, 0), (6.1, 0), (11.63, 3.35), (15.63, 8.78), (19.63, 14.2), (22.1, 21.7), (22.1, 30),
    };

    /// <summary>
    /// 노치 왼쪽 옆선의 제어점. <paramref name="progress"/> 0은 닫힌 노치(바닥이 수평으로 끝나는 S자),
    /// 1은 바닥이 패널 폭까지 넓어지고 옆선이 수직으로 끝나 패널 옆면으로 이어지는 노치다. 그 사이는 제어점을 선형으로 섞는다.
    /// 오른쪽 옆선은 노치 가운데를 기준으로 좌우를 뒤집어 쓴다.
    /// </summary>
    public static (double X, double Y)[] NotchLeftSide(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return ClosedLeftSide.Zip(OpenLeftSide, (closed, open) =>
            (closed.X + (open.X - closed.X) * t, closed.Y + (open.Y - closed.Y) * t)).ToArray();
    }

    /// <summary>
    /// Windows는 바탕화면 보기(Win+D)에서도 전체화면 알림을 보낼 수 있다.
    /// 앞 창이 셸 바탕화면·작업표시줄이면 전체화면으로 보지 않는다.
    /// </summary>
    public static bool IsShellWindowClass(string? className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static readonly HashSet<string> SystemOverlayProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool", "ScreenClippingHost", "ScreenSketch", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "SearchApp", "TextInputHost", "GameBar",
    };

    /// <summary>
    /// 화면 캡처(Win+Shift+S)·시작 메뉴·게임 바처럼 화면 전체를 잠깐 덮는 Windows 오버레이인지.
    /// Windows가 이런 오버레이도 전체화면 앱으로 알리므로, 노치를 숨기지 않도록 구분한다. 확장자는 무시한다.
    /// </summary>
    public static bool IsSystemOverlayProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return SystemOverlayProcesses.Contains(name);
    }

    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsChild = 0x40000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int HtCaption = 2;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;

    /// <summary>띠 아래로 옮길 대상인 일반 앱 창인지. 도구 창·맨 위 고정 창·자식 창·테두리 없는 팝업은 건드리지 않는다.</summary>
    public static bool IsMovableAppWindow(int style, int exStyle) =>
        (style & WsChild) == 0
        && ((style & WsCaption) == WsCaption || (style & WsThickFrame) != 0)
        && (exStyle & WsExToolWindow) == 0
        && (exStyle & WsExTopmost) == 0;

    /// <summary>창이 모니터 전체를 덮는 전체화면인지. 전체화면 창은 노치를 숨기는 대상이라 옮기지 않는다.</summary>
    public static bool CoversScreen(PixelRect window, PixelRect screen) =>
        window.Left <= screen.Left && window.Top <= screen.Top
        && window.Right >= screen.Right && window.Bottom >= screen.Bottom;

    /// <summary>
    /// 끌기를 시작한 지점이 창 윗변을 띠 쪽으로 움직이는 곳(제목 표시줄, 위쪽 테두리·모서리)인지.
    /// 창이 응답하지 않아 판정 값을 얻지 못하면 창 윗부분을 잡았는지로 대신 판단한다.
    /// </summary>
    public static bool ConstrainsTopEdge(int hitTest, int cursorY, int visibleTop, int captionFallbackPixels) =>
        hitTest is HtCaption or HtTop or HtTopLeft or HtTopRight
        || (hitTest == 0 && cursorY - visibleTop >= 0 && cursorY - visibleTop <= captionFallbackPixels);

    /// <summary>
    /// 창을 끄는 동안 커서가 올라갈 수 있는 가장 위 y(물리 픽셀). 커서는 띠 아랫선에서 멈춘다.
    /// 커서가 작업 영역 맨 위까지 닿아야 Windows 11의 스냅 레이아웃 막대가 열리므로, 끄는 동안에는
    /// 제목 표시줄을 잡은 높이만큼 창이 띠 밑으로 들어갈 수 있다. 놓으면 띠 아래로 내린다.
    /// 커서가 이미 띠 안에 있으면 지금 위치보다 더 올라가지 못하게만 한다.
    /// </summary>
    public static int DragCursorMinY(int cursorY, int bandBottom) => Math.Min(cursorY, bandBottom);

    /// <summary>
    /// 띠에 걸친 창을 띠 바로 아래로 내린 위치(물리 픽셀). 걸치지 않았으면 null.
    /// 보이는 테두리(<paramref name="visible"/>) 기준으로 내리고, 아래가 작업 영역을 넘으면 높이를 줄인다.
    /// 높이는 <paramref name="minHeight"/>보다 작게 줄이지 않는다.
    /// </summary>
    public static PixelRect? PushBelowBand(PixelRect frame, PixelRect visible, int bandBottom, int workBottom, int minHeight = 120)
    {
        if (visible.Top >= bandBottom) return null;
        int shift = bandBottom - visible.Top;
        int height = frame.Height;
        int overflow = visible.Bottom + shift - workBottom;
        if (overflow > 0) height = Math.Max(frame.Height - overflow, Math.Min(frame.Height, minHeight));
        return new PixelRect(frame.Left, frame.Top + shift, frame.Right, frame.Top + shift + height);
    }
}

/// <summary>물리 픽셀 사각형. Right·Bottom은 포함하지 않는 끝 좌표다(Win32 RECT와 같다).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

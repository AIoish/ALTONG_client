namespace Altong.Client.Services;

/// <summary>
/// 상단 노치와 화면 맨 위 띠의 크기·색·위치 규칙. 화면 객체 없이 검증할 수 있도록 분리한다.
/// 띠는 AppBar로 예약해 다른 창이 그 줄에 들어오지 않고, 노치는 띠 가운데에 붙는다.
/// </summary>
public static class TopNotchLayout
{
    /// <summary>
    /// [임시] 노치·띠 크기 비교용 후보. 패널 하단 버튼으로 실행 중에 바꿔 보고, 크기를 정하면 이 목록과 버튼을 지운다.
    /// 높이를 줄이면 옆선 곡선도 같은 비율로 줄고, 폭은 후보마다 정한다(마지막 후보는 높이만 줄이고 폭은 유지).
    /// </summary>
    public static readonly NotchSize[] SizeOptions =
    {
        new("30", 30, 360.2),
        new("24", 24, 288.16),
        new("20", 20, 240.13),
        new("16", 16, 192.11),
        new("12", 12, 144.08),
        new("8", 8, 96.05),
        new("20W", 20, 360.2),
    };

    /// <summary>[임시] 지금 쓰는 노치 크기. 기본은 30px(시안 그대로)이다.</summary>
    public static NotchSize Size { get; private set; } = SizeOptions[0];

    /// <summary>[임시] 노치 크기를 바꾼다. 창은 <see cref="NotificationDockWindow"/>가 다시 배치한다.</summary>
    public static void SetSize(NotchSize size) => Size = size;

    /// <summary>시안(30px) 대비 높이 비율. 옆선 곡선·렌즈를 이 비율로 줄인다.</summary>
    public static double HeightScale => Size.Height / 30;

    /// <summary>띠 높이(DIP). 노치 높이와 같아 노치가 띠 안에 들어간다.</summary>
    public static double BandHeight => Size.Height;
    public static double NotchHeight => BandHeight;
    /// <summary>호버·패널이 열린 동안 보이는 노치 윤곽선의 불투명도. 검은 띠에서 노치 범위만 알 수 있을 정도로 둔다.</summary>
    public const double OutlineOpacity = .55;
    /// <summary>
    /// 노치 위쪽(띠에 닿는 쪽) 전체 폭. 양옆은 짧은 오목 어깨에서 비스듬히 내려와 둥글게 바닥으로 이어진다.
    /// 팀 시안 이미지의 윤곽을 측정해 한쪽당 3차 베지어 2개로 맞춘 값이다(오차 약 0.3px).
    /// 모양은 NotificationDockWindow.xaml의 NotchFace 경로와 같다.
    /// </summary>
    public static double NotchSurfaceWidth => Size.Width;
    /// <summary>노치 아래쪽 평평한 부분의 폭.</summary>
    public static double NotchBottomWidth => NotchSurfaceWidth - 2 * ClosedLeftSide[^1].X * HeightScale;
    /// <summary>
    /// 노치 가운데를 기준으로 한 닫힌 노치·안내 말풍선 영역의 폭과, 패널이 닫혀 있을 때의 창 높이.
    /// 노치 아래 투명 영역은 안내 말풍선(그림자 포함) 자리이며 클릭은 아래 앱으로 전달된다.
    /// 실제 창 폭은 <see cref="WindowLeftExtent"/> + <see cref="WindowRightExtent"/>이고 높이는 <see cref="WindowHeightFor"/>다.
    /// </summary>
    public const double WindowWidth = 420;
    public const double WindowHeight = 190;
    /// <summary>안내 말풍선 크기. 노치 바로 아래 가운데에 둔다.</summary>
    public const double ReminderWidth = 344;
    public const double ReminderHeight = 122;
    public static double ReminderTop => NotchHeight + 4;

    public const string BlackBand = "#000000";
    /// <summary>
    /// 바탕화면 띠. 거의 투명하게 칠해 실제 바탕화면이 그대로 비쳐 보인다(맞춤 방식·슬라이드 쇼·움직이는 배경도 그대로).
    /// 완전히 투명하면 클릭이 아래 바탕화면으로 빠지므로 알파 1을 남겨 띠가 클릭을 받게 한다.
    /// </summary>
    public const string WallpaperBand = "#01000000";

    /// <summary>띠 채움색. 검정은 불투명 검정, 바탕화면은 바탕화면이 비치는 거의 투명한 색.</summary>
    public static string BandFill(TopBandStyle style) => style == TopBandStyle.Wallpaper ? WallpaperBand : BlackBand;

    /// <summary>띠 높이를 모니터 배율에 맞춘 물리 픽셀로 바꾼다.</summary>
    public static int BandHeightPixels(double dpiScaleY) => (int)Math.Round(BandHeight * Math.Max(1, dpiScaleY));

    /// <summary>
    /// 노치 가운데의 화면 x 좌표. 화면 가운데에서 좌우로 옮긴 거리를 더하고, 노치가 화면 밖으로 나가지 않게 제한한다.
    /// 상세 패널은 왼쪽에 자리가 없으면 오른쪽에 붙으므로(<see cref="DetailOnRight"/>) 노치는 화면 끝까지 옮길 수 있다.
    /// </summary>
    public static double NotchCenter(double screenLeft, double screenWidth, double centerOffset)
    {
        double half = NotchSurfaceWidth / 2;
        double center = screenLeft + screenWidth / 2 + centerOffset;
        return Math.Clamp(center, screenLeft + half, Math.Max(screenLeft + half, screenLeft + screenWidth - half));
    }

    /// <summary>노치 가운데 위치를 화면 가운데에서 옮긴 거리로 바꾼다. 작업 영역이 바뀌어도 같은 거리를 유지한다.</summary>
    public static double CenterOffset(double notchCenter, double screenLeft, double screenWidth) =>
        notchCenter - (screenLeft + screenWidth / 2);

    /// <summary>누른 뒤 이만큼 움직였으면 클릭이 아니라 끌기로 본다.</summary>
    public static bool IsDrag(double horizontalDelta, double verticalDelta, double horizontalThreshold, double verticalThreshold) =>
        Math.Abs(horizontalDelta) >= horizontalThreshold || Math.Abs(verticalDelta) >= verticalThreshold;

    /// <summary>알림 목록 영역의 폭. 패널이 열리면 노치 바닥이 이 폭까지 넓어져 패널과 한 덩어리로 이어진다.</summary>
    public const double PanelWidth = 316;
    /// <summary>상세 영역의 폭. 목록 왼쪽에 붙는다.</summary>
    public const double DetailWidth = 372;
    /// <summary>집중을 켤 때 흐르는 일렁임 색(민트: 청록·라벤더·청록). 노치 표시등과 같은 계열이다.</summary>
    public static readonly (string Left, string Middle, string Right) ActivationRipple = ("#72B5A6", "#A99BE0", "#72B5A6");
    /// <summary>
    /// 통과한 알림이 왔을 때 흐르는 일렁임 색(선셋: 코랄·핑크·호박색). 알통에서 따뜻한 색은 긴급·중요 알림을 뜻하므로
    /// 집중 시작(민트)과 색만으로 구분된다.
    /// </summary>
    public static readonly (string Left, string Middle, string Right) AlertRipple = ("#FF8B6B", "#FF6FA8", "#FFC36B");
    /// <summary>켜짐 일렁임(천천히)과 알림 일렁임(빠르게)의 길이.</summary>
    public const int ActivationRippleMilliseconds = 3500;
    public const int AlertRippleMilliseconds = 1800;
    /// <summary>
    /// 새 알림이 왔을 때 선셋 일렁임을 어떻게 할지 정한다. 패널을 열어 읽는 중이거나 전체화면이거나 노치가 숨었으면 생략한다.
    /// 앞선 일렁임(켜짐 민트 포함)이 아직 흐르는 중이면 끊지 않고, 끝난 뒤 한 번 더 흘리도록 미룬다(연달아 온 알림이 묻히지 않게).
    /// <paramref name="rippleEndsAt"/>은 지금 흐르는 일렁임이 끝나는 시각이다.
    /// </summary>
    public static AlertRippleDecision DecideAlertRipple(DateTime now, DateTime rippleEndsAt, bool notchVisible, bool panelOpen, bool fullscreen)
    {
        if (!notchVisible || panelOpen || fullscreen) return AlertRippleDecision.Skip;
        return now >= rippleEndsAt ? AlertRippleDecision.Play : AlertRippleDecision.Queue;
    }

    /// <summary>알림이 들어올 때마다 렌즈 포인트 뒤에서 한 번 번지는 빛. 0.7초 동안 커지며 사라진다.</summary>
    public const int LensFlashMilliseconds = 700;
    public const double LensFlashDiameter = 16;
    public const double LensFlashMaxScale = 1.6;

    /// <summary>렌즈 포인트 지름. 미확인 알림이 있으면 커진다.</summary>
    public const double LensDiameter = 5;
    public const double LensAlertDiameter = 6;
    /// <summary>렌즈 포인트 색(광택·몸체·가장자리). 평소 은빛, 미확인 알림이 있으면 유리 뒤 LED처럼 채도를 낮춘 호박빛 주황.
    /// 가장자리를 어둡게 두어 검은 노치에 스며들게 한다.</summary>
    public static readonly (string Highlight, string Body, string Edge) LensQuiet = ("#FFFFFF", "#C4C8D0", "#6E737C");
    public static readonly (string Highlight, string Body, string Edge) LensAlert = ("#F2D3C4", "#D27C60", "#5A3226");

    /// <summary>미확인 알림 여부에 따른 렌즈 색과 크기 배율. 색과 크기를 함께 바꿔 색만으로 구분하지 않는다.</summary>
    public static ((string Highlight, string Body, string Edge) Colors, double Scale) Lens(bool hasUnread) =>
        hasUnread ? (LensAlert, LensAlertDiameter / LensDiameter) : (LensQuiet, 1);

    /// <summary>패널 최대 높이와 아래쪽 모서리 반지름.</summary>
    public const double PanelMaxHeight = 480;
    public const double PanelCornerRadius = 16;
    /// <summary>패널 아래쪽과 작업 영역 끝 사이 최소 간격.</summary>
    public const double PanelBottomMargin = 8;

    /// <summary>패널을 여는 전체 시간과 닫는 시간. 노치가 넓어지는 단계와 패널이 내려오는 단계를 하나의 진행값으로 묶는다.</summary>
    public const int OpenMilliseconds = 400;
    public const int CloseMilliseconds = 260;
    /// <summary>열기 진행값 중 노치가 넓어지는 구간(0~이 값).</summary>
    public const double NotchMorphSpan = .42;
    /// <summary>열기 진행값 중 패널이 내려오기 시작하는 지점. 노치가 거의 다 넓어진 뒤다.</summary>
    public const double PanelRevealStart = .32;

    /// <summary>열기 진행값(0~1)에서 노치가 넓어진 정도. 천천히 시작해 천천히 끝난다.</summary>
    public static double NotchMorph(double openProgress)
    {
        double t = Math.Clamp(openProgress / NotchMorphSpan, 0, 1);
        return t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>열기 진행값(0~1)에서 패널이 내려온 비율. 처음에 빠르고 끝에서 길게 감속한다.</summary>
    public static double PanelReveal(double openProgress)
    {
        double t = Math.Clamp((openProgress - PanelRevealStart) / (1 - PanelRevealStart), 0, 1);
        return 1 - Math.Pow(1 - t, 4);
    }

    /// <summary>
    /// 노치 창이 노치 가운데에서 왼쪽·오른쪽으로 차지하는 폭. 상세(목록 왼쪽)까지 담을 수 있게 왼쪽을 늘 넉넉히 둔다.
    /// 창의 왼쪽 위 모서리가 움직이지 않아, 패널을 열고 닫거나 상세를 열 때 창 크기가 바뀌어도
    /// 이전 화면이 어긋난 위치에 잠깐 찍히지 않는다. 투명한 부분의 클릭은 아래 앱으로 전달된다.
    /// </summary>
    public const double WindowLeftExtent = PanelWidth / 2 + DetailWidth;
    /// <summary>상세 패널이 목록 오른쪽에 붙을 때도 담도록 오른쪽도 왼쪽만큼 둔다. 창 가운데가 늘 노치 가운데라 배율이 다른 옆 모니터에 걸려도 주 모니터 배율을 따른다.</summary>
    public const double WindowRightExtent = WindowLeftExtent;

    /// <summary>
    /// 상세 패널을 목록 오른쪽에 붙일지. 기본은 왼쪽이고, 노치를 화면 왼쪽 끝으로 옮겨 왼쪽에 상세가 들어갈 자리가 없을 때만 오른쪽에 붙인다.
    /// </summary>
    public static bool DetailOnRight(double notchCenter, double screenLeft, double screenWidth) =>
        notchCenter - PanelWidth / 2 - DetailWidth < screenLeft
        && notchCenter + PanelWidth / 2 + DetailWidth <= screenLeft + screenWidth;

    /// <summary>
    /// 노치 창 높이. 패널이 보이는 동안에만 패널을 담을 만큼 아래로 늘린다(위쪽은 그대로).
    /// 투명 창은 크기에 비례해 그리는 비용이 들기 때문에 닫혀 있을 때는 안내 말풍선 자리만 둔다.
    /// </summary>
    public static double WindowHeightFor(bool panelShown, double panelHeight) =>
        panelShown ? Math.Max(WindowHeight, NotchHeight + panelHeight + 2) : WindowHeight;

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
        double scale = HeightScale;
        // 열린 옆선은 패널 옆면(노치 가운데에서 패널 폭의 절반)에서 수직으로 끝나도록 옮긴다.
        // 노치가 패널보다 좁으면 위쪽 시작점도 노치 밖으로 넓어진다.
        double openEnd = (NotchSurfaceWidth - PanelWidth) / 2;
        double openShift = openEnd - OpenLeftSide[^1].X * scale;
        return ClosedLeftSide.Zip(OpenLeftSide, (closed, open) =>
        {
            double cx = closed.X * scale, cy = closed.Y * scale;
            double ox = open.X * scale + openShift, oy = open.Y * scale;
            return (cx + (ox - cx) * t, cy + (oy - cy) * t);
        }).ToArray();
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

/// <summary>새 알림 일렁임 결정: 생략, 지금 재생, 지금 것이 끝난 뒤 한 번 더.</summary>
public enum AlertRippleDecision { Skip, Play, Queue }

/// <summary>[임시] 노치·띠 크기 후보. <see cref="Label"/>은 비교 버튼 글자다.</summary>
public readonly record struct NotchSize(string Label, double Height, double Width);

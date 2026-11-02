using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class TopNotchLayoutTests
{
    [TestMethod]
    [DataRow(TopBandStyle.Black, "#1A2B3C", TopNotchLayout.BlackBand)]
    [DataRow(TopBandStyle.Black, null, TopNotchLayout.BlackBand)]
    [DataRow(TopBandStyle.Wallpaper, "#1A2B3C", "#1A2B3C")]
    [DataRow(TopBandStyle.Wallpaper, null, TopNotchLayout.BlackBand)]
    [DataRow(TopBandStyle.Wallpaper, "", TopNotchLayout.BlackBand)]
    public void BandColor_UsesWallpaperOnlyWhenSyncedAndAvailable(TopBandStyle style, string? wallpaper, string expected)
    {
        Assert.AreEqual(expected, TopNotchLayout.BandColor(style, wallpaper));
    }

    [TestMethod]
    public void AverageTopColor_AveragesOnlyTheTopRows()
    {
        // 4×10 그림: 위 1줄(10%의 6% → 최소 1줄)은 빨강·파랑 반반, 나머지는 초록.
        const int width = 4, height = 10, stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                if (y == 0 && x < 2) { pixels[i + 2] = 200; }      // 빨강
                else if (y == 0) { pixels[i] = 100; }               // 파랑
                else { pixels[i + 1] = 255; }                       // 초록
                pixels[i + 3] = 255;
            }
        Assert.AreEqual("#640032", TopNotchLayout.AverageTopColor(pixels, width, height, stride));
    }

    [TestMethod]
    public void AverageTopColor_FallsBackToBlackForEmptyImages()
    {
        Assert.AreEqual(TopNotchLayout.BlackBand, TopNotchLayout.AverageTopColor(Array.Empty<byte>(), 0, 0, 0));
    }

    [TestMethod]
    [DataRow("ScreenClippingHost", true)]
    [DataRow("SnippingTool.exe", true)]
    [DataRow("screensketch", true)]
    [DataRow("GameBar", true)]
    [DataRow("chrome", false)]
    [DataRow("POWERPNT", false)]
    [DataRow(null, false)]
    public void SystemOverlays_AreNotTreatedAsFullscreenApps(string? processName, bool expected)
    {
        Assert.AreEqual(expected, TopNotchLayout.IsSystemOverlayProcess(processName));
    }

    [TestMethod]
    [DataRow(1.0, 30)]
    [DataRow(1.25, 38)]
    [DataRow(1.5, 45)]
    [DataRow(2.0, 60)]
    public void BandHeightPixels_ScalesWithDpi(double scale, int expected)
    {
        Assert.AreEqual(expected, TopNotchLayout.BandHeightPixels(scale));
    }

    [TestMethod]
    public void WindowLeft_CentersOnScreenByDefault()
    {
        Assert.AreEqual((1920 - 360) / 2d, TopNotchLayout.WindowLeft(0, 1920, 360, 0));
    }

    [TestMethod]
    public void WindowLeft_KeepsTheWindowOnScreen()
    {
        Assert.AreEqual(0d, TopNotchLayout.WindowLeft(0, 1920, 360, -5000));
        Assert.AreEqual(1920d - 360, TopNotchLayout.WindowLeft(0, 1920, 360, 5000));
    }

    [TestMethod]
    public void CenterOffset_RoundTripsWithWindowLeft()
    {
        double left = TopNotchLayout.WindowLeft(0, 1920, 360, 240);
        Assert.AreEqual(240d, TopNotchLayout.CenterOffset(left, 0, 1920, 360), .001);
    }

    [TestMethod]
    public void PanelFlyoutLeft_CentersTheListPanelUnderTheNotch()
    {
        // 목록만 있을 때: 창 332 = 여백 8 + 목록 316 + 여백 8. 목록 가운데가 노치 가운데(960)에 온다.
        Assert.AreEqual(960d - 166, TopNotchLayout.PanelFlyoutLeft(960, 332, 0, 1920));
        // 상세가 열리면 창이 왼쪽으로 넓어져도 목록 가운데는 그대로다.
        Assert.AreEqual(960d - (704 - 8 - 158), TopNotchLayout.PanelFlyoutLeft(960, 704, 0, 1920));
        // 화면 밖으로 나가면 작업 영역 안으로 민다.
        Assert.AreEqual(0d, TopNotchLayout.PanelFlyoutLeft(100, 704, 0, 1920));
        Assert.AreEqual(1920d - 332, TopNotchLayout.PanelFlyoutLeft(1910, 332, 0, 1920));
    }

    [TestMethod]
    public void PanelWidth_MatchesTheFlyoutListPanel()
    {
        Assert.AreEqual(NotificationFlyoutWindow.NotchListWidth - 2 * TopNotchLayout.FlyoutInset, TopNotchLayout.PanelWidth);
    }

    [TestMethod]
    public void NotchLeftSide_ClosedMatchesTheMockAndOpenMatchesThePanel()
    {
        var closed = TopNotchLayout.NotchLeftSide(0);
        var open = TopNotchLayout.NotchLeftSide(1);
        // 닫힘: 바닥 폭은 시안과 같은 235.4.
        Assert.AreEqual(TopNotchLayout.NotchBottomWidth, TopNotchLayout.NotchSurfaceWidth - 2 * closed[^1].X, .01);
        // 열림: 바닥 폭이 목록 패널 폭과 같아 패널 옆면으로 곧게 이어진다.
        Assert.AreEqual(TopNotchLayout.PanelWidth, TopNotchLayout.NotchSurfaceWidth - 2 * open[^1].X, .01);
        // 높이와 위쪽 시작점은 그대로다.
        Assert.AreEqual(0d, open[0].X);
        Assert.AreEqual(TopNotchLayout.NotchHeight, open[^1].Y);
        // 닫힘은 바닥에서 수평으로 끝나고(마지막 두 점의 y가 같음), 열림은 수직으로 끝나 패널 옆면과 꺾임 없이 이어진다.
        Assert.AreEqual(closed[^2].Y, closed[^1].Y);
        Assert.AreEqual(open[^2].X, open[^1].X, .001);
        // 위쪽 시작은 두 모양 모두 띠와 수평으로 만난다.
        Assert.AreEqual(closed[0].Y, closed[1].Y);
        Assert.AreEqual(open[0].Y, open[1].Y);
        // 중간 값은 두 모양 사이에 있다.
        var half = TopNotchLayout.NotchLeftSide(.5);
        Assert.IsTrue(half[^1].X < closed[^1].X && half[^1].X > open[^1].X);
    }

    [TestMethod]
    [DataRow("Progman", true)]
    [DataRow("WorkerW", true)]
    [DataRow("Shell_TrayWnd", true)]
    [DataRow("Shell_SecondaryTrayWnd", true)]
    [DataRow("Chrome_WidgetWin_1", false)]
    [DataRow("screenClass", false)]
    [DataRow(null, false)]
    public void ShellWindowClasses_AreNotTreatedAsFullscreenApps(string? className, bool expected)
    {
        Assert.AreEqual(expected, TopNotchLayout.IsShellWindowClass(className));
    }

    [TestMethod]
    public void PushBelowBand_MovesOverlappingWindowJustBelowTheBand()
    {
        // 보이지 않는 테두리 7px이 있는 창. 보이는 윗변이 띠 아랫선(30)에 오도록 내린다.
        var frame = new PixelRect(93, 3, 1007, 607);
        var visible = new PixelRect(100, 10, 1000, 600);
        var moved = TopNotchLayout.PushBelowBand(frame, visible, 30, 1040);
        Assert.AreEqual(new PixelRect(93, 23, 1007, 627), moved);
    }

    [TestMethod]
    public void PushBelowBand_LeavesWindowsBelowTheBandAlone()
    {
        var rect = new PixelRect(100, 30, 1000, 600);
        Assert.IsNull(TopNotchLayout.PushBelowBand(rect, rect, 30, 1040));
    }

    [TestMethod]
    public void PushBelowBand_ShrinksWindowThatWouldPassTheTaskbar()
    {
        // 왼쪽 절반에 스냅한 창처럼 작업 영역을 꽉 채운 창.
        var rect = new PixelRect(0, 0, 960, 1040);
        var moved = TopNotchLayout.PushBelowBand(rect, rect, 30, 1040);
        Assert.AreEqual(new PixelRect(0, 30, 960, 1040), moved);
    }

    [TestMethod]
    public void PushBelowBand_KeepsAMinimumHeight()
    {
        var rect = new PixelRect(0, 0, 400, 300);
        var moved = TopNotchLayout.PushBelowBand(rect, rect, 30, 100, minHeight: 120);
        Assert.AreEqual(120, moved!.Value.Height);
        Assert.AreEqual(30, moved.Value.Top);
    }

    [TestMethod]
    public void DragCursorMinY_StopsTheCursorAtTheBandLine()
    {
        // 제목 표시줄을 잡고 끌든 위쪽 테두리를 잡고 줄이든, 커서는 띠 아랫선(30)에서 멈춘다.
        // 커서가 작업 영역 맨 위에 닿아야 Windows 11 스냅 레이아웃 막대를 쓸 수 있다.
        Assert.AreEqual(30, TopNotchLayout.DragCursorMinY(215, 30));
        Assert.AreEqual(30, TopNotchLayout.DragCursorMinY(400, 30));
    }

    [TestMethod]
    public void DragCursorMinY_DoesNotJumpWhenTheCursorIsAlreadyInTheBand()
    {
        Assert.AreEqual(20, TopNotchLayout.DragCursorMinY(20, 30));
    }

    [TestMethod]
    [DataRow(2, 500, 100, true)]    // 제목 표시줄
    [DataRow(12, 100, 100, true)]   // 위쪽 테두리
    [DataRow(13, 100, 100, true)]   // 왼쪽 위 모서리
    [DataRow(14, 100, 100, true)]   // 오른쪽 위 모서리
    [DataRow(15, 700, 100, false)]  // 아래쪽 테두리
    [DataRow(10, 300, 100, false)]  // 왼쪽 테두리
    [DataRow(0, 120, 100, true)]    // 응답 없음, 창 윗부분
    [DataRow(0, 400, 100, false)]   // 응답 없음, 창 아랫부분
    public void ConstrainsTopEdge_OnlyForCaptionAndTopBorder(int hit, int cursorY, int visibleTop, bool expected)
    {
        Assert.AreEqual(expected, TopNotchLayout.ConstrainsTopEdge(hit, cursorY, visibleTop, 48));
    }

    [TestMethod]
    public void IsMovableAppWindow_SkipsToolTopmostChildAndBorderlessWindows()
    {
        const int overlapped = 0x00C00000 | 0x00040000;
        Assert.IsTrue(TopNotchLayout.IsMovableAppWindow(overlapped, 0));
        Assert.IsTrue(TopNotchLayout.IsMovableAppWindow(0x00040000, 0));
        Assert.IsFalse(TopNotchLayout.IsMovableAppWindow(overlapped, 0x00000080));
        Assert.IsFalse(TopNotchLayout.IsMovableAppWindow(overlapped, 0x00000008));
        Assert.IsFalse(TopNotchLayout.IsMovableAppWindow(overlapped | 0x40000000, 0));
        Assert.IsFalse(TopNotchLayout.IsMovableAppWindow(unchecked((int)0x80000000), 0));
    }

    [TestMethod]
    public void CoversScreen_DetectsFullscreenWindows()
    {
        var screen = new PixelRect(0, 0, 1920, 1080);
        Assert.IsTrue(TopNotchLayout.CoversScreen(screen, screen));
        Assert.IsTrue(TopNotchLayout.CoversScreen(new PixelRect(-8, -8, 1928, 1088), screen));
        Assert.IsFalse(TopNotchLayout.CoversScreen(new PixelRect(0, 30, 1920, 1040), screen));
    }

    [TestMethod]
    public void NotchFitsInsideTheBand()
    {
        Assert.AreEqual(TopNotchLayout.BandHeight, TopNotchLayout.NotchHeight);
        Assert.AreEqual(360.2, TopNotchLayout.NotchSurfaceWidth, .001);
        Assert.IsTrue(TopNotchLayout.NotchBottomWidth < TopNotchLayout.NotchSurfaceWidth);
        Assert.IsTrue(TopNotchLayout.NotchSurfaceWidth <= TopNotchLayout.WindowWidth);
        // 패널은 노치가 거의 다 넓어진 뒤(넓어지는 시간 안쪽) 펼치기 시작한다.
        Assert.IsTrue(TopNotchLayout.PanelRevealDelayMilliseconds < TopNotchLayout.OpenMorphMilliseconds);
        Assert.IsTrue(TopNotchLayout.PanelRevealDelayMilliseconds >= TopNotchLayout.OpenMorphMilliseconds * .6);
        Assert.IsTrue(TopNotchLayout.NotchSurfaceWidth < TopNotchLayout.WindowWidth);
    }
}

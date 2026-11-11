using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class TopNotchLayoutTests
{
    [TestMethod]
    public void BandFill_IsOpaqueBlackOrSeeThroughWallpaper()
    {
        Assert.AreEqual(TopNotchLayout.BlackBand, TopNotchLayout.BandFill(TopBandStyle.Black));
        // 바탕화면 띠는 거의 투명해 바탕화면이 그대로 보이지만, 알파가 0이 아니어서 클릭은 띠가 받는다.
        var fill = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(TopNotchLayout.BandFill(TopBandStyle.Wallpaper));
        Assert.IsTrue(fill.A is > 0 and < 8);
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
    [DataRow(1.0, 8)]
    [DataRow(1.25, 10)]
    [DataRow(1.5, 12)]
    [DataRow(2.0, 16)]
    public void BandHeightPixels_ScalesWithDpi(double scale, int expected)
    {
        Assert.AreEqual(expected, TopNotchLayout.BandHeightPixels(scale));
    }

    [TestMethod]
    public void NotchCenter_StartsInTheMiddleAndStaysOnScreen()
    {
        Assert.AreEqual(960d, TopNotchLayout.NotchCenter(0, 1920, 0));
        Assert.AreEqual(1200d, TopNotchLayout.NotchCenter(0, 1920, 240));
        double half = TopNotchLayout.NotchSurfaceWidth / 2;
        // 노치는 화면 끝까지 옮길 수 있다(상세 패널은 자리가 없으면 반대쪽에 붙는다).
        Assert.AreEqual(half, TopNotchLayout.NotchCenter(0, 1920, -5000), .001);
        Assert.AreEqual(512d, TopNotchLayout.NotchCenter(0, 1024, 0), .001);
        Assert.AreEqual(1920 - half, TopNotchLayout.NotchCenter(0, 1920, 5000), .001);
        Assert.AreEqual(240d, TopNotchLayout.CenterOffset(TopNotchLayout.NotchCenter(0, 1920, 240), 0, 1920), .001);
    }

    [TestMethod]
    public void Window_KeepsItsLeftEdgeAndGrowsDownOnlyWhileThePanelIsShown()
    {
        // 상세(목록 왼쪽)까지 담을 만큼 왼쪽을 늘 두어 창의 왼쪽 위가 움직이지 않는다.
        Assert.AreEqual(TopNotchLayout.PanelWidth / 2 + TopNotchLayout.DetailWidth, TopNotchLayout.WindowLeftExtent);
        Assert.IsTrue(TopNotchLayout.WindowLeftExtent >= TopNotchLayout.ReminderWidth / 2);
        Assert.IsTrue(TopNotchLayout.WindowRightExtent >= TopNotchLayout.PanelWidth / 2);
        Assert.IsTrue(TopNotchLayout.WindowRightExtent >= TopNotchLayout.NotchSurfaceWidth / 2);
        Assert.AreEqual(TopNotchLayout.WindowHeight, TopNotchLayout.WindowHeightFor(false, 480));
        Assert.AreEqual(TopNotchLayout.NotchHeight + 480 + 2, TopNotchLayout.WindowHeightFor(true, 480));
        Assert.AreEqual(TopNotchLayout.WindowHeight, TopNotchLayout.WindowHeightFor(true, 100));
    }

    [TestMethod]
    public void OpenProgress_MorphsTheNotchBeforeRevealingThePanel()
    {
        Assert.AreEqual(0d, TopNotchLayout.NotchMorph(0));
        Assert.AreEqual(0d, TopNotchLayout.PanelReveal(0));
        Assert.AreEqual(1d, TopNotchLayout.NotchMorph(1));
        Assert.AreEqual(1d, TopNotchLayout.PanelReveal(1));
        // 패널이 내려오기 시작할 때 노치는 이미 거의 다 넓어져 있다.
        Assert.AreEqual(0d, TopNotchLayout.PanelReveal(TopNotchLayout.PanelRevealStart));
        Assert.IsTrue(TopNotchLayout.NotchMorph(TopNotchLayout.PanelRevealStart) > .8);
        Assert.AreEqual(1d, TopNotchLayout.NotchMorph(TopNotchLayout.NotchMorphSpan));
        // 두 단계 모두 진행값에 따라 줄어들지 않는다.
        double previousMorph = 0, previousReveal = 0;
        for (double progress = 0; progress <= 1.0001; progress += .02)
        {
            double morph = TopNotchLayout.NotchMorph(progress), reveal = TopNotchLayout.PanelReveal(progress);
            Assert.IsTrue(morph >= previousMorph - 1e-9 && reveal >= previousReveal - 1e-9);
            previousMorph = morph;
            previousReveal = reveal;
        }
    }

    [TestMethod]
    public void AlertRipple_QueuesInsteadOfDropping_AndSkipsWhileReadingOrFullscreen()
    {
        var start = new DateTime(2026, 11, 2, 9, 0, 0);
        var ends = start.AddMilliseconds(TopNotchLayout.AlertRippleMilliseconds);
        Assert.AreEqual(AlertRippleDecision.Play, TopNotchLayout.DecideAlertRipple(start, DateTime.MinValue, true, false, false));
        // 흐르는 중(켜짐 일렁임 포함)에 온 알림은 버리지 않고, 끝난 뒤 한 번 더 흘린다.
        Assert.AreEqual(AlertRippleDecision.Queue, TopNotchLayout.DecideAlertRipple(start.AddMilliseconds(500), ends, true, false, false));
        Assert.AreEqual(AlertRippleDecision.Play, TopNotchLayout.DecideAlertRipple(ends, ends, true, false, false));
        // 읽는 중·전체화면·숨김이면 생략한다.
        Assert.AreEqual(AlertRippleDecision.Skip, TopNotchLayout.DecideAlertRipple(start, DateTime.MinValue, true, true, false));
        Assert.AreEqual(AlertRippleDecision.Skip, TopNotchLayout.DecideAlertRipple(start, DateTime.MinValue, true, false, true));
        Assert.AreEqual(AlertRippleDecision.Skip, TopNotchLayout.DecideAlertRipple(start, DateTime.MinValue, false, false, false));
        // 알림(선셋)과 켜짐(민트)은 색으로 구분되고, 알림이 더 빨리 지나간다.
        Assert.AreNotEqual(TopNotchLayout.ActivationRipple, TopNotchLayout.AlertRipple);
        Assert.IsTrue(TopNotchLayout.AlertRippleMilliseconds < TopNotchLayout.ActivationRippleMilliseconds);
    }

    [TestMethod]
    public void Lens_ChangesColorAndSize_SoItIsNotColorOnly()
    {
        var quiet = TopNotchLayout.Lens(false);
        var alert = TopNotchLayout.Lens(true);
        Assert.AreEqual(1d, quiet.Scale);
        Assert.AreEqual(TopNotchLayout.LensAlertDiameter / TopNotchLayout.LensDiameter, alert.Scale, .001);
        Assert.IsTrue(alert.Scale > 1);
        Assert.AreNotEqual(quiet.Colors, alert.Colors);
    }

    [TestMethod]
    public void PanelWidth_MatchesThePanelControl()
    {
        Assert.AreEqual(TopNotchLayout.PanelWidth, NotificationPanel.ListWidth);
        Assert.AreEqual(TopNotchLayout.DetailWidth, NotificationPanel.DetailWidth);
    }

    [TestMethod]
    public void DetailPanel_MovesToTheRightOnlyWhenTheLeftHasNoRoom()
    {
        Assert.IsFalse(TopNotchLayout.DetailOnRight(960, 0, 1920), "가운데에서는 상세가 왼쪽에 붙는다.");
        Assert.IsFalse(TopNotchLayout.DetailOnRight(TopNotchLayout.WindowLeftExtent, 0, 1920));
        Assert.IsTrue(TopNotchLayout.DetailOnRight(TopNotchLayout.WindowLeftExtent - 1, 0, 1920));
        Assert.IsTrue(TopNotchLayout.DetailOnRight(TopNotchLayout.NotchSurfaceWidth / 2, 0, 1920));
        Assert.IsFalse(TopNotchLayout.DetailOnRight(1920 - TopNotchLayout.NotchSurfaceWidth / 2, 0, 1920), "오른쪽 끝에서는 왼쪽에 붙는다.");
        // 상세가 붙는 쪽을 넓게 두고, 창 폭은 좁은 화면(1024px)보다 작게 유지한다.
        Assert.AreEqual(TopNotchLayout.WindowLeftExtent, TopNotchLayout.NotchCenterInWindow(false));
        Assert.AreEqual(TopNotchLayout.WindowRightExtent, TopNotchLayout.NotchCenterInWindow(true));
        Assert.IsTrue(TopNotchLayout.WindowLeftExtent + TopNotchLayout.WindowRightExtent < 1024);
    }

    [TestMethod]
    public void Notch_IsTheMockScaledToHeight8AndStillOpensIntoThePanel()
    {
        var closed = TopNotchLayout.NotchLeftSide(0);
        var open = TopNotchLayout.NotchLeftSide(1);
        // 시안(높이 30, 위쪽 폭 360.2)을 높이 8에 맞춰 같은 비율로 줄인다.
        Assert.AreEqual(8d / 30, TopNotchLayout.HeightScale, 1e-9);
        Assert.AreEqual(360.2 * 8 / 30, TopNotchLayout.NotchSurfaceWidth, .001);
        Assert.AreEqual(TopNotchLayout.NotchHeight, closed[^1].Y, .001);
        Assert.AreEqual(TopNotchLayout.NotchHeight, open[^1].Y, .001);
        // 노치가 작아도 열리면 바닥이 목록 패널 폭과 같아진다.
        Assert.AreEqual(TopNotchLayout.PanelWidth, TopNotchLayout.NotchSurfaceWidth - 2 * open[^1].X, .01);
        Assert.IsTrue(TopNotchLayout.NotchBottomWidth > 0);
        Assert.IsTrue(TopNotchLayout.NotchSurfaceWidth / 2 <= TopNotchLayout.WindowRightExtent);
    }

    [TestMethod]
    public void NotchLeftSide_ClosedMatchesTheMockAndOpenMatchesThePanel()
    {
        var closed = TopNotchLayout.NotchLeftSide(0);
        var open = TopNotchLayout.NotchLeftSide(1);
        // 닫힘: 바닥 폭은 시안 비율 그대로 줄인 값이다(기본 높이 8에서 약 62.8).
        Assert.AreEqual(TopNotchLayout.NotchBottomWidth, TopNotchLayout.NotchSurfaceWidth - 2 * closed[^1].X, .01);
        // 열림: 바닥 폭이 목록 패널 폭과 같아 패널 옆면으로 곧게 이어진다.
        Assert.AreEqual(TopNotchLayout.PanelWidth, TopNotchLayout.NotchSurfaceWidth - 2 * open[^1].X, .01);
        // 높이와 위쪽 시작점은 그대로다.
        Assert.AreEqual(0d, open[0].X, .001);
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
        Assert.AreEqual(8d, TopNotchLayout.NotchHeight);
        Assert.AreEqual(96.05, TopNotchLayout.NotchSurfaceWidth, .001);
        Assert.IsTrue(TopNotchLayout.NotchBottomWidth < TopNotchLayout.NotchSurfaceWidth);
        Assert.IsTrue(TopNotchLayout.NotchSurfaceWidth <= TopNotchLayout.WindowWidth);
        // 패널은 노치가 다 넓어지기 전(겹치는 구간)부터 내려와 끊김 없이 이어진다.
        Assert.IsTrue(TopNotchLayout.PanelRevealStart < TopNotchLayout.NotchMorphSpan);
        Assert.IsTrue(TopNotchLayout.NotchSurfaceWidth < TopNotchLayout.WindowWidth);
    }
}

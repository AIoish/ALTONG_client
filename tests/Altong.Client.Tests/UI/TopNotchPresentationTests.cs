using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// 상단 노치 전환 UI. 창을 화면에 띄우지 않아 CI에서 화면 맨 위 띠(AppBar)를 등록하지 않는다.
/// 띠 등록·전체화면 숨김은 로컬에서 직접 확인한다.
/// </summary>
[TestClass]
public sealed class TopNotchPresentationTests
{
    [TestMethod]
    public void FlyoutOffersTopNotchAndBandStyles_OnlyInTopNotch()
    {
        RunOnSta(() =>
        {
            var flyout = new NotificationFlyoutWindow(new DockNotificationState());
            try
            {
                var bandBar = (FrameworkElement)flyout.FindName("BandStyleBar");
                var topButton = (Button)flyout.FindName("TopNotchDesignButton");
                Assert.AreEqual(Visibility.Collapsed, bandBar.Visibility);

                flyout.SetPresentation(DockPresentation.TopNotch);
                Assert.AreEqual(Visibility.Visible, bandBar.Visibility);
                Assert.AreEqual("선택됨", System.Windows.Automation.AutomationProperties.GetItemStatus(topButton));
                Assert.AreEqual("선택 안 됨", System.Windows.Automation.AutomationProperties.GetItemStatus(
                    (Button)flyout.FindName("StealthDesignButton")));

                flyout.SetPresentation(DockPresentation.Stealth);
                Assert.AreEqual(Visibility.Collapsed, bandBar.Visibility);
                Assert.AreEqual("선택 안 됨", System.Windows.Automation.AutomationProperties.GetItemStatus(topButton));
            }
            finally
            {
                flyout.Close();
            }
        });
    }

    [TestMethod]
    public void FlyoutButtons_RequestReturnAndBandStyle()
    {
        RunOnSta(() =>
        {
            var flyout = new NotificationFlyoutWindow(new DockNotificationState());
            var presentations = new List<DockPresentation>();
            var styles = new List<TopBandStyle>();
            flyout.SwitchPresentationRequested += (_, value) => presentations.Add(value);
            flyout.BandStyleRequested += (_, value) => styles.Add(value);
            try
            {
                flyout.SetPresentation(DockPresentation.TopNotch);
                flyout.SetBandStyle(TopBandStyle.Wallpaper);
                Assert.AreEqual("선택됨", System.Windows.Automation.AutomationProperties.GetItemStatus(
                    (Button)flyout.FindName("BandWallpaperButton")));
                Assert.AreEqual("선택 안 됨", System.Windows.Automation.AutomationProperties.GetItemStatus(
                    (Button)flyout.FindName("BandBlackButton")));

                Click(flyout, "BandBlackButton");
                Click(flyout, "BandWallpaperButton");
                Click(flyout, "StealthDesignButton");
                Click(flyout, "TopNotchDesignButton");

                CollectionAssert.AreEqual(new[] { TopBandStyle.Black, TopBandStyle.Wallpaper }, styles);
                CollectionAssert.AreEqual(new[] { DockPresentation.Stealth, DockPresentation.TopNotch }, presentations);
            }
            finally
            {
                flyout.Close();
            }
        });
    }

    [TestMethod]
    public void DockSwitchesToTopNotchAndBack_WithoutShowingTheBand()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state);
            try
            {
                dock.SetPresentation(DockPresentation.TopNotch);
                var top = (FrameworkElement)dock.FindName("TopNotchSurface");
                Assert.AreEqual(DockPresentation.TopNotch, dock.Presentation);
                Assert.AreEqual(Visibility.Visible, top.Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("NotchSurface")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)dock.FindName("StealthSurface")).Visibility);
                Assert.AreEqual(TopNotchLayout.NotchSurfaceWidth, top.Width);
                Assert.AreEqual(TopNotchLayout.NotchHeight, top.Height);
                Assert.AreEqual(TopNotchLayout.WindowWidth, dock.Width);
                Assert.AreEqual(0d, dock.Top);
                Assert.AreEqual((SystemParameters.PrimaryScreenWidth - dock.Width) / 2, dock.Left, .01);

                // 상단 띠는 배경색으로 시작한다.
                Assert.AreEqual(TopBandStyle.Wallpaper, dock.BandStyle);
                dock.SetTopBandStyle(TopBandStyle.Black);
                Assert.AreEqual(TopBandStyle.Black, dock.BandStyle);
                Assert.IsFalse(dock.IsFullscreenSuppressed);

                // 다른 디자인으로 돌아가도 띠 색 선택은 실행 중에 유지한다.
                dock.SetPresentation(DockPresentation.Stealth);
                Assert.AreEqual(Visibility.Collapsed, top.Visibility);
                Assert.AreEqual(TopBandStyle.Black, dock.BandStyle);
                Assert.AreEqual(1d, dock.Opacity);
            }
            finally
            {
                dock.Close();
            }
        });
    }

    private static void Click(Window window, string name) =>
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "상단 노치 UI 검증이 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

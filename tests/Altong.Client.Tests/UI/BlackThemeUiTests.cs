using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class BlackThemeUiTests
{
    [TestMethod]
    public void Palette_KeepsCharcoalSurfaces_AndAccessibleActionAndStatusColors() => RunSta(() =>
    {
        var report = new SessionReportWindow(new SessionResult(DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow, [], [], true));
        try
        {
            Color ColorOf(string key) => ((SolidColorBrush)report.FindResource(key)).Color;
            foreach (string surface in new[] { "BlackPaper", "BlackCanvas", "BlackSurface", "BlackRaised", "BlackInput" })
            {
                var color = ColorOf(surface);
                Assert.IsTrue(Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) <= 5);
                Assert.IsTrue(Contrast(ColorOf("BlackText"), color) >= 4.5, surface);
                Assert.IsTrue(Contrast(ColorOf("BlackMuted"), color) >= 4.5, "Secondary text on " + surface);
            }
            Assert.IsTrue(Contrast(ColorOf("BlackAction"), ColorOf("BlackActionText")) >= 7);
            Assert.IsTrue(Contrast(ColorOf("BlackControlRule"), ColorOf("BlackRaised")) >= 3);
            Assert.IsTrue(Contrast(ColorOf("BlackFocus"), ColorOf("BlackRaised")) >= 3);
            Assert.IsTrue(Contrast(ColorOf("BlackError"), ColorOf("BlackCanvas")) >= 4.5);
            foreach (var (foreground, background, minimum) in new[]
            {
                ("BlackAccentText", "BlackSelection", 4.5),
                ("BlackSelectionRule", "BlackSelection", 3d),
                ("BlackSuccess", "BlackSuccessSurface", 4.5),
                ("BlackSuccessMuted", "BlackSuccessSurface", 4.5),
                ("BlackText", "BlackSuccessSurface", 4.5),
                ("BlackSuccessRule", "BlackSuccessSurface", 3d),
                ("BlackSuccessRule", "BlackSurface", 3d),
                ("BlackSuccess", "BlackSurface", 4.5),
                ("BlackAccentText", "BlackSurface", 4.5),
                ("BlackAction", "BlackSurface", 3d),
                ("BlackFocus", "BlackSuccessSurface", 3d)
            })
                Assert.IsTrue(Contrast(ColorOf(foreground), ColorOf(background)) >= minimum,
                    $"{foreground} on {background}");
        }
        finally { report.Close(); }
    });

    [TestMethod]
    public void Report_DetailDisclosure_RetainsNativeExpandCollapseAutomation() => RunSta(() =>
    {
        var report = new SessionReportWindow(new SessionResult(DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow, [], [], true));
        try
        {
            var root = (FrameworkElement)report.Content;
            root.Measure(new Size(480, 380));
            root.Arrange(new Rect(0, 0, 480, 380));
            root.UpdateLayout();
            var expander = report.FindName("NotificationsExpander") as Expander;
            Assert.IsNotNull(expander);
            var provider = (IExpandCollapseProvider)new ExpanderAutomationPeer(expander).GetPattern(PatternInterface.ExpandCollapse);
            Assert.AreEqual(ExpandCollapseState.Collapsed, provider.ExpandCollapseState);
            provider.Expand();
            root.UpdateLayout();
            Assert.IsTrue(expander.IsExpanded);
            var content = (FrameworkElement)expander.Template.FindName("ExpandedContent", expander);
            Assert.AreEqual(Visibility.Visible, content.Visibility);
            provider.Collapse();
            root.UpdateLayout();
            Assert.AreEqual(Visibility.Collapsed, content.Visibility);
        }
        finally { report.Close(); }
    });

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                double channel = value / 255d;
                return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static void RunSta(Action action)
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

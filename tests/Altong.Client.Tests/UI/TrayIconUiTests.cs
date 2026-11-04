using System.Reflection;
using System.Windows.Forms;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class TrayIconUiTests
{
    [TestMethod]
    public void Menu_OpensDashboard_AndInvokesApplicationShutdown()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int opened = 0, exited = 0;
                using var tray = new TrayIconService(() => opened++);
                var menu = (ContextMenuStrip)typeof(TrayIconService)
                    .GetField("_contextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
                CollectionAssert.AreEqual(new[] { "대시보드 열기", "대시보드 종료" },
                    menu.Items.Cast<ToolStripItem>().Select(item => item.Text).ToArray());
                Assert.IsFalse(menu.Items[1].Enabled);
                tray.SetExitAction(() => exited++);
                Assert.IsTrue(menu.Items[1].Enabled);
                menu.Items[0].PerformClick();
                Assert.AreEqual(1, opened);
                Assert.AreEqual(0, exited);
                menu.Items[1].PerformClick();
                Assert.AreEqual(1, opened, "종료 메뉴가 대시보드를 다시 열지 않습니다.");
                Assert.AreEqual(1, exited, "숨기기 대신 앱 종료 콜백을 실행합니다.");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

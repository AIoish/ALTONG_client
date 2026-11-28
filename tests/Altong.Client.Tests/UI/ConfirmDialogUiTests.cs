using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.UI;

[TestClass]
public sealed class ConfirmDialogUiTests
{
    [TestMethod]
    public void DestructiveConfirmDialog_UsesGraphiteGlassStyle_AndDefaultsToCancel()
    {
        RunSta(() =>
        {
            var dialog = new ConfirmDialogWindow(
                title: "ALTONG 종료",
                message: "집중 모드가 켜져 있어요.\n진행 중인 집중을 끝내고 ALTONG을 종료할까요?",
                confirmText: "집중 끝내고 종료",
                cancelText: "계속 집중",
                isDestructive: true);

            Assert.AreEqual(WindowStyle.None, dialog.WindowStyle);
            Assert.IsTrue(dialog.AllowsTransparency);
            Assert.AreEqual("ALTONG 종료", ((TextBlock)dialog.FindName("TitleText")).Text);
            StringAssert.Contains(((TextBlock)dialog.FindName("MessageText")).Text, "집중 모드가 켜져 있어요.");

            var cancelButton = (Button)dialog.FindName("CancelButton");
            var confirmButton = (Button)dialog.FindName("ConfirmButton");
            Assert.AreEqual(Visibility.Visible, cancelButton.Visibility);
            Assert.AreEqual("계속 집중", cancelButton.Content);
            Assert.AreEqual("집중 끝내고 종료", confirmButton.Content);
            Assert.IsTrue(cancelButton.IsDefault, "파괴적 확인창은 취소 버튼이 기본 선택이어야 합니다.");
            Assert.IsFalse(confirmButton.IsDefault);

            confirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(dialog.Confirmed);
        });
    }

    [TestMethod]
    public void SingleButtonAlert_HidesCancelButton_AndSetsConfirmAsDefault()
    {
        RunSta(() =>
        {
            var dialog = new ConfirmDialogWindow(
                title: "메모 저장 실패",
                message: "메모를 저장하지 못했습니다.",
                confirmText: "확인",
                cancelText: null,
                isDestructive: false);

            var cancelButton = (Button)dialog.FindName("CancelButton");
            var confirmButton = (Button)dialog.FindName("ConfirmButton");
            Assert.AreEqual(Visibility.Collapsed, cancelButton.Visibility);
            Assert.IsTrue(confirmButton.IsDefault);
            Assert.IsTrue(confirmButton.IsCancel);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "STA test timed out.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

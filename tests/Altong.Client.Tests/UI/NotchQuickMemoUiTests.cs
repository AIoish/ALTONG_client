using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using Altong.Client.Services.Calendar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotchQuickMemoUiTests
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [TestMethod]
    public void MemoMode_KeepsPanelOpenPreservesDraftAndNotificationScroll_AndDoesNotStealFocusOnHover()
    {
        RunOnSta(() =>
        {
            var store = new QuickMemoStore();
            var state = new DockNotificationState();
            var dock = new NotificationDockWindow(state, store);
            try
            {
                dock.Show();
                state.BeginSession("memo-test");
                for (int i = 0; i < 25; i++) state.Receive(new NotificationRecord($"id{i}", "Slack", "테스트 사용자",
                    "확인 요청", "테스트 알림 내용", DateTime.UtcNow.AddMinutes(-i), true, SessionId: "memo-test"));
                state.OpenPanel();
                OpenImmediately(dock);
                var panel = dock.Panel;
                Assert.AreEqual((nint)3, SendMessage(new WindowInteropHelper(dock).Handle, 0x0021, 0, 0));
                var scroll = (ScrollViewer)panel.FindName("ListScroll");
                scroll.ScrollToVerticalOffset(100);
                dock.UpdateLayout();
                double offset = scroll.VerticalOffset;
                Click(panel, "QuickMemoButton");
                Pump();
                Assert.IsTrue(panel.IsMemoOpen);
                Assert.IsTrue(panel.MemoEditor.IsKeyboardFocusWithin,
                    $"Explicit memo focus: active={dock.IsActive}, visible={panel.MemoEditor.IsVisible}, height={panel.MemoEditor.ActualHeight}.");
                panel.MemoEditor.Text = "한글 메모\n확인할 내용";
                Invoke(dock, "LeaveTimer_Tick");
                Assert.IsTrue(state.IsPanelOpen, "Typing must prevent hover-leave dismissal without changing pin state.");
                Assert.IsFalse(state.IsPinned);
                Assert.IsTrue(panel.TryFlushMemoDraft());
                Assert.AreEqual(panel.MemoEditor.Text, store.Draft);
                var view = (QuickMemoView)panel.FindName("MemoView");
                view.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(dock)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.IsFalse(panel.IsMemoOpen);
                dock.UpdateLayout();
                Assert.AreEqual(offset, scroll.VerticalOffset, .1);
                Click(panel, "QuickMemoButton");
                Assert.AreEqual(store.Draft, panel.MemoEditor.Text);
                view.SaveMemo();
                panel.MemoEditor.Text = "이전 집중의 미저장 초안";
                Assert.IsTrue(panel.TryFlushMemoDraft());
                state.EndSession();
                Assert.IsFalse(panel.IsMemoOpen);
                state.BeginSession("next-session");
                state.OpenPanel();
                Click(panel, "QuickMemoButton");
                Assert.AreEqual("", panel.MemoEditor.Text, "A new focus session must start with an empty editor.");
                Assert.AreEqual(0, ((ItemsControl)view.FindName("MemoItems")).Items.Count,
                    "Saved notes from another focus session must not appear in the new session.");
                Assert.AreEqual("이전 집중의 미저장 초안", store.GetDraftForSession("memo-test"));
                panel.MemoEditor.Text = "이번 집중의 새 초안";
                Assert.IsTrue(panel.TryFlushMemoDraft());
                Assert.AreEqual("next-session", store.DraftSessionId,
                    "New input belongs exclusively to the current focus session.");
                view.SaveMemo();
                Assert.AreEqual(1, ((ItemsControl)view.FindName("MemoItems")).Items.Count);
                Assert.AreEqual("이번 집중의 새 초안", ((QuickMemoEntry)((ItemsControl)view.FindName("MemoItems")).Items[0]).Text);
                Assert.AreEqual(2, store.GetForSession("memo-test", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)).Count,
                    "The previous session's saved note and draft must remain available to its report.");
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void SaveAndComposition_PreventBlankOrDuplicateSaves_AndKeepImeEnterUntouched()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            state.BeginSession("memo");
            var store = new QuickMemoStore();
            var panel = new NotificationPanel(state);
            panel.SetMemoStore(store);
            var host = new Window { Content = panel, Width = 316, Height = 480, Background = Brushes.Black, ShowInTaskbar = false };
            try
            {
                host.Show();
                state.OpenPanel();
                Click(panel, "QuickMemoButton");
                var view = (QuickMemoView)panel.FindName("MemoView");
                var save = (Button)view.FindName("SaveMemoButton");
                Assert.IsFalse(save.IsEnabled);
                panel.MemoEditor.Text = "한글 조합 중";
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, panel.MemoEditor, "글"));
                var ime = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.ImeProcessed)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                view.RaiseEvent(ime);
                Assert.IsFalse(ime.Handled);
                typeof(QuickMemoView).GetField("_composing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, true);
                view.SaveMemo();
                Assert.AreEqual(0, store.Entries.Count);
                typeof(QuickMemoView).GetField("_composing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, false);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1, store.Entries.Count);
                Assert.AreEqual("", panel.MemoEditor.Text);
                Assert.AreEqual("", store.Draft);
            }
            finally { host.Close(); }
        });
    }

    [TestMethod]
    public void StopFocus_GuardsRepeatedClicksRetainsFailureStateAndUsesInjectedCoordinatorAction()
    {
        RunOnSta(() =>
        {
            var state = new DockNotificationState();
            state.BeginSession("stop-test");
            var dock = new NotificationDockWindow(state);
            int calls = 0;
            bool succeed = false;
            dock.Panel.StopFocusAction = () =>
            {
                calls++;
                if (!succeed) return "복구 실패 · 다시 시도해 주세요.";
                dock.EndNotificationSession();
                return null;
            };
            try
            {
                dock.Show();
                state.OpenPanel();
                Click(dock.Panel, "StopFocusButton");
                Click(dock.Panel, "StopFocusButton");
                Pump();
                Assert.AreEqual(1, calls);
                Assert.IsTrue(state.IsActive && state.IsPanelOpen);
                Assert.AreEqual(Visibility.Visible, ((TextBlock)dock.Panel.FindName("UtilityStatusText")).Visibility);
                succeed = true;
                Click(dock.Panel, "QuickMemoButton");
                dock.Panel.MemoEditor.Text = "집중 종료 후에도 남을 초안";
                Click(dock.Panel, "StopFocusButton");
                Pump();
                Assert.AreEqual(2, calls);
                Assert.IsFalse(state.IsActive);
                Assert.IsFalse(((Button)dock.Panel.FindName("StopFocusButton")).IsEnabled);
            }
            finally { dock.Close(); }
        });
    }

    [TestMethod]
    public void FailedMemoWrite_KeepsInputAndPreventsFocusStop_UntilStorageRecovers()
    {
        RunOnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "altong-memo-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "memos.json");
            var store = new QuickMemoStore(path);
            var state = new DockNotificationState();
            state.BeginSession("failure");
            var dock = new NotificationDockWindow(state, store);
            int stops = 0;
            dock.Panel.StopFocusAction = () => { stops++; return null; };
            try
            {
                dock.Show();
                state.OpenPanel();
                Click(dock.Panel, "QuickMemoButton");
                dock.Panel.MemoEditor.Text = "저장 실패해도 남을 한글 메모";
                Directory.CreateDirectory(path);
                var view = (QuickMemoView)dock.Panel.FindName("MemoView");
                view.SaveMemo();
                Assert.AreEqual(0, store.Entries.Count);
                Assert.AreEqual("저장 실패해도 남을 한글 메모", dock.Panel.MemoEditor.Text);
                Click(dock.Panel, "StopFocusButton");
                Pump();
                Assert.AreEqual(0, stops);
                Assert.IsTrue(state.IsActive && dock.Panel.IsMemoOpen);
                Directory.Delete(path);
                // Exercise an autosave completing around an explicit save: it must never resurrect an old draft.
                Invoke(view, "DraftTimer_Tick");
                dock.Panel.MemoEditor.AppendText("\n수정한 최종 내용");
                view.SaveMemo();
                Pump();
                var reopened = new QuickMemoStore(path);
                Assert.AreEqual("", reopened.Draft);
                Assert.AreEqual("저장 실패해도 남을 한글 메모\n수정한 최종 내용", reopened.Entries.Single().Text);
                Assert.AreEqual("", dock.Panel.MemoEditor.Text);
            }
            finally { dock.Close(); Directory.Delete(directory, recursive: true); }
        });
    }

    [TestMethod]
    public void ReportMemoSection_ShowsSavedNotesAndDrafts_PaginatesAndHidesWhenEmpty()
    {
        RunOnSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var entries = Enumerable.Range(0, 22).Select(i => new QuickMemoEntry($"memo-{i}",
                $"집중 중 메모 {i + 1}\n" + (i == 0 ? new string('가', 300) : "집중 후 확인할 내용입니다."), now.AddMinutes(i), "report", i == 1)).ToArray();
            string folder = Path.Combine(Path.GetTempPath(), "altong-report-memo-" + Guid.NewGuid().ToString("N"));
            var result = new SessionResult(now.AddMinutes(-25).UtcDateTime, now.UtcDateTime, [], [], true)
                { SessionId = "report", Memos = entries };
            var report = new SessionReportWindow(result, calendar: new DashboardCalendarStore(Path.Combine(folder, "calendar.json")),
                summaryStatePath: Path.Combine(folder, "state.json"));
            try
            {
                report.Width = 680;
                report.Height = 720;
                report.Show();
                Pump();
                var section = (FrameworkElement)report.FindName("MemoSectionBorder");
                var aiSummary = (FrameworkElement)report.FindName("AiSummarySection");
                var appUsage = (FrameworkElement)report.FindName("AppUsageExpander");
                Assert.IsTrue(aiSummary.TranslatePoint(new System.Windows.Point(), report).Y < section.TranslatePoint(new System.Windows.Point(), report).Y);
                Assert.IsTrue(section.TranslatePoint(new System.Windows.Point(), report).Y < appUsage.TranslatePoint(new System.Windows.Point(), report).Y,
                    "Memo placement must remain between AI summary and app usage.");
                var items = (ItemsControl)report.FindName("MemoItemsControl");
                Assert.AreEqual(Visibility.Visible, section.Visibility);
                Assert.AreEqual("22개", ((TextBlock)report.FindName("MemoCountText")).Text);
                Assert.AreEqual(20, items.Items.Count);
                Assert.AreEqual(entries[1].WhenText, ((SessionReportWindow.ReportMemoItem)items.Items[1]).WhenText);
                Assert.AreEqual(Visibility.Visible, ((Button)report.FindName("MemoReminderButton")).Visibility);
                Assert.AreEqual("메모 22개 확인", ((TextBlock)report.FindName("MemoReminderText")).Text);
                var firstMemo = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0);
                var longMemo = (Expander)items.ItemTemplate.FindName("MemoContentExpander", firstMemo);
                var secondMemo = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.AreEqual(Visibility.Visible, longMemo.Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((Expander)items.ItemTemplate.FindName("MemoContentExpander", secondMemo)).Visibility);
                longMemo.IsExpanded = true;
                report.UpdateLayout();
                Assert.AreEqual(Visibility.Collapsed, ((TextBlock)items.ItemTemplate.FindName("MemoPreview", firstMemo)).Visibility);
                Assert.AreEqual("접기", longMemo.Header);
                longMemo.IsExpanded = false;
                foreach (var size in new[] { new System.Windows.Size(680, 720), new System.Windows.Size(480, 520) })
                {
                    report.Width = size.Width;
                    report.Height = size.Height;
                    Pump();
                    section.BringIntoView();
                    report.UpdateLayout();
                    var save = (Button)report.FindName("SaveReportButton");
                    var close = (Button)report.FindName("SkipSaveButton");
                    Assert.IsTrue(close.TranslatePoint(new System.Windows.Point(), report).Y + close.ActualHeight <= report.ActualHeight);
                    SavePreview(report, $"report-memos-{size.Width:0}.png");
                }
                ((Button)report.FindName("MoreMemosButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(22, items.Items.Count);
                Assert.AreEqual(Visibility.Collapsed, ((Button)report.FindName("MoreMemosButton")).Visibility);
                firstMemo = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0);
                report.UpdateLayout();
                firstMemo = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0);
                longMemo = (Expander)items.ItemTemplate.FindName("MemoContentExpander", firstMemo);
                longMemo.IsExpanded = true;
                ((Button)report.FindName("MoreAppsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                report.UpdateLayout();
                firstMemo = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0);
                longMemo = (Expander)items.ItemTemplate.FindName("MemoContentExpander", firstMemo);
                Assert.IsTrue(longMemo.IsExpanded, "Updating another report section must not collapse the memo being read.");
                Assert.AreEqual(Visibility.Visible, longMemo.Visibility);
                SavePreview(report, "report-memo-expanded.png");
            }
            finally { report.Close(); }

            var empty = new SessionReportWindow(result with { Memos = [] }, calendar: new DashboardCalendarStore(Path.Combine(folder, "calendar.json")),
                summaryStatePath: Path.Combine(folder, "state.json"));
            try
            {
                Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)empty.FindName("MemoSectionBorder")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, ((Button)empty.FindName("MemoReminderButton")).Visibility);
            }
            finally { empty.Close(); }
        });
    }

    [TestMethod]
    public void CompactMemoLayout_LeavesActionsVisibleWithLongContentAndManyMemos()
    {
        RunOnSta(() =>
        {
            var store = new QuickMemoStore();
            for (int i = 0; i < 35; i++) store.Save($"메모 {i}\n" + new string('가', 1200), "layout");
            var state = new DockNotificationState();
            state.BeginSession("layout");
            var panel = new NotificationPanel(state);
            panel.SetMemoStore(store);
            var host = new Window { Content = panel, Width = 316, Height = 360, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Background = Brushes.Black, ShowInTaskbar = false };
            try
            {
                host.Show();
                state.OpenPanel();
                Click(panel, "QuickMemoButton");
                panel.MemoEditor.Text = "짧게 기록하고 작업으로 돌아가세요.\n긴 내용도 입력창 안에서 확인할 수 있어요.";
                host.UpdateLayout();
                var stop = (Button)panel.FindName("StopFocusButton");
                var view = (QuickMemoView)panel.FindName("MemoView");
                var save = (Button)view.FindName("SaveMemoButton");
                Assert.IsTrue(save.TranslatePoint(new System.Windows.Point(), host).Y + save.ActualHeight < stop.TranslatePoint(new System.Windows.Point(), host).Y);
                Assert.IsTrue(stop.TranslatePoint(new System.Windows.Point(), host).Y + stop.ActualHeight <= host.ActualHeight);
                Assert.AreEqual(20, ((ItemsControl)view.FindName("MemoItems")).Items.Count);
                Assert.AreEqual(Visibility.Visible, ((Button)view.FindName("MoreMemosButton")).Visibility);
                SavePreview(host, "memo-compact.png");
                host.Height = 480;
                Pump();
                host.UpdateLayout();
                SavePreview(host, "memo-default.png");
                Click(panel, "QuickMemoButton");
                host.UpdateLayout();
                SavePreview(host, "actions-default.png");
                ((Expander)panel.FindName("NotchAppearanceExpander")).IsExpanded = true;
                host.UpdateLayout();
                var settings = (FrameworkElement)panel.FindName("BandStyleBar");
                Assert.IsTrue(settings.TranslatePoint(new System.Windows.Point(), host).X + settings.ActualWidth <= host.ActualWidth,
                    "Band appearance settings must still fit inside the panel.");
                Assert.IsNull(panel.FindName("NotchSizeBar"), "The removed temporary notch size controls must not return during integration.");
                Assert.AreEqual(8d, TopNotchLayout.NotchHeight);
                SavePreview(host, "actions-settings.png");
            }
            finally { host.Close(); }
        });
    }

    private static void Click(NotificationPanel panel, string name) =>
        ((Button)panel.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void OpenImmediately(NotificationDockWindow dock)
    {
        dock.BeginAnimation(NotificationDockWindow.TopNotchOpenProgressProperty, null);
        dock.TopNotchOpenProgress = 1;
        dock.UpdateLayout();
    }
    private static void Invoke(object target, string method) => target.GetType().GetMethod(method,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [null, EventArgs.Empty]);
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void SavePreview(Window host, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }
}

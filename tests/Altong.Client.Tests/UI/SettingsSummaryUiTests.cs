using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.NotificationControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SettingsSummaryUiTests
{
    [TestMethod]
    public void AppControlSummary_TracksActualBackendAndEligibleSelectedApps()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var db = SqliteDatabase.CreateInMemory();
                db.Initialize();
                var focus = new FocusModeService();
                var native = new TestNative();
                using var coordinator = new FocusModeCoordinator(focus, new Observer(), new Launcher(), new ImmediateDispatcher(), native);
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                using var tracker = new Tracker();
                var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings),
                    new SessionResultsService(db, new SqliteFocusSessionRepository(db)), tracker, focus, coordinator);
                try
                {
                    var pane = (Border)window.FindName("BlockedAppsPane");
                    Assert.AreEqual("자체 제어 · 2개 선택", AutomationProperties.GetItemStatus(pane));
                    var appList = (StackPanel)window.FindName("QuickAppList");
                    Assert.AreEqual(2, appList.Children.Count, "제외된 앱은 목록과 선택 개수에 넣지 않습니다.");
                    Assert.AreEqual("2개 중 2개", ((TextBlock)window.FindName("BlockedAppsCountText")).Text);
                    Button Switch(int index) => ((Grid)((Border)appList.Children[index]).Child).Children.OfType<Button>().Single();
                    Switch(0).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.AreEqual("자체 제어 · 1개 선택", AutomationProperties.GetItemStatus(pane));
                    Assert.AreEqual(false, Switch(0).Tag, "누른 줄은 순서가 바뀌지 않고 그 자리에서 꺼집니다.");
                    coordinator.SwitchBackend(FocusControlBackend.Windows);
                    Assert.AreEqual("Windows 방식", AutomationProperties.GetItemStatus(pane));
                    StringAssert.Contains(((TextBlock)window.FindName("BlockedAppsStatusText")).Text, "Windows 방해 금지 방식");
                    coordinator.SwitchBackend(FocusControlBackend.Altong);
                    Assert.AreEqual("자체 제어 · 1개 선택", AutomationProperties.GetItemStatus(pane));
                }
                finally { window.Close(); }

                string corruptPath = Path.Combine(Path.GetTempPath(), "altong-settings-corrupt-" + Guid.NewGuid() + ".json");
                try
                {
                    File.WriteAllText(corruptPath, "{invalid");
                    var corruptSettings = new FocusSettingsStore(corruptPath);
                    var warningWindow = new DashboardWindow(corruptSettings, new FocusRoutineService(focus, corruptSettings),
                        new SessionResultsService(db, new SqliteFocusSessionRepository(db)), tracker, focus);
                    try
                    {
                        var timerCard = (Button)warningWindow.FindName("QuickTimerCard");
                        Assert.AreEqual("설정 확인 필요", AutomationProperties.GetItemStatus(timerCard));
                        StringAssert.Contains(((TextBlock)warningWindow.FindName("RoutineFeedbackText")).Text, "읽지 못해");
                        timerCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.IsTrue(corruptSettings.Current.TimerEnabled, "카드를 누르면 새 설정으로 다시 저장합니다.");
                        Assert.AreNotEqual("설정 확인 필요", AutomationProperties.GetItemStatus(timerCard));
                    }
                    finally { warningWindow.Close(); }
                }
                finally { if (File.Exists(corruptPath)) File.Delete(corruptPath); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    [TestMethod]
    public void BlockedApps_ShowAllButton_ExpandsInPlace_AndUnknownAppIdHasNoIcon()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var db = SqliteDatabase.CreateInMemory();
                db.Initialize();
                var focus = new FocusModeService();
                using var coordinator = new FocusModeCoordinator(focus, new Observer(), new Launcher(), new ImmediateDispatcher(), new TestNative(extraApps: 5));
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                using var tracker = new Tracker();
                var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings),
                    new SessionResultsService(db, new SqliteFocusSessionRepository(db)), tracker, focus, coordinator);
                try
                {
                    var appList = (StackPanel)window.FindName("QuickAppList");
                    var showAll = (Button)window.FindName("ShowAllAppsButton");
                    Assert.AreEqual(5, appList.Children.Count, "접혀 있으면 다섯 개만 보입니다.");
                    Assert.AreEqual(Visibility.Visible, showAll.Visibility);
                    Assert.AreEqual("앱 목록 전체 보기 · 7개", showAll.Content);
                    showAll.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.AreEqual(7, appList.Children.Count, "다른 화면으로 가지 않고 이 칸에서 전부 펼칩니다.");
                    Assert.AreEqual("접기", showAll.Content);
                    showAll.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.AreEqual(5, appList.Children.Count);
                    Assert.IsNull(AppIdIconResolver.Resolve("Altong.Tests.NoSuchApp_" + Guid.NewGuid().ToString("N")),
                        "없는 앱 ID는 예외 없이 null을 돌려 이름 기반 아이콘으로 넘어갑니다.");
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    private sealed class TestNative : INativeFocusController
    {
        private readonly HashSet<string> _excluded = [];
        public bool IsActive => false;
        public string Status => "준비됨";
        public FocusControlBackend Backend { get; private set; } = FocusControlBackend.Altong;
        public TestNative(int extraApps = 0) => Senders =
            [new("app1", "앱 1", null), new("app2", "앱 2", null), new("system", "시스템", "제외"),
             .. Enumerable.Range(3, extraApps).Select(i => new NotificationSender($"app{i}", $"앱 {i}", null))];
        public IReadOnlyList<NotificationSender> Senders { get; }
        public bool IsSelected(string id) => !_excluded.Contains(id);
        public void SetSelected(string id, bool selected) { if (selected) _excluded.Remove(id); else _excluded.Add(id); }
        public void SetBackend(FocusControlBackend backend) => Backend = backend;
        public void Refresh() { }
        public void Start() { }
        public void Stop() { }
        public void CheckHealth() { }
        public void Dispose() { }
    }
    private sealed class Observer : IWindowsNotificationModeObserver
    {
        public WindowsNotificationModeState CurrentState => WindowsNotificationModeState.Unknown;
        public event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged { add { } remove { } }
        public void Start() { }
        public WindowsNotificationModeState Refresh() => CurrentState;
        public void Dispose() { }
    }
    private sealed class Launcher : IWindowsNotificationSettingsLauncher { public bool TryOpen(out string? errorMessage) { errorMessage = null; return true; } }
    private sealed class ImmediateDispatcher : IUiDispatcher { public void Invoke(Action action) => action(); }
    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public CurrentContext CaptureNow() => CurrentContext;
    }
}

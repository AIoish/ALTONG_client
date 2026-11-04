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
                    var section = (Expander)window.FindName("NotificationSettingsExpander");
                    Assert.AreEqual("자체 제어 · 2개 선택", AutomationProperties.GetItemStatus(section));
                    var senderList = (StackPanel)window.FindName("NotificationSenderList");
                    Assert.AreEqual(2, senderList.Children.Count, "제외된 앱은 선택 개수에 포함하지 않습니다.");
                    var first = (CheckBox)senderList.Children[0];
                    first.IsChecked = false;
                    first.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.AreEqual("자체 제어 · 1개 선택", AutomationProperties.GetItemStatus(section));
                    section.IsExpanded = true;
                    section.IsExpanded = false;
                    Assert.AreEqual("자체 제어 · 1개 선택", AutomationProperties.GetItemStatus(section));
                    coordinator.SwitchBackend(FocusControlBackend.Windows);
                    Assert.AreEqual("Windows 방식", AutomationProperties.GetItemStatus(section));
                    coordinator.SwitchBackend(FocusControlBackend.Altong);
                    Assert.AreEqual("자체 제어 · 1개 선택", AutomationProperties.GetItemStatus(section));
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
                        var section = (Expander)warningWindow.FindName("TimerSettingsExpander");
                        Assert.AreEqual("설정 확인 필요", AutomationProperties.GetItemStatus(section));
                        ((Button)warningWindow.FindName("SaveTimerSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreEqual("타이머 꺼짐", AutomationProperties.GetItemStatus(section));
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

    private sealed class TestNative : INativeFocusController
    {
        private readonly HashSet<string> _excluded = [];
        public bool IsActive => false;
        public string Status => "준비됨";
        public FocusControlBackend Backend { get; private set; } = FocusControlBackend.Altong;
        public IReadOnlyList<NotificationSender> Senders { get; } =
            [new("app1", "앱 1", null), new("app2", "앱 2", null), new("system", "시스템", "제외")];
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

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Altong.Client.Tests;

/// <summary>대시보드의 "Windows 시작 시 알통 실행" 토글이 실제로 등록·해제하는지 테스트 전용 HKCU 키로 확인한다.</summary>
[TestClass]
public sealed class LaunchAtStartupUiTests
{
    private const string TestRoot = @"Software\AltongTests";

    [TestMethod]
    public void Toggle_RegistersAndUnregisters()
    {
        string runKey = $@"{TestRoot}\Run-{Guid.NewGuid():N}";
        try
        {
            RunOnSta(() =>
            {
                using var db = SqliteDatabase.CreateInMemory();
                db.Initialize();
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var focus = new FocusModeService();
                var registration = new StartupRegistration(@"C:\Apps\Altong.Client.exe", runKey);
                var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings),
                    new SessionResultsService(db, new SqliteFocusSessionRepository(db)), new Tracker(), focus,
                    startupRegistration: registration);
                try
                {
                    var card = (Button)window.FindName("QuickStartupCard");
                    var text = (TextBlock)window.FindName("QuickStartupText");
                    Assert.AreEqual("자동 실행 꺼짐", System.Windows.Automation.AutomationProperties.GetItemStatus(card));
                    Assert.IsTrue(card.IsEnabled);
                    Assert.AreEqual(false, card.Tag, "기본은 꺼짐");

                    card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.IsTrue(registration.IsEnabled, text.Text);
                    Assert.AreEqual(true, card.Tag);
                    Assert.AreEqual("자동 실행 켜짐", System.Windows.Automation.AutomationProperties.GetItemStatus(card));

                    card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.IsFalse(registration.IsEnabled, text.Text);
                    Assert.AreEqual("자동 실행 꺼짐", System.Windows.Automation.AutomationProperties.GetItemStatus(card));
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(runKey, throwOnMissingSubKey: false);
            using (var root = Registry.CurrentUser.OpenSubKey(TestRoot))
                if (root is { SubKeyCount: 0, ValueCount: 0 })
                    Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
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
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF 테스트가 시간 내 끝나야 합니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Dispose() { }
    }
}

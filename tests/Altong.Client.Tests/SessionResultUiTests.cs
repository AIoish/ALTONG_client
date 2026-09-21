using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SessionResultUiTests
{
    [TestMethod]
    public void ResultButton_IsHiddenUntilReady_AndResultWindowLoads()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var database = SqliteDatabase.CreateInMemory();
                database.Initialize();
                var results = new SessionResultsService(database, new SqliteFocusSessionRepository(database));
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var routine = new FocusRoutineService(new FocusModeService(), settings);
                using var tracker = new StubActiveWindowTracker();
                var window = new DashboardWindow(settings, routine, results, tracker);
                try
                {
                    window.ApplyTemplate();
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(1100, 720));
                    root.Arrange(new Rect(0, 0, 1100, 720));
                    root.UpdateLayout();
                    var tabs = (TabControl)window.FindName("DashboardTabs");
                    tabs.ApplyTemplate();
                    var button = (Button)tabs.Template.FindName("SessionResultButton", tabs);
                    Assert.IsNotNull(button);
                    PumpBindings();
                    Assert.AreEqual(Visibility.Collapsed, button.Visibility);
                    var start = DateTime.UtcNow.AddMinutes(-1);
                    results.Begin(start, 25);
                    results.End(DateTime.UtcNow, CurrentContext.Empty);
                    var completed = results.RefreshAsync();
                    while (!completed.IsCompleted)
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    completed.GetAwaiter().GetResult();
                    PumpBindings();
                    Assert.AreEqual(Visibility.Visible, button.Visibility);
                    var resultWindow = new SessionResultWindow(results.Latest!);
                    Assert.AreSame(results.Latest, resultWindow.DataContext);
                    resultWindow.Close();
                    results.Begin(DateTime.UtcNow, 25);
                    PumpBindings();
                    Assert.AreEqual(Visibility.Collapsed, button.Visibility);
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "WPF check timed out.");
        if (failure is not null) throw new AssertFailedException("Result UI failed.", failure);
    }

    private static void PumpBindings() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class StubActiveWindowTracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

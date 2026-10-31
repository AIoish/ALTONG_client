using Altong.Client.Data;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.UI.Notifications;

namespace Altong.Client.Tests;

[TestClass]
public sealed class WindowsNotificationModeResolverTests
{
    [TestMethod]
    public void RepeatedScreenCapturesWhileOff_DoNotCreateSessionsOrReports()
    {
        using var context = new FocusContext();
        for (int i = 0; i < 20; i++)
        {
            context.Observer.SetNative(ToastNotificationMode.AlarmsOnly, WindowsUserNotificationState.Busy);
            context.Observer.SetNative(ToastNotificationMode.Unrestricted, WindowsUserNotificationState.AcceptsNotifications);
        }
        Assert.IsFalse(context.Focus.IsEnabled);
        Assert.IsNull(context.Results.ActiveSession);
        Assert.AreEqual(0, context.Reports.Count);
        using var connection = context.Database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM focus_sessions";
        Assert.AreEqual(0L, command.ExecuteScalar());
    }

    [TestMethod]
    public void CaptureDuringFocus_KeepsOneSession_AndNormalStopProducesOneReport()
    {
        using var context = new FocusContext();
        context.Observer.SetNative(ToastNotificationMode.PriorityOnly, WindowsUserNotificationState.AcceptsNotifications);
        var id = context.Results.CurrentSessionId;
        Assert.IsNotNull(id);
        for (int i = 0; i < 20; i++)
        {
            context.Observer.SetNative(ToastNotificationMode.AlarmsOnly, WindowsUserNotificationState.Busy);
            context.Observer.SetNative(ToastNotificationMode.PriorityOnly, WindowsUserNotificationState.AcceptsNotifications);
            Assert.AreEqual(id, context.Results.CurrentSessionId);
        }
        Assert.IsTrue(context.Focus.IsEnabled);
        Assert.AreEqual(0, context.Reports.Count);
        context.Observer.SetNative(ToastNotificationMode.Unrestricted, WindowsUserNotificationState.AcceptsNotifications);
        Assert.IsFalse(context.Focus.IsEnabled);
        Assert.AreEqual(1, context.Reports.Count);
        Assert.AreEqual(id, context.Reports.Single().SessionId);
    }

    [TestMethod]
    public void StartingDuringCapture_DoesNotInferFocusFromAnUnknownInitialState()
    {
        var state = WindowsNotificationModeResolver.Resolve(ToastNotificationMode.AlarmsOnly,
            WindowsUserNotificationState.Busy, WindowsNotificationModeState.Unknown);
        Assert.AreEqual(WindowsNotificationModeState.Unknown, state);
        Assert.IsFalse(state.IsRestricted);
    }

    [TestMethod]
    public void ConfirmedAlarmsOnlyWithoutTemporarySuppression_RetainsExistingBehavior()
    {
        var state = WindowsNotificationModeResolver.Resolve(ToastNotificationMode.AlarmsOnly,
            WindowsUserNotificationState.AcceptsNotifications, WindowsNotificationModeState.Unknown);
        Assert.AreEqual(WindowsNotificationModeKind.AlarmsOnly, state.Kind);
        Assert.IsTrue(state.IsRestricted);
    }

    [TestMethod]
    public void ShellReadFailure_DoesNotCreateOrStopAFocusSession()
    {
        foreach (var kind in new[] { WindowsNotificationModeKind.Unrestricted, WindowsNotificationModeKind.PriorityOnly })
        {
            var confirmed = new WindowsNotificationModeState(kind);
            Assert.AreEqual(confirmed, WindowsNotificationModeResolver.Resolve(ToastNotificationMode.AlarmsOnly, null, confirmed));
        }
    }

    [DataTestMethod]
    [DataRow(WindowsUserNotificationState.NotPresent)]
    [DataRow(WindowsUserNotificationState.RunningD3DFullScreen)]
    [DataRow(WindowsUserNotificationState.PresentationMode)]
    [DataRow(WindowsUserNotificationState.QuietTime)]
    public void OtherNativeStates_RetainTheExistingAlarmsOnlyBehavior(WindowsUserNotificationState userState)
    {
        var confirmed = new WindowsNotificationModeState(WindowsNotificationModeKind.Unrestricted);
        Assert.AreEqual(WindowsNotificationModeKind.AlarmsOnly,
            WindowsNotificationModeResolver.Resolve(ToastNotificationMode.AlarmsOnly, userState, confirmed).Kind);
    }

    private sealed class FocusContext : IDisposable
    {
        public SqliteDatabase Database { get; } = SqliteDatabase.CreateInMemory();
        public FocusModeService Focus { get; } = new();
        public ModeObserver Observer { get; } = new();
        public SessionResultsService Results { get; }
        public List<SessionResult> Reports { get; } = [];
        private readonly FocusModeCoordinator _coordinator;

        public FocusContext()
        {
            Database.Initialize();
            Results = new SessionResultsService(Database, new SqliteFocusSessionRepository(Database));
            Focus.StateChanged += (_, _) =>
            {
                if (Focus.IsEnabled) Results.Begin(DateTime.UtcNow, 0);
                else if (Results.CompleteFocusSessionAsync(DateTime.UtcNow).GetAwaiter().GetResult() is { } result)
                    Reports.Add(result);
            };
            _coordinator = new FocusModeCoordinator(Focus, Observer, new SettingsLauncher(), new ImmediateDispatcher());
            _coordinator.Start();
        }

        public void Dispose() { _coordinator.Dispose(); Database.Dispose(); }
    }

    private sealed class ModeObserver : IWindowsNotificationModeObserver
    {
        public WindowsNotificationModeState CurrentState { get; private set; } = new(WindowsNotificationModeKind.Unrestricted);
        public event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged;
        public void SetNative(ToastNotificationMode mode, WindowsUserNotificationState? shellState)
        {
            CurrentState = WindowsNotificationModeResolver.Resolve(mode, shellState, CurrentState);
            ModeChanged?.Invoke(this, new WindowsNotificationModeChangedEventArgs(CurrentState));
        }
        public void Start() => ModeChanged?.Invoke(this, new WindowsNotificationModeChangedEventArgs(CurrentState));
        public WindowsNotificationModeState Refresh() => CurrentState;
        public void Dispose() { }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher { public void Invoke(Action action) => action(); }
    private sealed class SettingsLauncher : IWindowsNotificationSettingsLauncher
    {
        public bool TryOpen(out string? errorMessage) { errorMessage = null; return true; }
    }
}

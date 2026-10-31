using Altong.Client.Services;
using Altong.Client.Services.NotificationControl;
using Altong.Client.Services.Notifications;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NativeFocusCoordinatorTests
{
    [TestMethod]
    public void NativeFocusStartsAndStopsWithoutOpeningWindowsSettings()
    {
        var focus = new FocusModeService(); var native = new Native(); var launcher = new Launcher(); var observer = new Observer();
        using var coordinator = new FocusModeCoordinator(focus, observer, launcher, new Dispatcher(), native);
        coordinator.Start();
        Assert.IsFalse(focus.IsEnabled);
        coordinator.RequestToggle();
        Assert.IsTrue(focus.IsEnabled); Assert.IsTrue(native.IsActive); Assert.AreEqual(0, launcher.OpenCount);
        observer.Change(WindowsNotificationModeKind.AlarmsOnly);
        observer.Change(WindowsNotificationModeKind.Unrestricted);
        Assert.IsTrue(focus.IsEnabled);
        coordinator.RequestToggle();
        Assert.IsFalse(focus.IsEnabled); Assert.IsFalse(native.IsActive); Assert.AreEqual(0, launcher.OpenCount);
    }

    [TestMethod]
    public void FailedApplyDoesNotStartSession_AndFailedRestoreDoesNotPretendToStop()
    {
        var focus = new FocusModeService(); var native = new Native { Fail = true };
        using var coordinator = new FocusModeCoordinator(focus, new Observer(), new Launcher(), new Dispatcher(), native);
        coordinator.Start(); coordinator.RequestToggle();
        Assert.IsFalse(focus.IsEnabled); Assert.IsNotNull(coordinator.Guidance);
        native.Fail = false; coordinator.RequestToggle(); Assert.IsTrue(focus.IsEnabled);
        native.Fail = true; coordinator.RequestToggle(); Assert.IsTrue(focus.IsEnabled); Assert.IsTrue(native.IsActive);
    }

    [TestMethod]
    public void SwitchingBackendRestoresBeforeFollowingWindowsState()
    {
        var focus = new FocusModeService(); var native = new Native(); var observer = new Observer(); var launcher = new Launcher();
        using var coordinator = new FocusModeCoordinator(focus, observer, launcher, new Dispatcher(), native);
        coordinator.Start(); coordinator.RequestToggle();
        coordinator.SwitchBackend(FocusControlBackend.Windows);
        Assert.IsFalse(native.IsActive); Assert.IsFalse(focus.IsEnabled);
        coordinator.RequestToggle(); Assert.AreEqual(1, launcher.OpenCount);
        observer.Change(WindowsNotificationModeKind.AlarmsOnly); Assert.IsTrue(focus.IsEnabled);
        coordinator.SwitchBackend(FocusControlBackend.Altong);
        Assert.IsFalse(focus.IsEnabled); Assert.IsFalse(native.IsActive);
    }

    [TestMethod]
    public void GuardianLossRestoresAndEndsSession()
    {
        var focus = new FocusModeService(); var native = new Native();
        using var coordinator = new FocusModeCoordinator(focus, new Observer(), new Launcher(), new Dispatcher(), native);
        coordinator.Start(); coordinator.RequestToggle();
        native.LoseGuardian = true; coordinator.CheckNativeHealth();
        Assert.IsFalse(focus.IsEnabled); Assert.IsFalse(native.IsActive); Assert.IsNotNull(coordinator.Guidance);
    }

    [DataTestMethod]
    [DataRow(FocusControlBackend.Altong)]
    [DataRow(FocusControlBackend.Windows)]
    public async Task BothBackends_KeepExistingCollectorsPipelineKakaoAudioAndDockDelivery(FocusControlBackend backend)
    {
        var focus = new FocusModeService();
        var native = new Native();
        native.SetBackend(backend);
        var observer = new Observer();
        using var coordinator = new FocusModeCoordinator(focus, observer, new Launcher(), new Dispatcher(), native);
        using var audio = new FakeKakaoAudioOperator();
        var dock = new DockNotificationState();
        const string sessionId = "existing-collector-session";
        focus.StateChanged += (_, _) =>
        {
            if (focus.IsEnabled) { audio.Mute(); dock.BeginSession(sessionId); }
            else { audio.Unmute(); dock.EndSession(); }
        };
        using var standardCollector = new FakeNotificationListener();
        var windows = new FakeKakaoWindowOperator
        {
            TextExtractor = _ => new("가상 팀원", "가상 프로젝트", "[긴급] 서버 장애 확인 부탁해요.")
        };
        nint popup = 456789;
        windows.NotificationWindows.Add(popup);
        using var kakao = new KakaoNotificationInterceptor(() => focus.IsEnabled, windows, audioOperator: audio);
        using var collectors = new CompositeNotificationListener(standardCollector, kakao);
        using var database = SqliteDatabase.CreateInMemory();
        database.Initialize();
        var repository = new SqliteNotificationRepository(database);
        using var pipeline = new NotificationPipelineCoordinator(collectors, new Tracker(), repository,
            new RuleBasedFilterEngine(), () => focus.IsEnabled, () => focus.IsEnabled ? sessionId : null);
        var standardDelivered = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var kakaoDelivered = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.NotificationProcessed += (_, record) =>
        {
            kakao.OnNotificationProcessed(record);
            bool received = dock.Receive(record);
            (record.Id == "existing-standard" ? standardDelivered : kakaoDelivered).TrySetResult(
                received ? record : throw new InvalidOperationException("Existing dock rejected the active session notification."));
        };
        coordinator.Start();
        await pipeline.StartAsync();
        if (backend == FocusControlBackend.Altong) coordinator.RequestToggle();
        else observer.Change(WindowsNotificationModeKind.PriorityOnly);
        Assert.IsTrue(focus.IsEnabled);
        Assert.IsTrue(audio.IsMuted);

        // Both collector contracts remain independent of the chosen DND backend.
        standardCollector.EmitNotification(new("existing-standard", "Chrome", "가상 팀원", "[긴급] 서버 장애", "가상 내용", DateTime.UtcNow));
        var standardRecord = await standardDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        kakao.HandleWindowShowEvent(popup);
        var kakaoRecord = await kakaoDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(sessionId, standardRecord.SessionId);
        Assert.AreEqual(sessionId, kakaoRecord.SessionId);
        Assert.AreEqual("[긴급] 서버 장애 확인 부탁해요.", kakaoRecord.Body);
        Assert.AreEqual(2, dock.Items.Count);
        Assert.IsNotNull(await repository.GetByIdAsync(standardRecord.Id));
        Assert.IsNotNull(await repository.GetByIdAsync(kakaoRecord.Id));
        Assert.IsTrue(windows.HiddenWindows.Contains(popup));
        Assert.IsTrue(windows.ClosedWindows.Contains(popup));

        if (backend == FocusControlBackend.Altong) coordinator.RequestToggle();
        else observer.Change(WindowsNotificationModeKind.Unrestricted);
        Assert.IsFalse(focus.IsEnabled);
        Assert.IsFalse(audio.IsMuted);
        Assert.IsFalse(dock.IsActive);
        Assert.IsTrue(standardCollector.IsRunning, "Changing DND must not stop the existing collector.");
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

    private sealed class Dispatcher : IUiDispatcher { public void Invoke(Action action) => action(); }
    private sealed class Launcher : IWindowsNotificationSettingsLauncher
    {
        public int OpenCount;
        public bool TryOpen(out string? errorMessage) { OpenCount++; errorMessage = null; return true; }
    }
    private sealed class Observer : IWindowsNotificationModeObserver
    {
        public WindowsNotificationModeState CurrentState { get; private set; } = new(WindowsNotificationModeKind.Unrestricted);
        public event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged;
        public void Start() => Change(CurrentState.Kind);
        public WindowsNotificationModeState Refresh() => CurrentState;
        public void Change(WindowsNotificationModeKind kind) { CurrentState = new(kind); ModeChanged?.Invoke(this, new(CurrentState)); }
        public void Dispose() { }
    }
    private sealed class Native : INativeFocusController
    {
        public bool Fail, LoseGuardian;
        public bool IsActive { get; private set; }
        public string Status => "test";
        public FocusControlBackend Backend { get; private set; }
        public IReadOnlyList<NotificationSender> Senders => [];
        public bool IsSelected(string id) => true;
        public void SetSelected(string id, bool selected) { }
        public void SetBackend(FocusControlBackend backend) => Backend = backend;
        public void Refresh() { }
        public void Start() { if (Fail) throw new IOException("apply failed"); IsActive = true; }
        public void Stop() { if (Fail) throw new IOException("restore failed"); IsActive = false; }
        public void CheckHealth() { if (LoseGuardian) { Stop(); throw new IOException("guardian lost"); } }
        public void Dispose() { }
    }
}

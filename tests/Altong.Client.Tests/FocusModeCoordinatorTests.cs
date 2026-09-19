using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class FocusModeCoordinatorTests
{
    [TestMethod]
    public void Start_WhenWindowsModeIsRestricted_EnablesFocusMode()
    {
        using var context = CreateContext(WindowsNotificationModeKind.PriorityOnly);

        context.Coordinator.Start();

        Assert.IsTrue(context.FocusModeService.IsEnabled);
        Assert.AreEqual(
            WindowsNotificationModeKind.PriorityOnly,
            context.Coordinator.WindowsState.Kind);
    }

    [TestMethod]
    public void ExternalModeChange_UpdatesFocusMode()
    {
        using var context = CreateContext(WindowsNotificationModeKind.Unrestricted);
        context.Coordinator.Start();

        context.Observer.SetMode(WindowsNotificationModeKind.AlarmsOnly);
        Assert.IsTrue(context.FocusModeService.IsEnabled);

        context.Observer.SetMode(WindowsNotificationModeKind.Unrestricted);
        Assert.IsFalse(context.FocusModeService.IsEnabled);
    }

    [TestMethod]
    public void DuplicateModeEvent_DoesNotRaiseDuplicateFocusStateChange()
    {
        using var context = CreateContext(WindowsNotificationModeKind.Unrestricted);
        context.Coordinator.Start();
        var focusStateChangedCount = 0;
        context.FocusModeService.StateChanged += (_, _) => focusStateChangedCount++;

        context.Observer.SetMode(WindowsNotificationModeKind.PriorityOnly);
        context.Observer.RaiseCurrentMode();

        Assert.AreEqual(1, focusStateChangedCount);
    }

    [TestMethod]
    public void RequestStart_OpensSettingsAndWaitsForRestrictedMode()
    {
        using var context = CreateContext(WindowsNotificationModeKind.Unrestricted);
        context.Coordinator.Start();

        context.Coordinator.RequestToggle();

        Assert.AreEqual(1, context.SettingsLauncher.OpenCount);
        Assert.IsFalse(context.FocusModeService.IsEnabled);
        Assert.AreEqual(
            FocusModeGuidanceKind.EnableWindowsDnd,
            context.Coordinator.Guidance?.Kind);

        context.Observer.SetMode(WindowsNotificationModeKind.PriorityOnly);

        Assert.IsTrue(context.FocusModeService.IsEnabled);
        Assert.IsNull(context.Coordinator.Guidance);
    }

    [TestMethod]
    public void RequestStop_OpensSettingsAndWaitsForUnrestrictedMode()
    {
        using var context = CreateContext(WindowsNotificationModeKind.PriorityOnly);
        context.Coordinator.Start();

        context.Coordinator.RequestToggle();

        Assert.AreEqual(1, context.SettingsLauncher.OpenCount);
        Assert.IsTrue(context.FocusModeService.IsEnabled);
        Assert.AreEqual(
            FocusModeGuidanceKind.DisableWindowsDnd,
            context.Coordinator.Guidance?.Kind);

        context.Observer.SetMode(WindowsNotificationModeKind.Unrestricted);

        Assert.IsFalse(context.FocusModeService.IsEnabled);
        Assert.IsNull(context.Coordinator.Guidance);
    }

    [TestMethod]
    public void CancelGuidance_DoesNotChangeFocusMode()
    {
        using var context = CreateContext(WindowsNotificationModeKind.Unrestricted);
        context.Coordinator.Start();
        context.Coordinator.RequestToggle();

        context.Coordinator.CancelGuidance();

        Assert.IsNull(context.Coordinator.Guidance);
        Assert.IsFalse(context.FocusModeService.IsEnabled);
    }

    [DataTestMethod]
    [DataRow(WindowsNotificationModeKind.Unsupported)]
    [DataRow(WindowsNotificationModeKind.Error)]
    public void UnsupportedOrError_DoesNotChangeFocusAndExplainsNoManualFallback(
        WindowsNotificationModeKind mode)
    {
        using var context = CreateContext(mode);
        context.Coordinator.Start();

        context.Coordinator.RequestToggle();

        Assert.IsFalse(context.FocusModeService.IsEnabled);
        Assert.AreEqual(0, context.SettingsLauncher.OpenCount);
        Assert.AreEqual(
            FocusModeGuidanceKind.Unavailable,
            context.Coordinator.Guidance?.Kind);
        StringAssert.Contains(
            context.Coordinator.Guidance?.Message,
            "Altong만 따로 수동 전환하지 않습니다");
    }

    [TestMethod]
    public void Dispose_UnsubscribesFromObserver()
    {
        var context = CreateContext(WindowsNotificationModeKind.Unrestricted);
        context.Coordinator.Start();

        context.Coordinator.Dispose();
        context.Observer.SetMode(WindowsNotificationModeKind.PriorityOnly);

        Assert.IsFalse(context.FocusModeService.IsEnabled);
        Assert.AreEqual(0, context.Observer.SubscriberCount);
        Assert.IsTrue(context.Observer.IsDisposed);
    }

    [TestMethod]
    public void SettingsLaunchFailure_KeepsCancelableGuidance()
    {
        using var context = CreateContext(WindowsNotificationModeKind.Unrestricted);
        context.SettingsLauncher.ShouldSucceed = false;
        context.Coordinator.Start();

        context.Coordinator.RequestToggle();

        Assert.AreEqual(
            FocusModeGuidanceKind.EnableWindowsDnd,
            context.Coordinator.Guidance?.Kind);
        StringAssert.Contains(
            context.Coordinator.Guidance?.Message,
            "설정을 열지 못했습니다");
    }

    private static TestContext CreateContext(WindowsNotificationModeKind initialMode)
    {
        var focusModeService = new FocusModeService();
        var observer = new FakeWindowsNotificationModeObserver(initialMode);
        var settingsLauncher = new FakeWindowsNotificationSettingsLauncher();
        var coordinator = new FocusModeCoordinator(
            focusModeService,
            observer,
            settingsLauncher,
            new ImmediateDispatcher());

        return new TestContext(
            focusModeService,
            observer,
            settingsLauncher,
            coordinator);
    }

    private sealed record TestContext(
        FocusModeService FocusModeService,
        FakeWindowsNotificationModeObserver Observer,
        FakeWindowsNotificationSettingsLauncher SettingsLauncher,
        FocusModeCoordinator Coordinator) : IDisposable
    {
        public void Dispose()
        {
            Coordinator.Dispose();
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Invoke(Action action)
        {
            action();
        }
    }

    private sealed class FakeWindowsNotificationSettingsLauncher :
        IWindowsNotificationSettingsLauncher
    {
        public bool ShouldSucceed { get; set; } = true;

        public int OpenCount { get; private set; }

        public bool TryOpen(out string? errorMessage)
        {
            OpenCount++;
            errorMessage = ShouldSucceed ? null : "설정을 열지 못했습니다.";
            return ShouldSucceed;
        }
    }

    private sealed class FakeWindowsNotificationModeObserver :
        IWindowsNotificationModeObserver
    {
        private EventHandler<WindowsNotificationModeChangedEventArgs>? _modeChanged;

        public FakeWindowsNotificationModeObserver(WindowsNotificationModeKind initialMode)
        {
            CurrentState = CreateState(initialMode);
        }

        public WindowsNotificationModeState CurrentState { get; private set; }

        public int SubscriberCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged
        {
            add
            {
                _modeChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _modeChanged -= value;
                SubscriberCount--;
            }
        }

        public void Start()
        {
            RaiseCurrentMode();
        }

        public WindowsNotificationModeState Refresh()
        {
            return CurrentState;
        }

        public void SetMode(WindowsNotificationModeKind mode)
        {
            CurrentState = CreateState(mode);
            RaiseCurrentMode();
        }

        public void RaiseCurrentMode()
        {
            _modeChanged?.Invoke(
                this,
                new WindowsNotificationModeChangedEventArgs(CurrentState));
        }

        public void Dispose()
        {
            IsDisposed = true;
        }

        private static WindowsNotificationModeState CreateState(
            WindowsNotificationModeKind mode)
        {
            return new WindowsNotificationModeState(
                mode,
                mode is WindowsNotificationModeKind.Unsupported or
                    WindowsNotificationModeKind.Error
                    ? "테스트 상태"
                    : null);
        }
    }
}

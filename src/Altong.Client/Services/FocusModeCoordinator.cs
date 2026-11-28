using Altong.Client.Services.NotificationControl;

namespace Altong.Client.Services;

/// <summary>
/// 사용자의 전환 요청과 Windows 알림 모드 관찰 결과를 Altong 집중 상태에 연결한다.
/// </summary>
public sealed class FocusModeCoordinator : IDisposable
{
    private readonly FocusModeService _focusModeService;
    private readonly IWindowsNotificationModeObserver _notificationModeObserver;
    private readonly IWindowsNotificationSettingsLauncher _settingsLauncher;
    private readonly IUiDispatcher _dispatcher;
    private bool _isStarted;
    private bool _isDisposed;
    public INativeFocusController? NativeControl { get; }
    public FocusControlBackend Backend => NativeControl?.Backend ?? FocusControlBackend.Windows;

    public FocusModeCoordinator(
        FocusModeService focusModeService,
        IWindowsNotificationModeObserver notificationModeObserver,
        IWindowsNotificationSettingsLauncher settingsLauncher,
        IUiDispatcher dispatcher,
        INativeFocusController? nativeControl = null)
    {
        _focusModeService = focusModeService;
        _notificationModeObserver = notificationModeObserver;
        _settingsLauncher = settingsLauncher;
        _dispatcher = dispatcher;
        NativeControl = nativeControl;
    }

    public WindowsNotificationModeState WindowsState { get; private set; } =
        WindowsNotificationModeState.Unknown;

    public FocusModeGuidance? Guidance { get; private set; }

    public event EventHandler? StateChanged;

    public void Start()
    {
        ThrowIfDisposed();

        if (_isStarted)
        {
            return;
        }

        _isStarted = true;
        _notificationModeObserver.ModeChanged += NotificationModeObserver_ModeChanged;
        _notificationModeObserver.Start();
    }

    public void RequestToggle()
    {
        ThrowIfDisposed();
        _dispatcher.Invoke(RequestToggleOnUiThread);
    }

    /// <summary>설정 창 안내 없이 바로 집중을 켤 수 있는지 (알통 자체 제어 방식).</summary>
    public bool CanStartDirectly => Backend == FocusControlBackend.Altong && NativeControl is not null;

    /// <summary>
    /// 알통 자체 제어로 실제로 제어할 앱이 하나 이상 있는지. 검증되지 않은 Windows 빌드에서는 앱 목록이 비어 있고,
    /// 사용자가 모든 앱을 뺐을 때도 false다. 이때 켜기를 시도하면 오류 안내만 뜨므로 자동 제안을 하지 않는다.
    /// </summary>
    public bool HasControllableApps =>
        NativeControl is { } control && control.Senders.Any(s => s.IsEligible && control.IsSelected(s.Id));

    /// <summary>
    /// 집중 모드를 켜기만 한다. 이미 켜져 있으면 아무것도 하지 않아 토글처럼 반대로 꺼지는 일이 없다.
    /// 알통 자체 제어 방식에서만 동작하며, 켰으면 true를 돌려준다.
    /// </summary>
    public bool RequestStart()
    {
        ThrowIfDisposed();
        bool started = false;
        _dispatcher.Invoke(() =>
        {
            if (!CanStartDirectly || _focusModeService.IsEnabled) return;
            StartAltongFocus();
            started = _focusModeService.IsEnabled;
        });
        return started;
    }

    /// <summary>
    /// 집중 모드를 끄기만 한다. 이미 꺼져 있으면 아무것도 하지 않는다. 알통 자체 제어 방식에서만 동작하며
    /// (Windows 방식은 사용자가 직접 방해 금지를 꺼야 함), 껐으면 true를 돌려준다.
    /// </summary>
    public bool RequestStop()
    {
        ThrowIfDisposed();
        bool stopped = false;
        _dispatcher.Invoke(() =>
        {
            if (!CanStartDirectly || !_focusModeService.IsEnabled) return;
            StopAltongFocus();
            stopped = !_focusModeService.IsEnabled;
        });
        return stopped;
    }

    public void CancelGuidance()
    {
        ThrowIfDisposed();
        _dispatcher.Invoke(() => SetGuidance(null));
    }

    public void SwitchBackend(FocusControlBackend backend)
    {
        ThrowIfDisposed();
        if (NativeControl is null || Backend == backend) return;
        _dispatcher.Invoke(() =>
        {
            try
            {
                NativeControl.Stop();
                _focusModeService.Stop();
                NativeControl.SetBackend(backend);
                Guidance = null;
                WindowsState = WindowsNotificationModeState.Unknown;
                ApplyWindowsState(_notificationModeObserver.Refresh());
            }
            catch (Exception ex) { ShowNativeError(ex); }
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void RefreshNativeControl()
    {
        NativeControl?.Refresh();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RetryNativeRecovery()
    {
        try
        {
            NativeControl?.Stop();
            if (Backend == FocusControlBackend.Altong) _focusModeService.Stop();
            SetGuidance(null);
        }
        catch (Exception ex) { ShowNativeError(ex); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CheckNativeHealth()
    {
        if (Backend != FocusControlBackend.Altong || NativeControl is null) return;
        try { NativeControl.CheckHealth(); }
        catch (Exception ex)
        {
            if (!NativeControl.IsActive) _focusModeService.Stop();
            ShowNativeError(ex);
        }
    }

    private void ShowNativeError(Exception ex) => SetGuidance(new(FocusModeGuidanceKind.Unavailable, ex.Message, false));

    public void OpenSettingsAgain()
    {
        ThrowIfDisposed();

        _dispatcher.Invoke(() =>
        {
            if (Guidance is not
                {
                    Kind: FocusModeGuidanceKind.EnableWindowsDnd or
                          FocusModeGuidanceKind.DisableWindowsDnd,
                })
            {
                return;
            }

            OpenSettingsFor(Guidance.Kind);
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notificationModeObserver.ModeChanged -= NotificationModeObserver_ModeChanged;
        _notificationModeObserver.Dispose();
        NativeControl?.Dispose();
    }

    private void RequestToggleOnUiThread()
    {
        if (CanStartDirectly)
        {
            if (_focusModeService.IsEnabled)
            {
                StopAltongFocus();
            }
            else
            {
                StartAltongFocus();
            }
            return;
        }
        var state = _notificationModeObserver.Refresh();
        ApplyWindowsState(state);

        switch (state.Kind)
        {
            case WindowsNotificationModeKind.Unrestricted:
                OpenSettingsFor(FocusModeGuidanceKind.EnableWindowsDnd);
                break;

            case WindowsNotificationModeKind.PriorityOnly:
            case WindowsNotificationModeKind.AlarmsOnly:
                OpenSettingsFor(FocusModeGuidanceKind.DisableWindowsDnd);
                break;

            case WindowsNotificationModeKind.Unsupported:
            case WindowsNotificationModeKind.Error:
            case WindowsNotificationModeKind.Unknown:
                SetGuidance(new FocusModeGuidance(
                    FocusModeGuidanceKind.Unavailable,
                    BuildUnavailableMessage(state),
                    false));
                break;
        }
    }

    private void StartAltongFocus()
    {
        try
        {
            NativeControl!.Start();
            _focusModeService.Start();
            SetGuidance(null);
        }
        catch (Exception ex) { ShowNativeError(ex); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopAltongFocus()
    {
        try
        {
            NativeControl!.Stop();
            _focusModeService.Stop();
            SetGuidance(null);
        }
        catch (Exception ex) { ShowNativeError(ex); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSettingsFor(FocusModeGuidanceKind kind)
    {
        var message = kind == FocusModeGuidanceKind.EnableWindowsDnd
            ? "Windows에서 ‘방해 금지’를 켜 주세요.\n확인되면 집중 모드가 시작됩니다."
            : "Windows에서 ‘방해 금지’를 꺼 주세요.\n확인되면 집중 모드가 종료됩니다.";

        if (!_settingsLauncher.TryOpen(out var errorMessage))
        {
            message = $"{message}\n\n{errorMessage}";
        }

        SetGuidance(new FocusModeGuidance(kind, message, true));
    }

    private void NotificationModeObserver_ModeChanged(
        object? sender,
        WindowsNotificationModeChangedEventArgs e)
    {
        _dispatcher.Invoke(() => ApplyWindowsState(e.State));
    }

    private void ApplyWindowsState(WindowsNotificationModeState state)
    {
        if (state == WindowsState)
        {
            return;
        }

        WindowsState = state;

        // Windows DND is informational in Altong mode; external OS changes cannot toggle our session.
        if (Backend == FocusControlBackend.Altong)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (state.Kind)
        {
            case WindowsNotificationModeKind.Unrestricted:
                _focusModeService.Stop();

                if (Guidance?.Kind == FocusModeGuidanceKind.DisableWindowsDnd)
                {
                    Guidance = null;
                }

                break;

            case WindowsNotificationModeKind.PriorityOnly:
            case WindowsNotificationModeKind.AlarmsOnly:
                _focusModeService.Start();

                if (Guidance?.Kind == FocusModeGuidanceKind.EnableWindowsDnd)
                {
                    Guidance = null;
                }

                break;

            case WindowsNotificationModeKind.Unsupported:
            case WindowsNotificationModeKind.Error:
                if (Guidance is not null)
                {
                    Guidance = new FocusModeGuidance(
                        FocusModeGuidanceKind.Unavailable,
                        BuildUnavailableMessage(state),
                        false);
                }

                break;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetGuidance(FocusModeGuidance? guidance)
    {
        if (Guidance == guidance)
        {
            return;
        }

        Guidance = guidance;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildUnavailableMessage(WindowsNotificationModeState state)
    {
        var reason = state.Detail ?? "Windows 알림 모드 상태를 확인할 수 없습니다.";
        return $"{reason}\n\nAltong 집중 모드는 변경되지 않았습니다. 이 환경에서는 Altong만 따로 수동 전환하지 않습니다.";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

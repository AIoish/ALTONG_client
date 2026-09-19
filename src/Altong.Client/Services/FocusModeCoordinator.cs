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

    public FocusModeCoordinator(
        FocusModeService focusModeService,
        IWindowsNotificationModeObserver notificationModeObserver,
        IWindowsNotificationSettingsLauncher settingsLauncher,
        IUiDispatcher dispatcher)
    {
        _focusModeService = focusModeService;
        _notificationModeObserver = notificationModeObserver;
        _settingsLauncher = settingsLauncher;
        _dispatcher = dispatcher;
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

    public void CancelGuidance()
    {
        ThrowIfDisposed();
        _dispatcher.Invoke(() => SetGuidance(null));
    }

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
    }

    private void RequestToggleOnUiThread()
    {
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

using Windows.Foundation.Metadata;
using Windows.UI.Notifications;

namespace Altong.Client.Services;

/// <summary>
/// 현재 사용자의 Windows 알림 허용 모드를 관찰한다.
/// 방해 금지 설정을 변경하지는 않는다.
/// </summary>
public sealed class WindowsNotificationModeObserver : IWindowsNotificationModeObserver
{
    private const string ManagerRuntimeClass =
        "Windows.UI.Notifications.ToastNotificationManagerForUser";

    private readonly object _syncRoot = new();
    private ToastNotificationManagerForUser? _manager;
    private WindowsNotificationModeState _currentState = WindowsNotificationModeState.Unknown;
    private bool _hasPublishedState;
    private bool _isStarted;
    private bool _isDisposed;

    public WindowsNotificationModeState CurrentState
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentState;
            }
        }
    }

    public event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged;

    public void Start()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isStarted)
            {
                return;
            }

            _isStarted = true;
        }

        if (!IsApiSupported())
        {
            Publish(new WindowsNotificationModeState(
                WindowsNotificationModeKind.Unsupported,
                "현재 Windows 버전에서는 알림 모드 감지 API를 지원하지 않습니다."));
            return;
        }

        try
        {
            var manager = ToastNotificationManager.GetDefault();
            manager.NotificationModeChanged += Manager_NotificationModeChanged;

            lock (_syncRoot)
            {
                if (_isDisposed)
                {
                    manager.NotificationModeChanged -= Manager_NotificationModeChanged;
                    return;
                }

                _manager = manager;
            }

            Publish(ReadState(manager));
        }
        catch (Exception exception)
        {
            Publish(CreateErrorState(exception));
        }
    }

    public WindowsNotificationModeState Refresh()
    {
        if (!_isStarted)
        {
            Start();
            return CurrentState;
        }

        ToastNotificationManagerForUser? manager;

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            manager = _manager;
        }

        if (manager is null)
        {
            return CurrentState;
        }

        var state = ReadState(manager);
        Publish(state);
        return state;
    }

    public void Dispose()
    {
        ToastNotificationManagerForUser? manager;

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            manager = _manager;
            _manager = null;
        }

        if (manager is not null)
        {
            manager.NotificationModeChanged -= Manager_NotificationModeChanged;
        }
    }

    private static bool IsApiSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 23504)
            && ApiInformation.IsTypePresent(ManagerRuntimeClass)
            && ApiInformation.IsPropertyPresent(ManagerRuntimeClass, "NotificationMode")
            && ApiInformation.IsEventPresent(ManagerRuntimeClass, "NotificationModeChanged");
    }

    private static WindowsNotificationModeState ReadState(
        ToastNotificationManagerForUser manager)
    {
        try
        {
            return manager.NotificationMode switch
            {
                ToastNotificationMode.Unrestricted =>
                    new(WindowsNotificationModeKind.Unrestricted),
                ToastNotificationMode.PriorityOnly =>
                    new(WindowsNotificationModeKind.PriorityOnly),
                ToastNotificationMode.AlarmsOnly =>
                    new(WindowsNotificationModeKind.AlarmsOnly),
                _ => new(
                    WindowsNotificationModeKind.Unknown,
                    "Windows가 알 수 없는 알림 모드를 반환했습니다."),
            };
        }
        catch (Exception exception)
        {
            return CreateErrorState(exception);
        }
    }

    private static WindowsNotificationModeState CreateErrorState(Exception exception)
    {
        return new(
            WindowsNotificationModeKind.Error,
            $"Windows 알림 모드를 확인하지 못했습니다. ({exception.GetType().Name})");
    }

    private void Manager_NotificationModeChanged(
        ToastNotificationManagerForUser sender,
        object args)
    {
        Publish(ReadState(sender));
    }

    private void Publish(WindowsNotificationModeState state)
    {
        EventHandler<WindowsNotificationModeChangedEventArgs>? handler;

        lock (_syncRoot)
        {
            if (_isDisposed || (_hasPublishedState && state == _currentState))
            {
                return;
            }

            _currentState = state;
            _hasPublishedState = true;
            handler = ModeChanged;
        }

        handler?.Invoke(this, new WindowsNotificationModeChangedEventArgs(state));
    }
}

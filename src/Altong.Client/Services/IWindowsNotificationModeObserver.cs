namespace Altong.Client.Services;

public interface IWindowsNotificationModeObserver : IDisposable
{
    WindowsNotificationModeState CurrentState { get; }

    event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged;

    void Start();

    WindowsNotificationModeState Refresh();
}

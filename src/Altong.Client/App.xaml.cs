using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class App : System.Windows.Application
{
    public FocusModeService FocusModeService { get; } = new();

    internal bool IsShuttingDown { get; private set; }

    private MainWindow? _mainWindow;
    private NotificationDockWindow? _notificationDockWindow;
    private TrayIconService? _trayIconService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;

        _notificationDockWindow = new NotificationDockWindow();
        _notificationDockWindow.OpenRequested += NotificationDockWindow_OpenRequested;

        _trayIconService = new TrayIconService(
            ShowMainWindow,
            ToggleFocusMode,
            RequestShutdown);

        FocusModeService.StateChanged += FocusModeService_StateChanged;
        UpdateFocusModeShell();

        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FocusModeService.StateChanged -= FocusModeService_StateChanged;

        if (_notificationDockWindow is not null)
        {
            _notificationDockWindow.OpenRequested -= NotificationDockWindow_OpenRequested;
        }

        _trayIconService?.Dispose();
        _trayIconService = null;

        base.OnExit(e);
    }

    internal void ShowMainWindow()
    {
        RunOnUiThread(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            _mainWindow.Show();

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        });
    }

    private void ToggleFocusMode()
    {
        RunOnUiThread(FocusModeService.Toggle);
    }

    private void RequestShutdown()
    {
        RunOnUiThread(() =>
        {
            IsShuttingDown = true;

            _trayIconService?.Dispose();
            _trayIconService = null;

            _notificationDockWindow?.Close();
            _mainWindow?.Close();
            Shutdown();
        });
    }

    private void FocusModeService_StateChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(UpdateFocusModeShell);
    }

    private void NotificationDockWindow_OpenRequested(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void UpdateFocusModeShell()
    {
        if (_notificationDockWindow is null)
        {
            return;
        }

        if (FocusModeService.IsEnabled)
        {
            _notificationDockWindow.PositionOnPrimaryWorkArea();

            if (!_notificationDockWindow.IsVisible)
            {
                _notificationDockWindow.Show();
            }
        }
        else
        {
            _notificationDockWindow.Hide();
        }

        _trayIconService?.UpdateFocusModeState(FocusModeService.IsEnabled);
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.Invoke(action);
    }
}

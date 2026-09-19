using System.ComponentModel;
using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class MainWindow : Window
{
    private readonly FocusModeService _focusModeService;
    private readonly FocusModeCoordinator _focusModeCoordinator;

    private DebugConsoleWindow? _debugConsoleWindow;

    public MainWindow(
        FocusModeService focusModeService,
        FocusModeCoordinator focusModeCoordinator)
    {
        InitializeComponent();

        _focusModeService = focusModeService;
        _focusModeCoordinator = focusModeCoordinator;
        _focusModeService.StateChanged += FocusModeService_StateChanged;
        _focusModeCoordinator.StateChanged += FocusModeCoordinator_StateChanged;
        UpdateFocusModeView();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App { IsShuttingDown: false })
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _debugConsoleWindow?.Close();
        _debugConsoleWindow = null;

        _focusModeService.StateChanged -= FocusModeService_StateChanged;
        _focusModeCoordinator.StateChanged -= FocusModeCoordinator_StateChanged;
        base.OnClosed(e);
    }

    private void OpenDebugConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        if (_debugConsoleWindow is null)
        {
            _debugConsoleWindow = new DebugConsoleWindow(app.ActiveWindowTracker);
        }

        _debugConsoleWindow.Show();
        if (_debugConsoleWindow.WindowState == WindowState.Minimized)
        {
            _debugConsoleWindow.WindowState = WindowState.Normal;
        }

        _debugConsoleWindow.Activate();
    }

    private void FocusModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusModeCoordinator.RequestToggle();
    }

    private void FocusModeService_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateFocusModeView);
            return;
        }

        UpdateFocusModeView();
    }

    private void FocusModeCoordinator_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateFocusModeView);
            return;
        }

        UpdateFocusModeView();
    }

    private void UpdateFocusModeView()
    {
        FocusModeStatusText.Text = _focusModeService.IsEnabled
            ? "집중 모드 켜짐"
            : "집중 모드 꺼짐";

        FocusModeToggleButton.Content = _focusModeService.IsEnabled
            ? "집중 모드 끄기"
            : "집중 모드 켜기";

        WindowsNotificationModeText.Text =
            GetWindowsNotificationModeDescription(_focusModeCoordinator.WindowsState);
    }

    private static string GetWindowsNotificationModeDescription(
        WindowsNotificationModeState state)
    {
        return state.Kind switch
        {
            WindowsNotificationModeKind.Unrestricted =>
                "Windows 방해 금지: 꺼짐",
            WindowsNotificationModeKind.PriorityOnly =>
                "Windows 방해 금지: 켜짐",
            WindowsNotificationModeKind.AlarmsOnly =>
                "Windows 방해 금지: 켜짐",
            WindowsNotificationModeKind.Unsupported =>
                "현재 Windows에서는 방해 금지 연동을 지원하지 않습니다.",
            WindowsNotificationModeKind.Error =>
                "Windows 방해 금지 상태를 확인할 수 없습니다.",
            _ => "Windows 방해 금지 상태를 확인하는 중입니다.",
        };
    }
}

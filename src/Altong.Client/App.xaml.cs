using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;

namespace Altong.Client;

public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    public FocusModeService FocusModeService { get; } = new();
    public IActiveWindowTracker ActiveWindowTracker { get; } = new ActiveWindowTracker();

    public IAltongDatabase Database { get; private set; } = null!;
    public INotificationRepository NotificationRepository { get; private set; } = null!;
    public IWindowSessionRepository WindowSessionRepository { get; private set; } = null!;
    public IFocusSessionRepository FocusSessionRepository { get; private set; } = null!;

    internal bool IsShuttingDown { get; private set; }

    private MainWindow? _mainWindow;
    private DashboardWindow? _dashboardWindow;
    private NotificationDockWindow? _notificationDockWindow;
    private WindowsDndGuidanceWindow? _windowsDndGuidanceWindow;
    private TrayIconService? _trayIconService;
    private FocusModeCoordinator? _focusModeCoordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WinExe(GUI 앱)에서 dotnet run을 실행한 부모 터미널로 콘솔 출력 연결
        if (AttachConsole(AttachParentProcess))
        {
            var stdOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdOut);
            Console.WriteLine("\n[Altong] 터미널 콘솔 로그 연결 완료 (ActiveWindow 실시간 추적 시작)");
        }

        // 로컬 SQLite 데이터베이스 초기화 및 저장소 바인딩
        Database = new SqliteDatabase();
        Database.Initialize();

        NotificationRepository = new SqliteNotificationRepository(Database);
        WindowSessionRepository = new SqliteWindowSessionRepository(Database);
        FocusSessionRepository = new SqliteFocusSessionRepository(Database);

        // ActiveWindowTracker의 세션 종료([OUT]) 이벤트를 수신하여 window_sessions에 자동 적재
        ActiveWindowTracker.WindowSessionEnded += async (_, e) =>
        {
            try
            {
                var record = new WindowSessionRecord(
                    0, e.ProcessName, e.WindowTitle,
                    e.StartedAt.UtcDateTime, e.EndedAt.UtcDateTime, e.DurationSeconds);
                await WindowSessionRepository.InsertAsync(record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Database] 세션 저장 실패: {ex.Message}");
            }
        };

        _focusModeCoordinator = new FocusModeCoordinator(
            FocusModeService,
            new WindowsNotificationModeObserver(),
            new WindowsNotificationSettingsLauncher(),
            new WpfUiDispatcher(Dispatcher));

        _mainWindow = new MainWindow(FocusModeService, _focusModeCoordinator);
        MainWindow = _mainWindow;

        _notificationDockWindow = new NotificationDockWindow();
        _notificationDockWindow.ToggleMainWindowRequested +=
            NotificationDockWindow_ToggleMainWindowRequested;

        _windowsDndGuidanceWindow = new WindowsDndGuidanceWindow();
        _windowsDndGuidanceWindow.CancelRequested +=
            WindowsDndGuidanceWindow_CancelRequested;
        _windowsDndGuidanceWindow.OpenSettingsRequested +=
            WindowsDndGuidanceWindow_OpenSettingsRequested;

        _trayIconService = new TrayIconService(
            ShowMainWindow,
            RequestFocusModeChange,
            RequestShutdown);
        _trayIconService.SetDashboardAction(ShowDashboard);

        FocusModeService.StateChanged += FocusModeService_StateChanged;
        _focusModeCoordinator.StateChanged += FocusModeCoordinator_StateChanged;
        _focusModeCoordinator.Start();
        ActiveWindowTracker.Start();
        UpdateFocusModeShell();
        UpdateWindowsDndGuidance();

        _mainWindow.Show();
        _mainWindow.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FocusModeService.StateChanged -= FocusModeService_StateChanged;
        ActiveWindowTracker.Dispose();

        if (_focusModeCoordinator is not null)
        {
            _focusModeCoordinator.StateChanged -= FocusModeCoordinator_StateChanged;
            _focusModeCoordinator.Dispose();
            _focusModeCoordinator = null;
        }

        if (_notificationDockWindow is not null)
        {
            _notificationDockWindow.ToggleMainWindowRequested -=
                NotificationDockWindow_ToggleMainWindowRequested;
        }

        if (_windowsDndGuidanceWindow is not null)
        {
            _windowsDndGuidanceWindow.CancelRequested -=
                WindowsDndGuidanceWindow_CancelRequested;
            _windowsDndGuidanceWindow.OpenSettingsRequested -=
                WindowsDndGuidanceWindow_OpenSettingsRequested;
        }

        _trayIconService?.Dispose();
        _trayIconService = null;
        _dashboardWindow?.Close();
        _dashboardWindow = null;

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

    internal void ShowDashboard()
    {
        RunOnUiThread(() =>
        {
            if (_dashboardWindow is null)
            {
                _dashboardWindow = new DashboardWindow();
                _dashboardWindow.Closed += DashboardWindow_Closed;
            }

            _dashboardWindow.Show();

            if (_dashboardWindow.WindowState == WindowState.Minimized)
            {
                _dashboardWindow.WindowState = WindowState.Normal;
            }

            _dashboardWindow.Activate();
        });
    }

    private void DashboardWindow_Closed(object? sender, EventArgs e)
    {
        if (_dashboardWindow is not null)
        {
            _dashboardWindow.Closed -= DashboardWindow_Closed;
        }

        _dashboardWindow = null;
    }

    private void RequestFocusModeChange()
    {
        RunOnUiThread(() => _focusModeCoordinator?.RequestToggle());
    }

    private void RequestShutdown()
    {
        RunOnUiThread(() =>
        {
            IsShuttingDown = true;

            _trayIconService?.Dispose();
            _trayIconService = null;

            _windowsDndGuidanceWindow?.CloseForShutdown();
            _notificationDockWindow?.Close();
            _dashboardWindow?.Close();
            _mainWindow?.Close();
            Shutdown();
        });
    }

    private void FocusModeService_StateChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (FocusModeService.IsEnabled)
            {
                _mainWindow?.Hide();
            }
            else
            {
                ShowDashboard();
            }

            UpdateFocusModeShell();
        });
    }

    private void ToggleMainWindowVisibility()
    {
        RunOnUiThread(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            if (_mainWindow.IsVisible)
            {
                _mainWindow.Hide();
                return;
            }

            ShowMainWindow();
        });
    }

    private void FocusModeCoordinator_StateChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(UpdateWindowsDndGuidance);
    }

    private void NotificationDockWindow_ToggleMainWindowRequested(
        object? sender,
        EventArgs e)
    {
        ToggleMainWindowVisibility();
    }

    private void WindowsDndGuidanceWindow_CancelRequested(object? sender, EventArgs e)
    {
        _focusModeCoordinator?.CancelGuidance();
    }

    private void WindowsDndGuidanceWindow_OpenSettingsRequested(
        object? sender,
        EventArgs e)
    {
        _focusModeCoordinator?.OpenSettingsAgain();
    }

    private void UpdateFocusModeShell()
    {
        if (_notificationDockWindow is null)
        {
            return;
        }

        if (FocusModeService.IsEnabled)
        {
            var wasVisible = _notificationDockWindow.IsVisible;
            _notificationDockWindow.PositionOnPrimaryWorkArea();

            if (!wasVisible)
            {
                _notificationDockWindow.Show();
                _notificationDockWindow.PlayActivationAnimation();
            }
        }
        else
        {
            _notificationDockWindow.Hide();
        }

        _trayIconService?.UpdateFocusModeState(FocusModeService.IsEnabled);
    }

    private void UpdateWindowsDndGuidance()
    {
        if (_windowsDndGuidanceWindow is null || _focusModeCoordinator is null)
        {
            return;
        }

        if (_focusModeCoordinator.Guidance is { } guidance)
        {
            _windowsDndGuidanceWindow.Present(guidance);
        }
        else
        {
            _windowsDndGuidanceWindow.Dismiss();
        }
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

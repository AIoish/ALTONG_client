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
    public FocusSettingsStore FocusSettings { get; } = new();
    public FocusRoutineService FocusRoutine { get; private set; } = null!;
    public SessionResultsService SessionResults { get; private set; } = null!;
    private readonly System.Windows.Threading.DispatcherTimer _routineTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    public IActiveWindowTracker ActiveWindowTracker { get; } = new ActiveWindowTracker();

    public IAltongDatabase Database { get; private set; } = null!;
    public INotificationRepository NotificationRepository { get; private set; } = null!;
    public IWindowSessionRepository WindowSessionRepository { get; private set; } = null!;
    public IFocusSessionRepository FocusSessionRepository { get; private set; } = null!;

    internal bool IsShuttingDown { get; private set; }

    private DashboardWindow? _dashboardWindow;
    private NotificationDockWindow? _notificationDockWindow;
    private WindowsDndGuidanceWindow? _windowsDndGuidanceWindow;
    private TrayIconService? _trayIconService;
    private FocusModeCoordinator? _focusModeCoordinator;
    private readonly List<Task> _windowWrites = new();

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
        SessionResults = new SessionResultsService(Database, FocusSessionRepository);

        // ActiveWindowTracker의 세션 종료([OUT]) 이벤트를 수신하여 window_sessions에 자동 적재
        ActiveWindowTracker.WindowSessionEnded += (_, e) =>
        {
            var record = new WindowSessionRecord(
                0, e.ProcessName, e.WindowTitle,
                e.StartedAt.UtcDateTime, e.EndedAt.UtcDateTime, e.DurationSeconds);
            var write = WindowSessionRepository.InsertAsync(record);
            lock (_windowWrites)
            {
                _windowWrites.RemoveAll(task => task.IsCompletedSuccessfully);
                _windowWrites.Add(write);
            }
            _ = write.ContinueWith(t => Console.WriteLine($"[Database] 세션 저장 실패: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        };

        _focusModeCoordinator = new FocusModeCoordinator(
            FocusModeService,
            new WindowsNotificationModeObserver(),
            new WindowsNotificationSettingsLauncher(),
            new WpfUiDispatcher(Dispatcher));

        FocusRoutine = new FocusRoutineService(FocusModeService, FocusSettings);
        _routineTimer.Tick += RoutineTimer_Tick;
        FocusRoutine.PhaseChanged += FocusRoutine_PhaseChanged;
        _routineTimer.Start();

        _notificationDockWindow = new NotificationDockWindow();
        _notificationDockWindow.ToggleDashboardRequested +=
            NotificationDockWindow_ToggleDashboardRequested;

        _windowsDndGuidanceWindow = new WindowsDndGuidanceWindow();
        _windowsDndGuidanceWindow.CancelRequested +=
            WindowsDndGuidanceWindow_CancelRequested;
        _windowsDndGuidanceWindow.OpenSettingsRequested +=
            WindowsDndGuidanceWindow_OpenSettingsRequested;

        _trayIconService = new TrayIconService(ShowDashboard);
        _trayIconService.SetDashboardAction(ToggleDashboard);

        FocusModeService.StateChanged += FocusModeService_StateChanged;
        _focusModeCoordinator.StateChanged += FocusModeCoordinator_StateChanged;
        _focusModeCoordinator.Start();
        ActiveWindowTracker.Start();
        UpdateFocusModeShell();
        UpdateWindowsDndGuidance();

        ShowDashboard();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FocusModeService.StateChanged -= FocusModeService_StateChanged;
        _routineTimer.Stop();
        _routineTimer.Tick -= RoutineTimer_Tick;
        if (FocusRoutine is not null)
            FocusRoutine.PhaseChanged -= FocusRoutine_PhaseChanged;
        ActiveWindowTracker.Dispose();

        if (_focusModeCoordinator is not null)
        {
            _focusModeCoordinator.StateChanged -= FocusModeCoordinator_StateChanged;
            _focusModeCoordinator.Dispose();
            _focusModeCoordinator = null;
        }

        if (_notificationDockWindow is not null)
        {
            _notificationDockWindow.ToggleDashboardRequested -=
                NotificationDockWindow_ToggleDashboardRequested;
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

    internal void ShowDashboard()
    {
        RunOnUiThread(() =>
        {
            if (_dashboardWindow is null)
            {
                _dashboardWindow = new DashboardWindow(
                    FocusSettings,
                    FocusRoutine,
                    SessionResults,
                    ActiveWindowTracker,
                    FocusModeService,
                    _focusModeCoordinator,
                    ShowCurrentRoutineReminder,
                    RequestShutdown);
                _dashboardWindow.Closed += DashboardWindow_Closed;
                MainWindow = _dashboardWindow;
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

    internal void RequestShutdown()
    {
        RunOnUiThread(() =>
        {
            IsShuttingDown = true;

            _trayIconService?.Dispose();
            _trayIconService = null;

            _windowsDndGuidanceWindow?.CloseForShutdown();
            _notificationDockWindow?.Close();
            _dashboardWindow?.Close();
            Shutdown();
        });
    }

    private void FocusModeService_StateChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            FocusRoutine.Refresh();
            if (FocusModeService.IsEnabled)
            {
                SessionResults.Begin(DateTime.UtcNow, FocusSettings.Current.FocusMinutes);
            }
            else
            {
                var lastContext = ActiveWindowTracker.CaptureNow();
                Task pendingWrites;
                lock (_windowWrites) pendingWrites = Task.WhenAll(_windowWrites);
                SessionResults.End(DateTime.UtcNow, lastContext, pendingWrites);
                _ = SessionResults.RefreshAsync();
                ShowDashboard();
            }

            UpdateFocusModeShell();
            UpdateRoutineView();
        });
    }

    private void ToggleDashboard()
    {
        RunOnUiThread(() =>
        {
            if (_dashboardWindow?.IsVisible == true)
            {
                _dashboardWindow.Hide();
                return;
            }

            ShowDashboard();
        });
    }

    private void RoutineTimer_Tick(object? sender, EventArgs e)
    {
        FocusRoutine.Refresh();
        UpdateRoutineView();
        _ = SessionResults.RefreshAsync();
    }

    private void UpdateRoutineView()
    {
        if (FocusRoutine.Phase != FocusRoutinePhase.Idle)
            _trayIconService?.UpdateRoutineStatus(FocusRoutine.StatusText);
    }

    private void FocusRoutine_PhaseChanged(object? sender, EventArgs e)
    {
        switch (FocusRoutine.Phase)
        {
            case FocusRoutinePhase.Focus:
                _notificationDockWindow?.ShowRoutineReminder(
                    "집중 시간이 시작됐어요.",
                    () => FocusRoutine.StatusText,
                    "집중에 필요한 작업을 시작해보세요.");
                break;

            case FocusRoutinePhase.Break:
                _notificationDockWindow?.ShowRoutineReminder(
                    "휴식 시간이 시작됐어요.",
                    () => FocusRoutine.StatusText,
                    "잠시 쉬어가세요. 방해 금지 모드는 직접 조절할 수 있어요.");
                break;

            case FocusRoutinePhase.Idle:
                _notificationDockWindow?.ShowRoutineReminder(
                    "집중 모드가 종료됐어요.",
                    () => "세션 종료",
                    "세션 통계를 정리하고 있어요. 대시보드에서 결과를 확인할 수 있어요.");
                break;
        }
    }

    internal void ShowCurrentRoutineReminder()
    {
        RunOnUiThread(() =>
        {
            if (_notificationDockWindow is null || FocusRoutine.Phase == FocusRoutinePhase.Idle)
                return;

            bool isBreak = FocusRoutine.Phase == FocusRoutinePhase.Break;
            _notificationDockWindow.ShowRoutineReminder(
                isBreak ? "휴식 시간이 진행 중이에요." : "집중 시간이 진행 중이에요.",
                () => FocusRoutine.StatusText,
                isBreak ? "충분히 쉬고 다음 집중을 준비해보세요." : "현재 작업에 집중해보세요.");
        });
    }

    private void FocusModeCoordinator_StateChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(UpdateWindowsDndGuidance);
    }

    private void NotificationDockWindow_ToggleDashboardRequested(
        object? sender,
        EventArgs e)
    {
        ToggleDashboard();
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
            _notificationDockWindow.KeepVisible();
            if (!wasVisible)
                _notificationDockWindow.PlayActivationAnimation();
        }
        else
        {
            _notificationDockWindow.HideAfterCurrentReminder();
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

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;

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
    public NotificationPipelineCoordinator NotificationPipeline { get; private set; } = null!;
    public KakaoNotificationInterceptor KakaoInterceptor { get; private set; } = null!;

    internal bool IsShuttingDown { get; private set; }

    private MainWindow? _mainWindow;
    private DashboardWindow? _dashboardWindow;
    private NotificationDockWindow? _notificationDockWindow;
    private WindowsDndGuidanceWindow? _windowsDndGuidanceWindow;
    private BreakReminderWindow? _breakReminderWindow;
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
            AppLogger.Info("[Altong] 터미널 콘솔 로그 연결 완료 (ActiveWindow 실시간 추적 시작)");
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
                e.StartedAt.UtcDateTime, e.EndedAt.UtcDateTime, e.DurationSeconds,
                SessionResults?.CurrentSessionId);
            var write = WindowSessionRepository.InsertAsync(record);
            lock (_windowWrites)
            {
                _windowWrites.RemoveAll(task => task.IsCompletedSuccessfully);
                _windowWrites.Add(write);
            }
            _ = write.ContinueWith(t => AppLogger.Error($"[Database] 세션 저장 실패: {t.Exception?.GetBaseException().Message}", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        };

        // 실시간 알림 수신 및 AI 필터링 파이프라인 가동 (OS 표준 토스트 + 카카오톡 전용 인터셉터 복합 구성)
        var winRtListener = new WinRtNotificationListener();
        KakaoInterceptor = new KakaoNotificationInterceptor(() => FocusModeService.IsEnabled);
        var compositeListener = new CompositeNotificationListener(winRtListener, KakaoInterceptor);

        var filterEngine = new RuleBasedFilterEngine();
        NotificationPipeline = new NotificationPipelineCoordinator(
            compositeListener,
            ActiveWindowTracker,
            NotificationRepository,
            filterEngine,
            () => FocusModeService.IsEnabled,
            () => SessionResults.CurrentSessionId);

        // 카카오톡 알림 평가 후 스텔스 복원 또는 완전 소멸 후속 제어 연동
        NotificationPipeline.NotificationProcessed += (_, record) =>
        {
            KakaoInterceptor.OnNotificationProcessed(record);
        };

        _ = NotificationPipeline.StartAsync();

        _focusModeCoordinator = new FocusModeCoordinator(
            FocusModeService,
            new WindowsNotificationModeObserver(),
            new WindowsNotificationSettingsLauncher(),
            new WpfUiDispatcher(Dispatcher));

        FocusRoutine = new FocusRoutineService(FocusModeService, FocusSettings);
        _routineTimer.Tick += RoutineTimer_Tick;
        FocusRoutine.PhaseChanged += FocusRoutine_PhaseChanged;
        _routineTimer.Start();

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

        _breakReminderWindow = new BreakReminderWindow();

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
        _routineTimer.Stop();
        _routineTimer.Tick -= RoutineTimer_Tick;
        if (FocusRoutine is not null)
            FocusRoutine.PhaseChanged -= FocusRoutine_PhaseChanged;
        ActiveWindowTracker.Dispose();
        NotificationPipeline?.Dispose();

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

        _breakReminderWindow?.CloseForShutdown();
        _breakReminderWindow = null;

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
                _dashboardWindow = new DashboardWindow(FocusSettings, FocusRoutine, SessionResults, ActiveWindowTracker);
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
            _breakReminderWindow?.CloseForShutdown();
            _breakReminderWindow = null;
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
            FocusRoutine.Refresh();
            if (FocusModeService.IsEnabled)
            {
                SessionResults.Begin(DateTime.UtcNow, FocusSettings.Current.FocusMinutes);
                _mainWindow?.Hide();
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
        _mainWindow?.UpdateRoutineStatus(FocusRoutine.StatusText);
    }

    private void FocusRoutine_PhaseChanged(object? sender, EventArgs e)
    {
        if (FocusRoutine.Phase == FocusRoutinePhase.Idle) return;
        if (FocusRoutine.Phase == FocusRoutinePhase.Break)
            _breakReminderWindow?.Present(FocusRoutine.StatusText);
        else
            _trayIconService?.ShowRoutineReminder("집중할 시간이에요.", FocusRoutine.StatusText);
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

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
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
    public IKakaoAudioOperator KakaoAudioOperator { get; private set; } = null!;

    internal bool IsShuttingDown { get; private set; }

    private DashboardWindow? _dashboardWindow;
    private NotificationDockWindow? _notificationDockWindow;
    private WindowsDndGuidanceWindow? _windowsDndGuidanceWindow;
    private TrayIconService? _trayIconService;
    private FocusModeCoordinator? _focusModeCoordinator;
    private readonly List<Task> _windowWrites = new();
    private DateTime _lastWindowCheckpointAt = DateTime.MinValue;
    private bool _windowCheckpointInProgress;
    private bool _routineTickInProgress;
    private SingleInstanceService? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            bool activated = _singleInstance.ActivateExistingAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter().GetResult();
            if (!activated)
                System.Windows.MessageBox.Show("알통이 이미 실행 중입니다. 트레이 아이콘에서 기존 창을 열어주세요.",
                    "ALTONG", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _singleInstance.StartListening(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsShuttingDown) ShowDashboard();
        })));

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

        // 활성 창 기록은 현재 집중 세션에만 연결한다.
        ActiveWindowTracker.WindowSessionEnded += (_, e) =>
        {
            if (!FocusModeService.IsEnabled || SessionResults.ActiveSession is not { } focus)
                return;
            // Tracker events can be raised while holding its state lock; do not perform SQLite I/O there.
            var write = Task.Run(() => PersistFocusWindowIntervalAsync(focus, e.ProcessName, e.WindowTitle,
                e.StartedAt.UtcDateTime, e.EndedAt.UtcDateTime));
            lock (_windowWrites)
            {
                _windowWrites.RemoveAll(task => task.IsCompletedSuccessfully);
                _windowWrites.Add(write);
            }
            _ = write.ContinueWith(t => AppLogger.Error($"[Database] 세션 저장 실패: {t.Exception?.GetBaseException().Message}", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        };

        // 실시간 알림 수신 및 AI 필터링 파이프라인 가동 (OS 표준 토스트 + 카카오톡 전용 인터셉터 복합 구성)
        KakaoAudioOperator = new LiveKakaoAudioOperator();
        if (FocusModeService.IsEnabled)
        {
            KakaoAudioOperator.Mute();
        }

        var winRtListener = new WinRtNotificationListener();
        KakaoInterceptor = new KakaoNotificationInterceptor(
            () => FocusModeService.IsEnabled,
            audioOperator: KakaoAudioOperator);
        var compositeListener = new CompositeNotificationListener(winRtListener, KakaoInterceptor);

        var filterEngine = new RuleBasedFilterEngine();
        NotificationPipeline = new NotificationPipelineCoordinator(
            compositeListener,
            ActiveWindowTracker,
            NotificationRepository,
            filterEngine,
            () => FocusModeService.IsEnabled,
            () => SessionResults.CurrentSessionId);

        NotificationPipeline.NotificationProcessed += NotificationPipeline_NotificationProcessed;

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

        _notificationDockWindow = new NotificationDockWindow();
        _notificationDockWindow.RoutineStatusProvider = () => new DockRoutineStatus(FocusRoutine.Phase);
        _notificationDockWindow.ShowDashboardRequested +=
            NotificationDockWindow_ShowDashboardRequested;

        _windowsDndGuidanceWindow = new WindowsDndGuidanceWindow();
        _windowsDndGuidanceWindow.CancelRequested +=
            WindowsDndGuidanceWindow_CancelRequested;
        _windowsDndGuidanceWindow.OpenSettingsRequested +=
            WindowsDndGuidanceWindow_OpenSettingsRequested;

        _trayIconService = new TrayIconService(ShowDashboard);
        _trayIconService.SetDashboardAction(ToggleDashboard);
        _trayIconService.SetDashboardCloseAction(HideDashboard);

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
        if (_singleInstance is { IsPrimary: false })
        {
            _singleInstance.Dispose();
            base.OnExit(e);
            return;
        }
        IsShuttingDown = true;
        FocusModeService.StateChanged -= FocusModeService_StateChanged;
        _routineTimer.Stop();
        _routineTimer.Tick -= RoutineTimer_Tick;
        if (FocusRoutine is not null)
            FocusRoutine.PhaseChanged -= FocusRoutine_PhaseChanged;
        PersistFocusStateForExit();
        ActiveWindowTracker.Dispose();
        if (NotificationPipeline is not null)
        {
            NotificationPipeline.NotificationProcessed -= NotificationPipeline_NotificationProcessed;
            NotificationPipeline.Dispose();
        }
        KakaoAudioOperator?.Dispose();

        if (_focusModeCoordinator is not null)
        {
            _focusModeCoordinator.StateChanged -= FocusModeCoordinator_StateChanged;
            _focusModeCoordinator.Dispose();
            _focusModeCoordinator = null;
        }

        if (_notificationDockWindow is not null)
        {
            _notificationDockWindow.ShowDashboardRequested -=
                NotificationDockWindow_ShowDashboardRequested;
            _notificationDockWindow.Close();
            _notificationDockWindow = null;
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

        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private void PersistFocusStateForExit()
    {
        if (SessionResults is null) return;
        DateTime endedAt = DateTime.UtcNow;
        CurrentContext? context = null;
        if (SessionResults.ActiveSession is { } focus)
        {
            context = ActiveWindowTracker.CaptureNow();
            try
            {
                PersistCurrentFocusWindowSnapshotAsync(focus, endedAt, context).GetAwaiter().GetResult();
                Task pendingWrites;
                lock (_windowWrites) pendingWrites = Task.WhenAll(_windowWrites);
                pendingWrites.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Database] 앱 사용 기록 저장 실패: {ex.GetBaseException().Message}");
            }
        }
        try
        {
            if (context is not null) SessionResults.End(endedAt, context, collectResult: false);
            // OFF may already have cleared ActiveSession while its background writes are still pending.
            SessionResults.PendingSave.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Database] 집중 상태 저장 실패: {ex.GetBaseException().Message}");
        }
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

    private Task PersistCurrentFocusWindowSnapshotAsync(
        FocusSessionRecord focus, DateTime endedAt, CurrentContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ActiveProcess) || context.DurationSeconds <= 0)
            return Task.CompletedTask;
        DateTime startedAt = context.LastUpdated.AddSeconds(-context.DurationSeconds);
        if (startedAt < _lastWindowCheckpointAt) startedAt = _lastWindowCheckpointAt;
        return PersistFocusWindowIntervalAsync(focus, context.ActiveProcess, context.WindowTitle,
            startedAt, endedAt);
    }

    private Task PersistFocusWindowIntervalAsync(FocusSessionRecord focus, string process,
        string title, DateTime startedAt, DateTime endedAt)
    {
        var start = startedAt > focus.StartedAt ? startedAt : focus.StartedAt;
        var end = focus.EndedAt is { } focusEnd && focusEnd < endedAt ? focusEnd : endedAt;
        if (end <= start) return Task.CompletedTask;
        return WindowSessionRepository.InsertAsync(new WindowSessionRecord(0, process, title, start, end,
            Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds)), focus.SessionId));
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
        var changedAt = DateTime.UtcNow;
        var enabled = FocusModeService.IsEnabled;
        RunOnUiThread(() =>
        {
            if (!enabled) _notificationDockWindow?.EndNotificationSession();
            FocusRoutine.Refresh();
            if (enabled)
            {
                KakaoAudioOperator?.Mute();
                lock (_windowWrites) _windowWrites.RemoveAll(task => task.IsCompleted);
                _lastWindowCheckpointAt = DateTime.MinValue;
                SessionResults.Begin(changedAt, FocusSettings.Current.TimerEnabled ? FocusSettings.Current.FocusMinutes : 0);
                if (SessionResults.CurrentSessionId is { } sessionId)
                    _notificationDockWindow?.BeginNotificationSession(sessionId);
            }
            else
            {
                KakaoAudioOperator?.Unmute();
                var lastContext = ActiveWindowTracker.CaptureNow();
                if (SessionResults.ActiveSession is { } focus)
                {
                    var snapshotWrite = PersistCurrentFocusWindowSnapshotAsync(focus, changedAt, lastContext);
                    Task pendingWrites;
                    lock (_windowWrites) pendingWrites = Task.WhenAll(_windowWrites.Append(snapshotWrite));
                    _ = CompleteAndShowFocusReportAsync(changedAt, pendingWrites,
                        NotificationPipeline.DrainSessionAsync(focus.SessionId));
                }
            }

            UpdateFocusModeShell();
            UpdateRoutineView();
        });
    }

    private async Task CompleteAndShowFocusReportAsync(DateTime endedAt, Task pendingWrites, Task notificationWrites)
    {
        try
        {
            var result = await SessionResults.CompleteFocusSessionAsync(endedAt, pendingWrites, notificationWrites);
            if (result is null || IsShuttingDown) return;
            await Dispatcher.InvokeAsync(() => { });
            if (IsShuttingDown) return;
            var report = new SessionReportWindow(result, SessionResults.Reports);
            if (_dashboardWindow?.IsVisible == true)
                report.Owner = _dashboardWindow;
            else
                report.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            report.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[SessionResults] 집중 결과 표시 실패: {ex.GetBaseException().Message}", ex);
        }
    }

    private void HideDashboard() => RunOnUiThread(() => _dashboardWindow?.Hide());

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

    private async void RoutineTimer_Tick(object? sender, EventArgs e)
    {
        if (_routineTickInProgress || IsShuttingDown) return;
        _routineTickInProgress = true;
        try
        {
            FocusRoutine.Refresh();
            UpdateRoutineView();
            await SessionResults.RefreshAsync();
            if (!IsShuttingDown) await CheckpointActiveWindowAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Database] 집중 상태 갱신 실패: {ex.GetBaseException().Message}");
        }
        finally { _routineTickInProgress = false; }
    }

    private async Task CheckpointActiveWindowAsync()
    {
        if (_windowCheckpointInProgress || !FocusModeService.IsEnabled ||
            SessionResults.ActiveSession is not { } focus)
            return;
        var now = DateTime.UtcNow;
        if (_lastWindowCheckpointAt != DateTime.MinValue &&
            now - _lastWindowCheckpointAt < TimeSpan.FromSeconds(5))
            return;
        _windowCheckpointInProgress = true;
        try
        {
            var context = ActiveWindowTracker.CaptureNow();
            var write = PersistCurrentFocusWindowSnapshotAsync(focus, now, context);
            lock (_windowWrites)
            {
                _windowWrites.RemoveAll(task => task.IsCompletedSuccessfully);
                _windowWrites.Add(write);
            }
            await write;
            _lastWindowCheckpointAt = now;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Database] 활성 창 확인 저장 실패: {ex.GetBaseException().Message}");
        }
        finally { _windowCheckpointInProgress = false; }
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
                    FocusRoutine.UsesTimer ? "집중 시간이 시작됐어요." : "집중 모드가 켜졌어요.",
                    () => FocusRoutine.StatusText,
                    FocusRoutine.UsesTimer ? "집중에 필요한 작업을 시작해보세요." : "시간 제한 없이 집중하세요. 종료할 때 집중 모드를 꺼 주세요.");
                break;

            case FocusRoutinePhase.Break:
                _notificationDockWindow?.ShowRoutineReminder(
                    "휴식 시간이 시작됐어요.",
                    () => FocusRoutine.StatusText,
                    "잠시 쉬어가세요. 방해 금지 모드는 직접 조절할 수 있어요.");
                break;

            case FocusRoutinePhase.Completed:
                _notificationDockWindow?.ShowRoutineReminder(
                    "설정한 집중 시간이 끝났어요.",
                    () => FocusRoutine.StatusText,
                    "집중 모드는 계속 켜져 있어요. 마치려면 직접 꺼 주세요.");
                break;

            case FocusRoutinePhase.Idle:
                _notificationDockWindow?.ShowRoutineReminder(
                    "집중 모드가 종료됐어요.",
                    () => "세션 종료",
                    "집중 결과 리포트를 준비하고 있어요.");
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
                isBreak ? "휴식 시간이 진행 중이에요."
                    : FocusRoutine.Phase == FocusRoutinePhase.Completed ? "설정한 집중 시간이 끝났어요."
                    : FocusRoutine.UsesTimer ? "집중 시간이 진행 중이에요." : "집중 모드가 켜져 있어요.",
                () => FocusRoutine.StatusText,
                isBreak ? "충분히 쉬고 다음 집중을 준비해보세요." : "집중 모드를 끌 때까지 현재 상태가 유지됩니다.");
        });
    }

    private void FocusModeCoordinator_StateChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(UpdateWindowsDndGuidance);
    }

    private void NotificationDockWindow_ShowDashboardRequested(
        object? sender,
        EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (_dashboardWindow is { IsActive: true } dashboard)
                dashboard.WindowState = WindowState.Minimized;
            else
                ShowDashboard();
        });
    }

    private void NotificationPipeline_NotificationProcessed(object? sender, NotificationRecord record)
    {
        // 수집 당시 숨긴 카톡 팝업 정리는 UI 표시와 독립적으로 수행한다.
        KakaoInterceptor.OnNotificationProcessed(record);
        if (IsShuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            // 큐에서 기다리는 동안 집중 세션이 끝나거나 교체되었을 수도 있다.
            if (IsShuttingDown || !FocusModeService.IsEnabled ||
                record.SessionId != SessionResults.CurrentSessionId) return;
            _notificationDockWindow?.ReceiveNotification(record);
        }));
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

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.AI.Briefing;
using Altong.Client.Services.Focus;
using Altong.Client.Services.Win32;
using Altong.Client.Services.Notifications;
using Altong.Client.Services.NotificationControl;

namespace Altong.Client;

public partial class App : System.Windows.Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public FocusModeService FocusModeService { get; } = new();
    public FocusSettingsStore FocusSettings { get; } = new();
    public AutoDndSettingsStore AutoDndSettings { get; } = new();
    public StartupRegistration StartupRegistration { get; } = new();
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
    private AutoDndController? _autoDnd;
    private AutoDndSuggestionWindow? _autoDndSuggestionWindow;
    private readonly System.Windows.Threading.DispatcherTimer _autoDndTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    /// <summary>타이머 종료 제안 카드를 자동으로 닫는 타이머 (무응답이면 집중 유지).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _timerPromptTimer = new();
    private AutoDndPromptKind _activePrompt;
    private bool _isShowingDashboard;
    private readonly List<Task> _windowWrites = new();
    private DateTime _lastWindowCheckpointAt = DateTime.MinValue;
    private bool _windowCheckpointInProgress;
    private bool _routineTickInProgress;
    private SingleInstanceService? _singleInstance;
    private KlueFilterEngine? _klueFilterEngine;
    private LlamaReportSummaryProvider? _reportSummaryProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() == "--notification-guardian")
        {
            int result = NotificationRecoveryGuardian.Run(e.Args);
            Shutdown(result);
            return;
        }
        base.OnStartup(e);
        // Windows 시작 프로그램으로 실행되면 대시보드 없이 트레이에만 뜬다.
        bool launchedAtStartup = e.Args.Contains(StartupRegistration.StartupArgument);

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            // 시작 프로그램으로 한 번 더 실행된 경우 이미 떠 있는 알통을 앞으로 꺼내지 않고 조용히 끝낸다.
            if (launchedAtStartup)
            {
                Shutdown();
                return;
            }
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

        // Recover before database/model loading: a startup failure must not strand suppressed notifications.
        var nativeFocusController = new NativeFocusController();

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

        var modelDir = Path.Combine(AppContext.BaseDirectory,
            "models", "filtering", "klue-base-safety-v1");

        _klueFilterEngine = new KlueFilterEngine(
            Path.Combine(modelDir, "model-fp32.onnx"),
            Path.Combine(modelDir, "tokenizer.json"),
            shadow: false,
            observe: d => AppLogger.Info(
                $"[KLUE] {d.Status}: U={d.UrgencyScore}, R={d.RelevanceScore}, modelPass={d.ModelPassed}"));

        var briefingModelPath = Path.Combine(AppContext.BaseDirectory,
            "models", "briefing", "qwen_briefing_v8_q8_0.gguf");
        _reportSummaryProvider = new LlamaReportSummaryProvider(
            NotificationRepository,
            briefingModelPath);

        IFilterEngine filterEngine = _klueFilterEngine;
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
            new WpfUiDispatcher(Dispatcher),
            nativeFocusController);

        FocusRoutine = new FocusRoutineService(FocusModeService, FocusSettings);
        _routineTimer.Tick += RoutineTimer_Tick;
        FocusRoutine.PhaseChanged += FocusRoutine_PhaseChanged;
        _routineTimer.Start();

        _notificationDockWindow = new NotificationDockWindow();
        // 상단 노치는 화면 맨 위 띠를 예약해 다른 창이 노치를 가리지 않게 한다.
        _notificationDockWindow.ReserveTopBand = true;
        _notificationDockWindow.RoutineStatusProvider = () => new DockRoutineStatus(FocusRoutine.Phase);
        _notificationDockWindow.ShowDashboardRequested +=
            NotificationDockWindow_ShowDashboardRequested;

        _windowsDndGuidanceWindow = new WindowsDndGuidanceWindow();
        _windowsDndGuidanceWindow.CancelRequested +=
            WindowsDndGuidanceWindow_CancelRequested;
        _windowsDndGuidanceWindow.OpenSettingsRequested +=
            WindowsDndGuidanceWindow_OpenSettingsRequested;

        // 트레이 클릭은 WinForms 메시지 처리 도중에 들어오므로, 창 생성은 그 처리가 끝난 뒤로 미룬다.
        // 더블클릭의 두 번째 이벤트가 첫 창 생성 도중에 끼어들지 않고 차례대로 실행된다.
        _trayIconService = new TrayIconService(() => PostToUiThread(ShowDashboard));
        _trayIconService.SetDashboardAction(() => PostToUiThread(ToggleDashboard));
        _trayIconService.SetDashboardCloseAction(() => PostToUiThread(HideDashboard));

        FocusModeService.StateChanged += FocusModeService_StateChanged;
        _focusModeCoordinator.StateChanged += FocusModeCoordinator_StateChanged;
        _focusModeCoordinator.Start();
        ActiveWindowTracker.Start();
        StartAutoDnd();
        UpdateFocusModeShell();
        UpdateWindowsDndGuidance();

        // 폴더를 옮겼거나 다른 빌드로 실행했으면 시작 프로그램 등록 경로를 지금 실행 중인 알통으로 고친다.
        try
        {
            if (StartupRegistration.RepairPathIfRegistered())
                AppLogger.Info("[Startup] 시작 프로그램 등록 경로를 현재 실행 파일로 고쳤습니다.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLogger.Warn($"[Startup] 시작 프로그램 등록 확인 실패: {ex.Message}");
        }

        if (launchedAtStartup)
            AppLogger.Info("[Startup] Windows 시작으로 실행되어 트레이에서 대기합니다.");
        else
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
        StopAutoDnd();
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
        _klueFilterEngine?.Dispose();
        _reportSummaryProvider?.Dispose();
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
            // 창을 만드는 도중(CreateWindowEx 안)에 들어온 두 번째 열기 요청이 Show()를 다시 부르면
            // WPF가 HWND를 하나 더 만들고 "VisualTarget의 루트 Visual은 부모를 가질 수 없습니다"로 실패한다.
            // 이미 여는 중이면 그 창이 곧 뜨므로 무시한다.
            if (_isShowingDashboard) return;
            _isShowingDashboard = true;
            try
            {
                ShowDashboardCore();
            }
            finally
            {
                _isShowingDashboard = false;
            }
        });
    }

    private void ShowDashboardCore()
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
                RequestShutdown,
                summaryProvider: null,
                autoDndSettings: AutoDndSettings,
                startupRegistration: StartupRegistration);
            _dashboardWindow.Closed += DashboardWindow_Closed;
            MainWindow = _dashboardWindow;
        }

        _dashboardWindow.Show();

        if (_dashboardWindow.WindowState == WindowState.Minimized)
        {
            _dashboardWindow.WindowState = WindowState.Normal;
        }

        _dashboardWindow.Activate();
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
            StopAutoDnd();
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
            if (!enabled)
            {
                _notificationDockWindow?.EndNotificationSession();
                // 다른 경로로 집중이 끝났으면 타이머 종료 제안은 더 의미가 없다.
                CloseTimerPrompt(immediately: true);
            }
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
            var report = new SessionReportWindow(result, SessionResults.Reports, isSaved: false, summaryProvider: _reportSummaryProvider);
            // 대시보드가 지금 활성 창일 때만 딸린 창으로 연다. 뒤에 깔려 있거나 최소화된 대시보드에 딸리면
            // 리포트도 함께 뒤에 뜨거나 숨는다(자리비움 자동 종료·타이머 카드로 끝난 경우).
            if (_dashboardWindow is { IsVisible: true, IsActive: true } dashboard
                && dashboard.WindowState != WindowState.Minimized)
                report.Owner = dashboard;
            else
                report.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            report.Show();
            // 자리비움 자동 종료나 포커스를 가져가지 않는 타이머 카드로 끝나면 알통이 활성 앱이 아니어서
            // Windows가 새 창을 작업 중인 창 뒤에 둔다. 이때 WPF는 창을 "활성"(IsActive)으로 보고하기도 하므로
            // Windows의 실제 전경 창으로 판단한다. 포커스는 빼앗지 않고 맨 위 고정으로 보이게 한 뒤,
            // 사용자가 리포트를 누르면 일반 창으로 돌린다. 전체화면 영상·발표 위로는 덮지 않는다.
            if (GetForegroundWindow() != new System.Windows.Interop.WindowInteropHelper(report).Handle
                && !new Win32FullscreenDetector().IsFullscreenActive())
            {
                report.Topmost = true;
                void ReleaseTopmost(object? sender, System.Windows.Input.MouseButtonEventArgs e)
                {
                    report.PreviewMouseDown -= ReleaseTopmost;
                    report.Topmost = false;
                }
                report.PreviewMouseDown += ReleaseTopmost;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[SessionResults] 집중 결과 표시 실패: {ex.GetBaseException().Message}", ex);
        }
    }

    // ───────────────────────────── AutoDND (작업 맥락 기반 집중 진입·이탈) ─────────────────────────────

    private enum AutoDndPromptKind { None, Entry, TimerFinished }

    private bool IsAutoDndAvailable =>
        AutoDndSettings.Current.Enabled
        && _focusModeCoordinator is { CanStartDirectly: true, HasControllableApps: true };

    /// <summary>
    /// 진입: 작업 앱을 일정 시간 쓰면 상단에 집중 제안 카드를 띄운다(스스로 켜지는 않음).
    /// 이탈: 집중 중 15분 동안 자리를 비우면 집중을 끝내고, 설정한 집중 시간이 끝나면 종료를 제안한다.
    /// 설정에서 켜져 있고 알통 자체 제어 방식일 때만 동작한다.
    /// </summary>
    private void StartAutoDnd()
    {
        AutoDndOptions? options = null;
#if DEBUG
        // 직접 테스트·시연용: 15분 자리비움 같은 기준을 기다리지 않도록 시간을 줄인다. 릴리스 빌드에는 없다.
        if (Environment.GetEnvironmentVariable("ALTONG_AUTODND_FAST") == "1")
        {
            options = AutoDndOptions.Fast;
            AppLogger.Warn("[AutoDND] 빠른 테스트 모드: 작업 20초면 제안, 자리비움 1분이면 종료, [나중에] 1분");
        }
#endif
        _autoDnd = new AutoDndController(
            ActiveWindowTracker,
            new Win32UserIdleDetector(),
            new Win32FullscreenDetector(),
            () => FocusModeService.IsEnabled,
            () => IsAutoDndAvailable,
            new AutoDndStateMachine(options: options),
            isOnBreak: () => FocusRoutine.Phase == FocusRoutinePhase.Break);
        _autoDnd.SuggestionRequested += AutoDnd_SuggestionRequested;
        _autoDnd.SuggestionClosed += AutoDnd_SuggestionClosed;
        _autoDnd.AwayExitRequested += AutoDnd_AwayExitRequested;

        _autoDndSuggestionWindow = new AutoDndSuggestionWindow();
        _autoDndSuggestionWindow.Accepted += AutoDndSuggestion_Accepted;
        _autoDndSuggestionWindow.Declined += AutoDndSuggestion_Declined;

        _timerPromptTimer.Interval = _autoDnd.Machine.Options.SuggestionTimeout;
        _timerPromptTimer.Tick += TimerPromptTimer_Tick;
        _autoDndTimer.Tick += AutoDndTimer_Tick;
        _autoDndTimer.Start();
    }

    private void StopAutoDnd()
    {
        _autoDndTimer.Stop();
        _autoDndTimer.Tick -= AutoDndTimer_Tick;
        _timerPromptTimer.Stop();
        _timerPromptTimer.Tick -= TimerPromptTimer_Tick;
        _activePrompt = AutoDndPromptKind.None;
        if (_autoDnd is not null)
        {
            _autoDnd.SuggestionRequested -= AutoDnd_SuggestionRequested;
            _autoDnd.SuggestionClosed -= AutoDnd_SuggestionClosed;
            _autoDnd.AwayExitRequested -= AutoDnd_AwayExitRequested;
            _autoDnd = null;
        }
        if (_autoDndSuggestionWindow is not null)
        {
            _autoDndSuggestionWindow.Accepted -= AutoDndSuggestion_Accepted;
            _autoDndSuggestionWindow.Declined -= AutoDndSuggestion_Declined;
            _autoDndSuggestionWindow.CloseForShutdown();
            _autoDndSuggestionWindow = null;
        }
    }

    private void AutoDndTimer_Tick(object? sender, EventArgs e)
    {
        if (IsShuttingDown) return;
        try { _autoDnd?.Tick(); }
        catch (Exception ex) { AppLogger.Error($"[AutoDND] 상태 갱신 실패: {ex.GetBaseException().Message}", ex); }
    }

    private void AutoDnd_SuggestionRequested(object? sender, EventArgs e)
    {
        if (IsShuttingDown || _autoDnd is null) return;
        _activePrompt = AutoDndPromptKind.Entry;
        _autoDndSuggestionWindow?.Present(_autoDnd.Machine.Options.SuggestionTimeout, AutoDndPrompt.Entry);
    }

    private void AutoDnd_SuggestionClosed(object? sender, EventArgs e)
    {
        if (_activePrompt != AutoDndPromptKind.Entry) return;
        _activePrompt = AutoDndPromptKind.None;
        _autoDndSuggestionWindow?.Dismiss();
    }

    /// <summary>이탈: 자리를 오래 비웠으니 집중을 끝낸다. 리포트는 돌아왔을 때 화면에 남아 있다.</summary>
    private void AutoDnd_AwayExitRequested(object? sender, EventArgs e)
    {
        if (IsShuttingDown) return;
        CloseTimerPrompt(immediately: true);
        if (_focusModeCoordinator?.RequestStop() != true)
            AppLogger.Info("[AutoDND] 자리비움으로 집중을 끝내려 했지만 끄지 못했습니다.");
    }

    /// <summary>이탈: 설정한 집중 시간이 끝나면 종료를 제안한다. 아무것도 누르지 않으면 집중을 이어 간다.</summary>
    private void ShowTimerFinishedPrompt()
    {
        if (_autoDndSuggestionWindow is null || _autoDnd is null) return;
        _activePrompt = AutoDndPromptKind.TimerFinished;
        _autoDndSuggestionWindow.Present(_timerPromptTimer.Interval, AutoDndPrompt.TimerFinished);
        _timerPromptTimer.Stop();
        _timerPromptTimer.Start();
    }

    private void TimerPromptTimer_Tick(object? sender, EventArgs e) => CloseTimerPrompt(immediately: false);

    private void CloseTimerPrompt(bool immediately)
    {
        _timerPromptTimer.Stop();
        if (_activePrompt != AutoDndPromptKind.TimerFinished) return;
        _activePrompt = AutoDndPromptKind.None;
        if (immediately) _autoDndSuggestionWindow?.HideImmediately();
        else _autoDndSuggestionWindow?.Dismiss();
    }

    private void AutoDndSuggestion_Accepted(object? sender, EventArgs e)
    {
        // 알통 자체 제어를 켜고 끄는 동안 UI 스레드가 몇 초 막히므로, 카드를 먼저 즉시 숨겨 멈춘 채 남지 않게 한다.
        switch (_activePrompt)
        {
            case AutoDndPromptKind.Entry:
                _autoDndSuggestionWindow?.HideImmediately();
                if (_autoDnd?.Accept() != true) return;
                if (_focusModeCoordinator?.RequestStart() != true)
                    AppLogger.Info("[AutoDND] 제안을 수락했지만 집중 모드를 켜지 못했습니다.");
                break;

            case AutoDndPromptKind.TimerFinished:
                CloseTimerPrompt(immediately: true);
                _focusModeCoordinator?.RequestStop();
                break;
        }
    }

    private void AutoDndSuggestion_Declined(object? sender, EventArgs e)
    {
        switch (_activePrompt)
        {
            case AutoDndPromptKind.Entry:
                _autoDnd?.Reject();
                break;
            case AutoDndPromptKind.TimerFinished:
                CloseTimerPrompt(immediately: false);
                break;
        }
    }

    private void HideDashboard() => RunOnUiThread(() => _dashboardWindow?.Hide());

    private void ToggleDashboard()
    {
        RunOnUiThread(() =>
        {
            if (_isShowingDashboard) return;
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
            _focusModeCoordinator?.CheckNativeHealth();
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
                // AutoDND가 켜져 있으면 안내 말풍선 대신 [집중 종료] [계속하기] 카드로 종료를 제안한다.
                if (IsAutoDndAvailable)
                {
                    ShowTimerFinishedPrompt();
                    break;
                }
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

    private void PostToUiThread(Action action) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsShuttingDown) action();
        }));

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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Altong.Client.Data.Models;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 카카오톡의 독자적인 Win32 알림 팝업을 포착하여 실시간 가로채기(스텔스 숨김) 및
/// 집중 모드에서 숨긴 원래 팝업을 닫는 전용 인터셉터. 통과한 내용은 미니바에서 표시한다.
/// </summary>
public sealed class KakaoNotificationInterceptor : IWindowsNotificationListener
{
    private const uint EventObjectShow = 0x8002;
    private const int ObjidWindow = 0;
    private const int ObjidClient = -4;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private const uint WmUser = 0x0400;
    private const uint PmNoRemove = 0x0000;

    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PidCacheDuration = TimeSpan.FromSeconds(3);

    private delegate void WinEventDelegate(
        nint hWinEventHook,
        uint eventType,
        nint hWnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage([In] ref Msg lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private readonly IKakaoWindowOperator _windowOperator;
    private readonly IKakaoAudioOperator? _audioOperator;
    private readonly Func<bool> _isFocusModeEnabled;
    private readonly ConcurrentDictionary<string, PendingWindowEntry> _pendingWindows = new();

    // 이미 포착한 알림 창. 창이 사라지거나 숨겨질 때까지 다시 포착하지 않는다(스캐너가 정리).
    private readonly ConcurrentDictionary<nint, byte> _capturedHwnds = new();

    private readonly object _syncRoot = new();
    private readonly object _pidLock = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Task<bool>? _startTask;
    private System.Threading.Timer? _scannerTimer;
    private WinEventDelegate? _winEventProc;
    private volatile bool _isRunning;
    private bool _isDisposed;
    private int _scanInProgress;
    private uint _kakaoPid;
    private DateTimeOffset _lastPidCheck = DateTimeOffset.MinValue;

    private record PendingWindowEntry(nint Hwnd, DateTimeOffset CreatedAt);

    private readonly record struct Capture(nint Hwnd, string Id, bool IsFocusMode);

    public event EventHandler<RawNotification>? NotificationReceived;

    public bool IsRunning => _isRunning;

    public KakaoNotificationInterceptor(
        Func<bool> isFocusModeEnabled,
        IKakaoWindowOperator? windowOperator = null,
        IKakaoAudioOperator? audioOperator = null)
    {
        _isFocusModeEnabled = isFocusModeEnabled ?? throw new ArgumentNullException(nameof(isFocusModeEnabled));
        _windowOperator = windowOperator ?? new LiveKakaoWindowOperator();
        _audioOperator = audioOperator;
    }

    public Task<bool> StartAsync()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_startTask is not null)
            {
                return _startTask;
            }

            // 훅 스레드에서 이어지는 await 연속 작업이 메시지 루프를 막지 않도록 비동기 완료
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startTask = tcs.Task;
            _isRunning = true;

            _hookThread = new Thread(() => RunHookLoop(tcs))
            {
                IsBackground = true,
                Name = "KakaoNotificationHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            // 50ms 백업 스캐너 (이벤트 훅이 놓친 팝업 포착 + 보류 창 타임아웃 정리)
            _scannerTimer = new System.Threading.Timer(OnScannerTick, null, 50, 50);

            return tcs.Task;
        }
    }

    public void Stop()
    {
        Thread? hookThread;
        System.Threading.Timer? scannerTimer;

        lock (_syncRoot)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _startTask = null;
            scannerTimer = _scannerTimer;
            _scannerTimer = null;
            hookThread = _hookThread;
            _hookThread = null;

            // 훅 스레드가 아직 ID를 등록하기 전이면, 스레드가 스스로 _isRunning=false를 보고 종료한다.
            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WmQuit, nint.Zero, nint.Zero);
            }
        }

        // 진행 중인 스캐너 콜백과 훅 스레드가 끝난 뒤에 보류 창을 정리해야 숨긴 창이 남지 않는다.
        scannerTimer?.Dispose();
        SpinWait.SpinUntil(() => Volatile.Read(ref _scanInProgress) == 0, 1000);
        hookThread?.Join(1000);

        CleanupPendingWindows();
        AppLogger.Info("[KakaoInterceptor] 카카오톡 가로채기 엔진 중지 완료.");
    }

    private void RunHookLoop(TaskCompletionSource<bool> tcs)
    {
        nint hook = nint.Zero;
        try
        {
            // PostThreadMessage(WM_QUIT)가 유실되지 않도록 메시지 큐를 먼저 생성
            PeekMessage(out _, nint.Zero, WmUser, WmUser, PmNoRemove);

            // 가비지 컬렉션 방지를 위해 필드에 delegate 유지
            _winEventProc = OnWinEvent;
            uint kakaoPid = EnsureKakaoPid(DateTimeOffset.UtcNow);

            // 시스템/오브젝트 전 이벤트(0x0001 ~ 0x7FFFFFFF) 훅 등록
            // 64비트 .NET 9 프로세스에서 32비트(WOW64) 카카오톡 창 이벤트를 누락 없이 0ms로 수신하려면
            // idProcess=0(글로벌 훅)으로 등록 후 OnWinEvent 콜백에서 PID를 초고속(1ns) 필터링해야 함
            hook = SetWinEventHook(
                0x0001,
                0x7FFFFFFF,
                nint.Zero,
                _winEventProc,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);

            if (hook == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                AppLogger.Error($"[KakaoInterceptor] SetWinEventHook 등록 실패: ErrorCode={err}");
                ResetAfterStartFailure();
                tcs.TrySetResult(false);
                return;
            }

            lock (_syncRoot)
            {
                if (!_isRunning)
                {
                    // 시작 도중 Stop이 호출됨
                    tcs.TrySetResult(false);
                    return;
                }

                _hookThreadId = GetCurrentThreadId();
            }

            AppLogger.Info($"[KakaoInterceptor] 카카오톡 전용 가로채기 훅 엔진 가동 시작. (TargetPID={kakaoPid})");
            tcs.TrySetResult(true);

            // Win32 메시지 루프 가동 (훅 이벤트 처리를 위해 필수). GetMessage는 오류 시 -1을 반환한다.
            while (GetMessage(out Msg msg, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[KakaoInterceptor] 훅 스레드 비정상 종료: {ex.Message}", ex);
            ResetAfterStartFailure();
            tcs.TrySetResult(false);
        }
        finally
        {
            // UnhookWinEvent는 훅을 등록한 스레드에서 호출해야 한다.
            if (hook != nint.Zero)
            {
                UnhookWinEvent(hook);
            }

            lock (_syncRoot)
            {
                _hookThreadId = 0;
            }
        }
    }

    private void ResetAfterStartFailure()
    {
        lock (_syncRoot)
        {
            if (_hookThread != Thread.CurrentThread)
            {
                return;
            }

            _isRunning = false;
            _startTask = null;
            _hookThread = null;
            _scannerTimer?.Dispose();
            _scannerTimer = null;
        }
    }

    private uint EnsureKakaoPid(DateTimeOffset now)
    {
        lock (_pidLock)
        {
            if (now - _lastPidCheck < PidCacheDuration)
            {
                return _kakaoPid;
            }

            _lastPidCheck = now;
            try
            {
                var procs = Process.GetProcessesByName("KakaoTalk");
                _kakaoPid = procs.Length > 0 ? (uint)procs[0].Id : 0;
                foreach (var proc in procs)
                {
                    proc.Dispose();
                }
            }
            catch
            {
                _kakaoPid = 0;
            }

            return _kakaoPid;
        }
    }

    private void OnScannerTick(object? state)
    {
        // 콜백 중첩 실행 방지 (플래그를 먼저 세운 뒤 _isRunning을 확인해야 Stop과 경합하지 않음)
        if (Interlocked.Exchange(ref _scanInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_isRunning)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // 5초 이상 판정이 오지 않은 보류 창 자동 소멸 (타임아웃 안전망)
            foreach (var kvp in _pendingWindows)
            {
                if (now - kvp.Value.CreatedAt > PendingTimeout &&
                    _pendingWindows.TryRemove(kvp.Key, out var expired))
                {
                    AppLogger.Warn($"[KakaoInterceptor] 알림 처리 타임아웃(5초 초과): 창을 안전하게 소멸합니다. (hWnd=0x{expired.Hwnd:X8})");
                    _windowOperator.CloseWindow(expired.Hwnd);
                }
            }

            // 파괴된 창은 포착 목록에서 제거
            foreach (var hwnd in _capturedHwnds.Keys)
            {
                if (!IsWindow(hwnd))
                {
                    _capturedHwnds.TryRemove(hwnd, out _);
                }
            }

            // 카카오톡 프로세스가 실행 중이지 않으면 전체 윈도우 스캔 생략
            if (EnsureKakaoPid(now) == 0)
            {
                return;
            }

            nint hWnd = nint.Zero;
            while ((hWnd = FindWindowEx(nint.Zero, hWnd, null, null)) != nint.Zero)
            {
                if (!_capturedHwnds.ContainsKey(hWnd) && _windowOperator.IsNotificationWindow(hWnd))
                {
                    OnWindowDetected(hWnd);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoInterceptor] 백업 스캐너 오류: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _scanInProgress, 0);
        }
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hWnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hWnd == nint.Zero || (idObject != ObjidWindow && idObject != ObjidClient))
        {
            return;
        }

        if (_capturedHwnds.ContainsKey(hWnd))
        {
            return;
        }

        // 카카오톡 프로세스의 창인지 확인 (카카오톡 미실행 시 즉시 무시)
        uint kakaoPid = EnsureKakaoPid(DateTimeOffset.UtcNow);
        if (kakaoPid == 0)
        {
            return;
        }

        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid != kakaoPid)
        {
            return;
        }

        OnWindowDetected(hWnd);
    }

    /// <summary>
    /// 훅/스캐너 경로: 숨김까지만 즉시 처리하고, 수백 ms 이상 걸리는 OCR은 워커 스레드로 넘긴다.
    /// </summary>
    private void OnWindowDetected(nint hWnd)
    {
        if (TryBeginCapture(hWnd, out var capture))
        {
            ThreadPool.QueueUserWorkItem(_ => CompleteCapture(capture));
        }
    }

    /// <summary>
    /// 카카오톡 윈도우 표시 이벤트를 동기적으로 처리합니다. (단위 테스트 및 모의 환경에서 직접 호출 가능)
    /// </summary>
    public void HandleWindowShowEvent(nint hWnd)
    {
        if (TryBeginCapture(hWnd, out var capture))
        {
            CompleteCapture(capture);
        }
    }

    private bool TryBeginCapture(nint hWnd, out Capture capture)
    {
        capture = default;
        try
        {
            // 1. 카카오톡 알림 팝업 창 여부 판정
            if (!_windowOperator.IsNotificationWindow(hWnd))
            {
                return false;
            }

            // 2. 원자적 중복 진입 차단: 훅과 스캐너가 동시에 같은 창을 잡아도 하나만 통과
            if (!_capturedHwnds.TryAdd(hWnd, 0))
            {
                return false;
            }

            bool isFocusMode = _isFocusModeEnabled();
            capture = new Capture(hWnd, $"kakao_{Guid.NewGuid():N}", isFocusMode);

            string stealthTag = isFocusMode ? "스텔스 은닉" : "일반 표시";
            AppLogger.Info($"[KakaoInterceptor] 카카오톡 알림 팝업 포착 & {stealthTag} (hWnd=0x{hWnd:X8})");

            // 3. 집중 모드: 보류 목록에 먼저 등록(판정/타임아웃/종료 시 반드시 닫힘)한 뒤 즉시 숨김 및 오디오 음소거
            if (isFocusMode)
            {
                _pendingWindows[capture.Id] = new PendingWindowEntry(hWnd, DateTimeOffset.UtcNow);
                _windowOperator.HideWindow(hWnd);
                _audioOperator?.Mute();
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[KakaoInterceptor] 윈도우 가로채기 처리 중 오류: {ex.Message}", ex);
            return false;
        }
    }

    private void CompleteCapture(Capture capture)
    {
        try
        {

            // 팝업 UI로부터 발신자 및 메시지 본문 추출
            KakaoNotificationText extracted = _windowOperator.ExtractNotificationText(capture.Hwnd);

            var rawNotification = new RawNotification(
                Id: capture.Id,
                AppName: "KakaoTalk.exe",
                Sender: extracted.Sender ?? string.Empty,
                Title: extracted.Title,
                Body: extracted.Body,
                Timestamp: DateTime.UtcNow);

            // 알림 파이프라인으로 이벤트 전달
            NotificationReceived?.Invoke(this, rawNotification);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[KakaoInterceptor] 알림 텍스트 추출/전달 중 오류: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 판정 완료 후 집중 모드에서 숨겨 둔 팝업을 닫습니다. 일반 수신 팝업은 건드리지 않습니다.
    /// </summary>
    public void OnNotificationProcessed(NotificationRecord record)
    {
        if (!_pendingWindows.TryRemove(record.Id, out var entry))
        {
            return;
        }

        try
        {
            // 통과 여부와 관계없이 원래 팝업·차임은 사용하지 않는다.
            // 원문과 판정 결과는 이미 DB 및 NotificationProcessed 이벤트에 전달된다.
            _windowOperator.CloseWindow(entry.Hwnd);
            AppLogger.Info($"[KakaoInterceptor] 집중 알림 팝업 닫기 완료 (hWnd=0x{entry.Hwnd:X8}, passed={record.IsPassed})");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[KakaoInterceptor] 알림 후속 처리 오류: {ex.Message}", ex);
        }
    }

    private void CleanupPendingWindows()
    {
        foreach (var kvp in _pendingWindows)
        {
            if (_pendingWindows.TryRemove(kvp.Key, out var entry))
            {
                try
                {
                    _windowOperator.CloseWindow(entry.Hwnd);
                }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        Stop();
    }
}

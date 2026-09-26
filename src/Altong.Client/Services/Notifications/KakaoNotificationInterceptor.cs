using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Altong.Client.Data.Models;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 카카오톡의 독자적인 Win32 알림 팝업을 포착하여 실시간 가로채기(스텔스 숨김) 및
/// AI 필터링 결과에 따라 복원(통과) 또는 소멸(차단)을 수행하는 전용 인터셉터.
/// </summary>
public sealed class KakaoNotificationInterceptor : IWindowsNotificationListener
{
    private const uint EventObjectShow = 0x8002;
    private const int ObjidWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private const uint DesktopAllAccess = 0x01FF;

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
    private static extern bool GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage([In] ref Msg lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenDesktop(string lpszDesktop, int dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(nint hDesktop);

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
    private readonly Func<bool> _isFocusModeEnabled;
    private readonly ConcurrentDictionary<string, PendingWindowEntry> _pendingWindows = new();

    private readonly object _syncRoot = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private nint _hookHandle;
    private WinEventDelegate? _winEventProc;
    private bool _isRunning;
    private bool _isDisposed;

    private record PendingWindowEntry(nint Hwnd, DateTimeOffset CreatedAt);

    public event EventHandler<RawNotification>? NotificationReceived;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _isRunning;
            }
        }
    }

    public KakaoNotificationInterceptor(
        Func<bool> isFocusModeEnabled,
        IKakaoWindowOperator? windowOperator = null)
    {
        _isFocusModeEnabled = isFocusModeEnabled ?? throw new ArgumentNullException(nameof(isFocusModeEnabled));
        _windowOperator = windowOperator ?? new LiveKakaoWindowOperator();
    }

    [DllImport("user32.dll")]
    private static extern nint FindWindowEx(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    private System.Threading.Timer? _scannerTimer;
    private uint _kakaoPid;

    public Task<bool> StartAsync()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isRunning)
            {
                return Task.FromResult(true);
            }

            var tcs = new TaskCompletionSource<bool>();

            _hookThread = new Thread(() => RunHookLoop(tcs))
            {
                IsBackground = true,
                Name = "KakaoNotificationHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            // 50ms 주기 초고속 윈도우 스캐너 가동 (이벤트 훅 누락 시에도 50ms 내 즉각 은닉 보장)
            _scannerTimer = new System.Threading.Timer(OnScannerTick, null, 50, 50);

            return tcs.Task;
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;

            _scannerTimer?.Dispose();
            _scannerTimer = null;

            if (_hookHandle != nint.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = nint.Zero;
            }

            if (_hookThreadId != 0)
            {
                PostThreadMessage(_hookThreadId, WmQuit, nint.Zero, nint.Zero);
            }
        }

        if (_hookThread is not null && _hookThread.IsAlive)
        {
            _hookThread.Join(1000);
            _hookThread = null;
        }

        CleanupPendingWindows();
        AppLogger.Info("[KakaoInterceptor] 카카오톡 가로채기 엔진 중지 완료.");
    }

    private void RunHookLoop(TaskCompletionSource<bool> tcs)
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();

            // 가비지 컬렉션 방지를 위해 필드에 delegate 유지
            _winEventProc = OnWinEvent;

            // KakaoTalk 프로세스 PID 직접 타깃팅
            uint kakaoPid = 0;
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("KakaoTalk");
                if (procs.Length > 0)
                {
                    kakaoPid = (uint)procs[0].Id;
                    _kakaoPid = kakaoPid;
                    AppLogger.Info($"[KakaoInterceptor] KakaoTalk 프로세스 감지 (PID: {kakaoPid})");
                }
            }
            catch { }

            // 시스템/오브젝트 전 이벤트(0x0001 ~ 0x7FFFFFFF) 훅 등록
            // 64비트 .NET 9 프로세스에서 32비트(WOW64) 카카오톡 창 이벤트를 누락 없이 수신하려면
            // idProcess=0(글로벌 훅)으로 등록 후 OnWinEvent 콜백에서 PID를 초고속(1ns) 필터링해야 함
            _hookHandle = SetWinEventHook(
                0x0001,
                0x7FFFFFFF,
                nint.Zero,
                _winEventProc,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);

            if (_hookHandle == nint.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                AppLogger.Error($"[KakaoInterceptor] SetWinEventHook 등록 실패: ErrorCode={err}");
                tcs.TrySetResult(false);
                return;
            }

            lock (_syncRoot)
            {
                _isRunning = true;
            }

            AppLogger.Info($"[KakaoInterceptor] 카카오톡 전용 가로채기 훅 엔진 가동 시작. (TargetPID={kakaoPid})");
            tcs.TrySetResult(true);

            // Win32 메시지 루프 가동 (훅 이벤트 처리를 위해 필수)
            while (GetMessage(out Msg msg, nint.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[KakaoInterceptor] 훅 스레드 비정상 종료: {ex.Message}", ex);
            tcs.TrySetResult(false);
        }
        finally
        {
            if (_hookHandle != nint.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = nint.Zero;
            }
        }
    }

    private void OnScannerTick(object? state)
    {
        if (!_isRunning)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;

            // 5초 이상 응답 없는 보류 창 자동 소멸 (타임아웃 안전망)
            foreach (var kvp in _pendingWindows)
            {
                if (now - kvp.Value.CreatedAt > TimeSpan.FromSeconds(5))
                {
                    if (_pendingWindows.TryRemove(kvp.Key, out var expired))
                    {
                        AppLogger.Warn($"[KakaoInterceptor] ⚠️ 알림 처리 타임아웃(5초 초과): 창을 안전하게 소멸합니다. (hWnd=0x{expired.Hwnd:X8})");
                        _windowOperator.CloseWindow(expired.Hwnd);
                    }
                }
            }

            nint hWnd = nint.Zero;
            while ((hWnd = FindWindowEx(nint.Zero, hWnd, null, null)) != nint.Zero)
            {
                if (_recentlyHandledHwnds.TryGetValue(hWnd, out var lastTime) && now - lastTime < TimeSpan.FromSeconds(3.0))
                {
                    continue;
                }

                if (_windowOperator.IsNotificationWindow(hWnd))
                {
                    HandleWindowShowEvent(hWnd);
                }
            }
        }
        catch { }
    }

    private readonly ConcurrentDictionary<nint, DateTimeOffset> _recentlyHandledHwnds = new();

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hWnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hWnd == nint.Zero)
        {
            return;
        }

        // 창 레벨 이벤트(OBJID_WINDOW=0 또는 OBJID_CLIENT=-4)만 수신
        if (idObject != 0 && idObject != -4)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_recentlyHandledHwnds.TryGetValue(hWnd, out var lastTime) && now - lastTime < TimeSpan.FromSeconds(3.0))
        {
            return;
        }

        // 카카오톡 프로세스의 창인지 확인 (TargetPID 필터링)
        if (_kakaoPid != 0)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != _kakaoPid)
            {
                return;
            }
        }

        HandleWindowShowEvent(hWnd);
    }

    /// <summary>
    /// 카카오톡 윈도우 표시 이벤트를 처리합니다. (단위 테스트 및 모의 환경에서 직접 호출 가능)
    /// </summary>
    public void HandleWindowShowEvent(nint hWnd)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;

            // 1. 원자적(Atomic) 중복 진입 차단: 동시 호출(훅 + 타이머) 중 최초 1개 스레드만 허용
            if (!_recentlyHandledHwnds.TryAdd(hWnd, now))
            {
                if (_recentlyHandledHwnds.TryGetValue(hWnd, out var lastTime) && now - lastTime < TimeSpan.FromSeconds(3.0))
                {
                    return;
                }
                _recentlyHandledHwnds[hWnd] = now;
            }

            if (_recentlyHandledHwnds.Count > 100)
            {
                foreach (var kv in _recentlyHandledHwnds)
                {
                    if (now - kv.Value > TimeSpan.FromSeconds(10))
                    {
                        _recentlyHandledHwnds.TryRemove(kv.Key, out _);
                    }
                }
            }

            // 2. 카카오톡 알림 팝업 창 여부 판정
            if (!_windowOperator.IsNotificationWindow(hWnd))
            {
                // 알림 창이 아닌 일반 윈도우는 3초간 재검사를 건너뛰어 반복 부하 및 오인 방지
                return;
            }

            bool isFocusMode = _isFocusModeEnabled();
            AppLogger.Info($"[KakaoInterceptor] 🎯 카카오톡 알림 팝업 창 포착 성공: hWnd=0x{hWnd:X8} (집중모드={isFocusMode})");

            // 2. 집중 모드 활성화 시: 사용자 눈에 보이기 전에 즉시 스텔스 숨김 (0ms 차단)
            if (isFocusMode)
            {
                _windowOperator.HideWindow(hWnd);
                AppLogger.Info($"[KakaoInterceptor] 🚨 카카오톡 알림 팝업 즉각 스텔스 은닉 완료: hWnd=0x{hWnd:X8}");
            }

            // 3. 팝업 UI로부터 발신자 및 메시지 본문 추출
            KakaoNotificationText extracted = _windowOperator.ExtractNotificationText(hWnd);

            string notificationId = $"kakao_{Guid.NewGuid():N}";
            var rawNotification = new RawNotification(
                Id: notificationId,
                AppName: "KakaoTalk.exe",
                Sender: extracted.Sender ?? string.Empty,
                Title: extracted.Title,
                Body: extracted.Body,
                Timestamp: DateTime.UtcNow);

            // 4. 집중 모드인 경우 AI 판정 대기 목록에 등록
            if (isFocusMode)
            {
                _pendingWindows[notificationId] = new PendingWindowEntry(hWnd, DateTimeOffset.UtcNow);
            }

            AppLogger.Info($"[KakaoInterceptor] 카카오톡 알림 포착: {extracted.Sender} - '{extracted.Body}' (집중모드={isFocusMode})");

            // 5. 알림 파이프라인으로 이벤트 전달
            NotificationReceived?.Invoke(this, rawNotification);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[KakaoInterceptor] 윈도우 가로채기 처리 중 오류: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 알림 파이프라인에서 AI 평가 및 DB 저장이 완료되었을 때 호출되어 창 복원 또는 소멸을 수행합니다.
    /// </summary>
    public void OnNotificationProcessed(NotificationRecord record)
    {
        if (!_pendingWindows.TryRemove(record.Id, out var entry))
        {
            return;
        }

        try
        {
            if (record.IsPassed == true)
            {
                // 통과(중요/긴급 알림): 화면에 다시 복원 표시
                _windowOperator.ShowWindow(entry.Hwnd);
                AppLogger.Info($"[KakaoInterceptor] 중요 카카오 알림 복원 표시: {record.Sender} - '{record.Title}'");
            }
            else
            {
                // 차단 알림: 조용히 윈도우 닫기
                _windowOperator.CloseWindow(entry.Hwnd);
                AppLogger.Info($"[KakaoInterceptor] 차단 카카오 알림 완전 소멸: {record.Sender} - '{record.Title}'");
            }
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

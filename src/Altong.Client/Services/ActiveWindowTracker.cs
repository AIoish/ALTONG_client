using Altong.Client.Models;
using Altong.Client.Services.Win32;

namespace Altong.Client.Services;

/// <summary>
/// Win32 API 기반으로 활성 창을 주기적으로 감지하고,
/// 5초 안정화 룰(포커스 튐 방어) 및 최근 작업 세트(Recent Working Set)를 관리하는 트래커 구현체.
/// </summary>
public sealed class ActiveWindowTracker : IActiveWindowTracker
{
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultStabilizationThreshold = TimeSpan.FromSeconds(5);
    private const int MaxRecentProcesses = 3;

    private readonly IWindowInfoProvider _windowInfoProvider;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _stabilizationThreshold;
    private readonly int _selfProcessId;
    private readonly TimeProvider _timeProvider;

    private readonly object _syncRoot = new();
    private readonly List<string> _recentProcesses = [];

    private WindowInfo? _stableWindow;
    private DateTimeOffset _stableEnteredAt;

    private WindowInfo? _candidateWindow;
    private DateTimeOffset _candidateEnteredAt;

    private CurrentContext _currentContext = CurrentContext.Empty;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;
    private bool _isDisposed;

    public CurrentContext CurrentContext
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentContext;
            }
        }
    }

    public event EventHandler<CurrentContext>? ContextChanged;
    public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;

    public ActiveWindowTracker()
        : this(new LiveWindowInfoProvider(), DefaultPollingInterval, DefaultStabilizationThreshold)
    {
    }

    public ActiveWindowTracker(
        IWindowInfoProvider windowInfoProvider,
        TimeSpan pollingInterval,
        TimeSpan stabilizationThreshold,
        int? selfProcessId = null,
        TimeProvider? timeProvider = null)
    {
        _windowInfoProvider = windowInfoProvider ?? throw new ArgumentNullException(nameof(windowInfoProvider));
        _pollingInterval = pollingInterval;
        _stabilizationThreshold = stabilizationThreshold;
        _selfProcessId = selfProcessId ?? Environment.ProcessId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Start()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_cancellationTokenSource is not null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _cancellationTokenSource = cts;

            // 시작 즉시 초기 활성 창 1회 캡처 (0초 시점 맥락 확보)
            EvaluateCurrentWindowLocked(_timeProvider.GetUtcNow());

            _pollingTask = Task.Run(() => RunPollingLoopAsync(cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_syncRoot)
        {
            if (_cancellationTokenSource is null)
            {
                return;
            }

            cts = _cancellationTokenSource;
            task = _pollingTask;

            _cancellationTokenSource = null;
            _pollingTask = null;
        }

        cts.Cancel();

        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // 의도된 정상 취소
        }
        finally
        {
            cts.Dispose();
        }
    }

    public CurrentContext CaptureNow()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return EvaluateCurrentWindowLocked(_timeProvider.GetUtcNow());
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

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollingInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                CurrentContext? updatedContext = null;

                lock (_syncRoot)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    CurrentContext previous = _currentContext;
                    CurrentContext current = EvaluateCurrentWindowLocked(_timeProvider.GetUtcNow());

                    if (HasContextChangedSignificantly(previous, current))
                    {
                        updatedContext = current;
                    }
                }

                if (updatedContext is not null)
                {
                    ContextChanged?.Invoke(this, updatedContext);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 루프 탈출
        }
    }

    private CurrentContext EvaluateCurrentWindowLocked(DateTimeOffset now)
    {
        WindowInfo? window = _windowInfoProvider.GetForegroundWindowInfo();

        // 1. 유효하지 않은 창 핸들이거나 알통 자체 창인 경우 직전 안정 맥락 유지
        if (window is null || window.Handle == nint.Zero || window.ProcessId == _selfProcessId)
        {
            return UpdateDurationForStableWindow(now);
        }

        // 2. 최초 실행 시점 (초기 창 즉시 안정화)
        if (_stableWindow is null)
        {
            return StabilizeWindow(window, now);
        }

        // 3. 현재 창이 기존 안정 창과 동일한 경우 (HWND 및 타이틀 일치)
        if (window.Handle == _stableWindow.Handle && window.WindowTitle == _stableWindow.WindowTitle)
        {
            if (_candidateWindow is not null)
            {
                Console.WriteLine($"[{now.ToLocalTime():HH:mm:ss}] [DEFENSE] {_candidateWindow.ProcessName} (5초 미만 이탈) -> 직전 맥락 유지: {_stableWindow.ProcessName}");
                _candidateWindow = null;
            }

            return UpdateDurationForStableWindow(now);
        }

        // 4. 창이 변경되었거나 동일 창 내 탭(타이틀)이 변경된 경우 -> 5초 룰 검증
        if (_candidateWindow is null ||
            _candidateWindow.Handle != window.Handle ||
            _candidateWindow.WindowTitle != window.WindowTitle)
        {
            // 새로운 후보 창 진입 감지 (후보 타이머 시작)
            _candidateWindow = window;
            _candidateEnteredAt = now;

            Console.WriteLine($"[{now.ToLocalTime():HH:mm:ss}] [WAIT]    {window.ProcessName} ('{TruncateTitle(window.WindowTitle)}') (5초 체류 검증 시작)");

            // 아직 5초가 안 되었으므로 기존 안정 작업 맥락 유지
            return UpdateDurationForStableWindow(now);
        }

        // 동일 후보 창에 계속 머무르는 중 -> 5초 경과 여부 확인
        TimeSpan candidateDuration = now - _candidateEnteredAt;
        if (candidateDuration >= _stabilizationThreshold)
        {
            // 5초 룰 통과: 정식 안정 창으로 승격!
            return StabilizeWindow(_candidateWindow, _candidateEnteredAt, now);
        }

        // 아직 5초 미만: 직전 안정 맥락 유지
        return UpdateDurationForStableWindow(now);
    }

    private CurrentContext StabilizeWindow(WindowInfo window, DateTimeOffset enteredAt, DateTimeOffset? now = null)
    {
        DateTimeOffset currentNow = now ?? enteredAt;

        // 직전 안정 창이 존재하고, 다른 창으로 전환되는 경우 직전 세션 종료 [OUT] 선출력 및 이벤트 발행
        if (_stableWindow is not null &&
            (_stableWindow.Handle != window.Handle || _stableWindow.WindowTitle != window.WindowTitle))
        {
            int previousTotalDuration = Math.Max(0, (int)(enteredAt - _stableEnteredAt).TotalSeconds);
            Console.WriteLine($"[{currentNow.ToLocalTime():HH:mm:ss}] [OUT]     {_stableWindow.ProcessName} ('{TruncateTitle(_stableWindow.WindowTitle)}') 최종 체류: {previousTotalDuration}s");

            var sessionEnded = new WindowSessionEndedEventArgs(
                _stableWindow.ProcessName,
                _stableWindow.WindowTitle,
                _stableEnteredAt,
                enteredAt,
                previousTotalDuration);

            WindowSessionEnded?.Invoke(this, sessionEnded);
        }

        _stableWindow = window;
        _stableEnteredAt = enteredAt;
        _candidateWindow = null;

        UpdateRecentProcesses(window.ProcessName);

        int durationSeconds = Math.Max(0, (int)(currentNow - _stableEnteredAt).TotalSeconds);

        _currentContext = new CurrentContext(
            ActiveProcess: window.ProcessName,
            WindowTitle: window.WindowTitle,
            LastUpdated: currentNow.UtcDateTime,
            DurationSeconds: durationSeconds,
            RecentProcesses: [.. _recentProcesses]);

        Console.WriteLine($"[{currentNow.ToLocalTime():HH:mm:ss}] [CONFIRM] {window.ProcessName} ('{TruncateTitle(window.WindowTitle)}') | 콤비: [{string.Join(", ", _recentProcesses)}]");

        return _currentContext;
    }

    private CurrentContext UpdateDurationForStableWindow(DateTimeOffset now)
    {
        if (_stableWindow is null)
        {
            return _currentContext;
        }

        int durationSeconds = Math.Max(0, (int)(now - _stableEnteredAt).TotalSeconds);

        _currentContext = _currentContext with
        {
            DurationSeconds = durationSeconds,
            LastUpdated = now.UtcDateTime
        };

        return _currentContext;
    }

    private void UpdateRecentProcesses(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        _recentProcesses.Remove(processName);
        _recentProcesses.Insert(0, processName);

        if (_recentProcesses.Count > MaxRecentProcesses)
        {
            _recentProcesses.RemoveRange(MaxRecentProcesses, _recentProcesses.Count - MaxRecentProcesses);
        }
    }

    private static bool HasContextChangedSignificantly(CurrentContext previous, CurrentContext current)
    {
        return previous.ActiveProcess != current.ActiveProcess ||
               previous.WindowTitle != current.WindowTitle;
    }

    private static string TruncateTitle(string title, int maxLength = 65)
    {
        if (string.IsNullOrEmpty(title) || title.Length <= maxLength)
        {
            return title ?? string.Empty;
        }

        int prefixLength = (maxLength - 3) / 2 + 1;
        int suffixLength = (maxLength - 3) / 2;

        return string.Concat(
            title.AsSpan(0, prefixLength),
            "...",
            title.AsSpan(title.Length - suffixLength, suffixLength));
    }
}

using Altong.Client.Services.Win32;

namespace Altong.Client.Services.Focus;

/// <summary>
/// 활성 창·입력·전체화면 상태를 모아 <see cref="AutoDndStateMachine"/>에 넘기고, 제안 배너를 열고 닫을 때와
/// 자리비움으로 집중을 끝낼 때를 알린다. 집중 모드를 직접 켜고 끄지 않는다. 코디네이터 호출은 앱이 맡는다.
/// 로그에는 프로세스 분류와 상태만 남기고 창 제목은 남기지 않는다.
/// </summary>
public sealed class AutoDndController
{
    private readonly IActiveWindowTracker _tracker;
    private readonly IUserIdleDetector _idle;
    private readonly IFullscreenDetector _fullscreen;
    private readonly Func<bool> _isFocusEnabled;
    private readonly Func<bool> _isAvailable;
    private readonly Func<bool> _isOnBreak;

    public AutoDndController(
        IActiveWindowTracker tracker,
        IUserIdleDetector idle,
        IFullscreenDetector fullscreen,
        Func<bool> isFocusEnabled,
        Func<bool> isAvailable,
        AutoDndStateMachine? machine = null,
        Func<bool>? isOnBreak = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _idle = idle ?? throw new ArgumentNullException(nameof(idle));
        _fullscreen = fullscreen ?? throw new ArgumentNullException(nameof(fullscreen));
        _isFocusEnabled = isFocusEnabled ?? throw new ArgumentNullException(nameof(isFocusEnabled));
        _isAvailable = isAvailable ?? throw new ArgumentNullException(nameof(isAvailable));
        _isOnBreak = isOnBreak ?? (() => false);
        Machine = machine ?? new AutoDndStateMachine();
        Machine.StateChanged += Machine_StateChanged;
        Machine.AwayExitRequested += Machine_AwayExitRequested;
    }

    public AutoDndStateMachine Machine { get; }

    /// <summary>제안 배너를 띄울 때.</summary>
    public event EventHandler? SuggestionRequested;

    /// <summary>수락·거절·무응답·집중 시작 등으로 제안 배너를 닫아야 할 때.</summary>
    public event EventHandler? SuggestionClosed;

    /// <summary>집중 중 자리를 오래 비워 집중을 끝내야 할 때 (이탈).</summary>
    public event EventHandler? AwayExitRequested;

    /// <summary>약 1초마다 UI 스레드에서 호출한다.</summary>
    public void Tick()
    {
        bool available = _isAvailable();
        bool focusEnabled = _isFocusEnabled();
        var category = WorkContextClassifier.Classify(_tracker.CurrentContext.ActiveProcess);
        // 꺼져 있으면 Win32를 부르지 않는다. 입력 유휴는 진입(제안 시점)과 이탈(자리비움) 모두에 쓰고,
        // 전체화면은 제안할 때만 의미가 있다.
        var idle = available ? _idle.GetIdleTime() : TimeSpan.Zero;
        bool fullscreen = available && !focusEnabled && _fullscreen.IsFullscreenActive();
        bool onBreak = focusEnabled && _isOnBreak();
        Machine.Update(new AutoDndObservation(category, idle, fullscreen, focusEnabled, available, onBreak));
    }

    /// <summary>[집중 시작]. 배너가 떠 있을 때만 true를 돌려준다. true면 앱이 집중 모드를 켠다.</summary>
    public bool Accept() => Machine.Accept();

    /// <summary>[나중에].</summary>
    public bool Reject() => Machine.Reject();

    private void Machine_AwayExitRequested(object? sender, EventArgs e)
    {
        AppLogger.Info($"[AutoDND] 자리비움 {Machine.Options.AwayExit.TotalMinutes:0}분 이상 -> 집중 종료 요청");
        AwayExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Machine_StateChanged(object? sender, (AutoDndState Previous, AutoDndState Current) change)
    {
        var category = WorkContextClassifier.Classify(_tracker.CurrentContext.ActiveProcess);
        AppLogger.Info($"[AutoDND] {change.Previous} -> {change.Current} " +
            $"(분류 {category}, 작업 {Machine.WorkInWindow.TotalSeconds:0}s, 비작업 {Machine.LeisureInWindow.TotalSeconds:0}s, 연속 거절 {Machine.RejectionCount})");
        if (change.Current == AutoDndState.Suggesting)
            SuggestionRequested?.Invoke(this, EventArgs.Empty);
        else if (change.Previous == AutoDndState.Suggesting)
            SuggestionClosed?.Invoke(this, EventArgs.Empty);
    }
}

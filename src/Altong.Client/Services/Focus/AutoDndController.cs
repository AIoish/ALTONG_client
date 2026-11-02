using Altong.Client.Services.Win32;

namespace Altong.Client.Services.Focus;

/// <summary>
/// 활성 창·입력·전체화면 상태를 모아 <see cref="AutoDndStateMachine"/>에 넘기고, 제안 배너를 열고 닫을 때를 알린다.
/// 집중 모드를 직접 켜지 않는다. 수락 처리(코디네이터 호출)는 앱이 맡는다.
/// 로그에는 프로세스 분류와 상태만 남기고 창 제목은 남기지 않는다.
/// </summary>
public sealed class AutoDndController
{
    private readonly IActiveWindowTracker _tracker;
    private readonly IUserIdleDetector _idle;
    private readonly IFullscreenDetector _fullscreen;
    private readonly Func<bool> _isFocusEnabled;
    private readonly Func<bool> _isAvailable;

    public AutoDndController(
        IActiveWindowTracker tracker,
        IUserIdleDetector idle,
        IFullscreenDetector fullscreen,
        Func<bool> isFocusEnabled,
        Func<bool> isAvailable,
        AutoDndStateMachine? machine = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _idle = idle ?? throw new ArgumentNullException(nameof(idle));
        _fullscreen = fullscreen ?? throw new ArgumentNullException(nameof(fullscreen));
        _isFocusEnabled = isFocusEnabled ?? throw new ArgumentNullException(nameof(isFocusEnabled));
        _isAvailable = isAvailable ?? throw new ArgumentNullException(nameof(isAvailable));
        Machine = machine ?? new AutoDndStateMachine();
        Machine.StateChanged += Machine_StateChanged;
    }

    public AutoDndStateMachine Machine { get; }

    /// <summary>제안 배너를 띄울 때.</summary>
    public event EventHandler? SuggestionRequested;

    /// <summary>수락·거절·무응답·집중 시작 등으로 제안 배너를 닫아야 할 때.</summary>
    public event EventHandler? SuggestionClosed;

    /// <summary>약 1초마다 UI 스레드에서 호출한다.</summary>
    public void Tick()
    {
        bool available = _isAvailable();
        bool focusEnabled = _isFocusEnabled();
        var category = WorkContextClassifier.Classify(_tracker.CurrentContext.ActiveProcess);
        // 비활성 상태에서는 Win32를 부르지 않는다. 전체화면은 제안 직전에만 의미가 있지만 호출 비용이 작아 매번 확인한다.
        var idle = available && !focusEnabled ? _idle.GetIdleTime() : TimeSpan.Zero;
        bool fullscreen = available && !focusEnabled && _fullscreen.IsFullscreenActive();
        Machine.Update(new AutoDndObservation(category, idle, fullscreen, focusEnabled, available));
    }

    /// <summary>[집중 시작]. 배너가 떠 있을 때만 true를 돌려준다. true면 앱이 집중 모드를 켠다.</summary>
    public bool Accept() => Machine.Accept();

    /// <summary>[나중에].</summary>
    public bool Reject() => Machine.Reject();

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

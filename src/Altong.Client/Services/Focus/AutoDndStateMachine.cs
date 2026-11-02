namespace Altong.Client.Services.Focus;

public enum AutoDndState
{
    /// <summary>설정에서 꺼졌거나 Windows 백엔드라 동작하지 않는다.</summary>
    Disabled,
    /// <summary>최근 작업 기록이 없다.</summary>
    Standby,
    /// <summary>작업 시간을 쌓는 중이다.</summary>
    WarmingUp,
    /// <summary>조건은 채웠지만 사용자가 입력하지 않거나 전체화면이라 제안을 미루는 중이다.</summary>
    PendingInput,
    /// <summary>제안 배너를 보여 주는 중이다.</summary>
    Suggesting,
    /// <summary>이번 작업 흐름에서는 이미 제안했으므로 흐름이 끊길 때까지 다시 묻지 않는다.</summary>
    Suppressed,
    /// <summary>[나중에]를 눌러 일정 시간 묻지 않는다.</summary>
    Cooldown,
    /// <summary>집중 모드가 켜져 있다.</summary>
    InFocus,
}

/// <summary>상태 머신이 한 번 판단할 때 보는 관찰값.</summary>
/// <param name="Category">지금 활성 창의 분류.</param>
/// <param name="IdleTime">마지막 키보드·마우스 입력 이후 지난 시간.</param>
/// <param name="IsFullscreen">전체화면 앱·프레젠테이션 중인지.</param>
/// <param name="IsFocusEnabled">집중 모드가 켜져 있는지 (수동·제안 수락 모두).</param>
/// <param name="IsAvailable">AutoDND 설정이 켜져 있고 Altong 백엔드인지.</param>
/// <param name="IsOnBreak">집중 타이머의 휴식 시간인지. 휴식 중 자리비움은 정상이라 자동 종료하지 않는다.</param>
public readonly record struct AutoDndObservation(
    WorkCategory Category,
    TimeSpan IdleTime,
    bool IsFullscreen,
    bool IsFocusEnabled,
    bool IsAvailable = true,
    bool IsOnBreak = false);

public sealed record AutoDndOptions
{
    /// <summary>작업 여부를 판단하는 최근 구간.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>구간 안에서 필요한 최소 작업 시간.</summary>
    public TimeSpan RequiredWork { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>구간 안에서 허용하는 비작업 시간(이 값 이상이면 진입하지 않는다).</summary>
    public TimeSpan MaxLeisure { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>이 시간 안에 입력이 있었으면 사용자가 화면 앞에 있다고 본다.</summary>
    public TimeSpan ActiveInput { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>이 시간 이상 입력이 없으면 작업 창이 떠 있어도 자리비움으로 보고 작업 시간에 넣지 않는다 (인강·PDF 허용 한도).</summary>
    public TimeSpan AwayIdle { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>배너를 자동으로 닫기까지의 시간.</summary>
    public TimeSpan SuggestionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// 작업이 이만큼 끊기거나 이만큼 자리를 비우면 작업 흐름이 끝난 것(휴식)으로 보고 다시 제안할 수 있다.
    /// 무응답 뒤와 집중 종료 뒤에 같은 기준을 쓴다.
    /// </summary>
    public TimeSpan StreakReset { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>집중 중 입력이 이만큼 없으면 자리를 비운 것으로 보고 집중 종료를 요청한다 (타이머 휴식 시간 제외).</summary>
    public TimeSpan AwayExit { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>연속 거절 횟수별 제안 금지 시간. 마지막 값을 계속 쓴다.</summary>
    public IReadOnlyList<TimeSpan> RejectionCooldowns { get; init; } =
        [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];
    /// <summary>판단 간격이 이보다 벌어지면(절전·타이머 지연) 그 사이를 작업 시간으로 인정하지 않는다.</summary>
    public TimeSpan MaxSampleGap { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// 작업 맥락을 보고 집중 모드 진입을 "제안"할 시점을 정하는 상태 머신. UI·Win32와 분리해 가상 시간으로 검증한다.
/// <list type="bullet">
/// <item>최근 120초 중 작업 90초 이상, 비작업 15초 미만이면 제안한다. Neutral(카톡·탐색기)은 진행도를 깎지 않는다.</item>
/// <item>입력이 없거나 전체화면이면 <see cref="AutoDndState.PendingInput"/>으로 미뤘다가 다음 입력 때 제안한다.</item>
/// <item>무응답(15초)은 거절로 세지 않고, 이번 작업 흐름 동안만 다시 묻지 않는다.</item>
/// <item>[나중에]는 15→30→60분 쿨다운.</item>
/// <item>집중을 끈 뒤 계속 작업하면 묻지 않고, 5분 이상 쉬었다가 다시 작업하면 제안한다.</item>
/// <item>집중 중 15분 동안 입력이 없으면 <see cref="AwayExitRequested"/>로 종료를 요청한다 (이탈).</item>
/// </list>
/// 앱 재시작 시 거절 횟수는 초기화된다.
/// </summary>
public sealed class AutoDndStateMachine
{
    private readonly TimeProvider _time;
    private readonly AutoDndOptions _options;
    private readonly Queue<Sample> _samples = new();
    private DateTimeOffset? _lastSampleAt;
    private DateTimeOffset? _lastWorkAt;
    private DateTimeOffset _suggestedAt;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private TimeSpan _work;
    private TimeSpan _leisure;
    private bool _streakSuppressed;
    private bool _awayExitRequested;

    public AutoDndStateMachine(TimeProvider? timeProvider = null, AutoDndOptions? options = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new AutoDndOptions();
        if (_options.RejectionCooldowns.Count == 0)
            throw new ArgumentException("At least one rejection cooldown is required.", nameof(options));
    }

    public AutoDndOptions Options => _options;

    public AutoDndState State { get; private set; } = AutoDndState.Standby;

    /// <summary>연속 거절 횟수. 수락하면 0으로 돌아간다.</summary>
    public int RejectionCount { get; private set; }

    /// <summary>최근 구간에 쌓인 작업 시간.</summary>
    public TimeSpan WorkInWindow => _work;

    /// <summary>최근 구간에 쌓인 비작업 시간.</summary>
    public TimeSpan LeisureInWindow => _leisure;

    /// <summary>쿨다운이 끝나는 시각. 쿨다운이 없으면 null.</summary>
    public DateTimeOffset? CooldownUntil => _cooldownUntil > _time.GetUtcNow() ? _cooldownUntil : null;

    /// <summary>상태가 바뀔 때 (이전 상태, 새 상태)로 알린다.</summary>
    public event EventHandler<(AutoDndState Previous, AutoDndState Current)>? StateChanged;

    /// <summary>집중 중 자리를 오래 비워 집중을 끝내야 할 때. 집중 세션 하나에 한 번만 알린다.</summary>
    public event EventHandler? AwayExitRequested;

    /// <summary>약 1초마다 호출해 관찰값을 반영하고 상태를 다시 정한다.</summary>
    public void Update(AutoDndObservation observation)
    {
        var now = _time.GetUtcNow();

        if (!observation.IsAvailable)
        {
            ResetTracking();
            SetState(AutoDndState.Disabled);
            return;
        }

        if (observation.IsFocusEnabled)
        {
            if (State != AutoDndState.InFocus)
            {
                ResetTracking();
                _streakSuppressed = false;
                _awayExitRequested = false;
            }
            SetState(AutoDndState.InFocus);

            // 이탈: 자리를 오래 비우면 집중을 끝낸다. 타이머 휴식 중 자리비움은 정상이므로 휴식이 끝난 뒤에 판단한다.
            if (!_awayExitRequested && !observation.IsOnBreak && observation.IdleTime >= _options.AwayExit)
            {
                _awayExitRequested = true;
                AwayExitRequested?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (State == AutoDndState.InFocus)
        {
            // 집중을 끈 뒤 계속 작업하면 묻지 않는다 (집중 없이 하겠다는 뜻으로 본다).
            // 5분 이상 쉬었다가 돌아와 다시 작업하면 제안한다. 자리비움으로 끝났다면 이미 쉰 것이라 바로 풀린다.
            _streakSuppressed = true;
            _lastWorkAt = now;
        }

        bool away = observation.IdleTime >= _options.AwayIdle;
        Record(now, away ? WorkCategory.Neutral : observation.Category);

        if (_streakSuppressed && HasStreakEnded(now, observation.IdleTime))
            _streakSuppressed = false;

        if (State == AutoDndState.Suggesting)
        {
            if (now - _suggestedAt < _options.SuggestionTimeout) return;
            // 무응답: 작업에 빠져 배너를 못 봤을 가능성이 크다. 거절로 세지 않고 이번 흐름에서만 다시 묻지 않는다.
            _streakSuppressed = true;
        }

        if (_cooldownUntil > now)
        {
            SetState(AutoDndState.Cooldown);
            return;
        }

        if (_streakSuppressed)
        {
            SetState(AutoDndState.Suppressed);
            return;
        }

        if (_work < _options.RequiredWork || _leisure >= _options.MaxLeisure)
        {
            SetState(_work > TimeSpan.Zero ? AutoDndState.WarmingUp : AutoDndState.Standby);
            return;
        }

        if (observation.IdleTime <= _options.ActiveInput && !observation.IsFullscreen)
        {
            _suggestedAt = now;
            SetState(AutoDndState.Suggesting);
            return;
        }

        SetState(AutoDndState.PendingInput);
    }

    /// <summary>[집중 시작]. 거절 횟수를 초기화한다. 집중 모드가 실제로 켜지지 않아도 이번 흐름에서는 다시 묻지 않는다.</summary>
    public bool Accept()
    {
        if (State != AutoDndState.Suggesting) return false;
        RejectionCount = 0;
        _streakSuppressed = true;
        SetState(AutoDndState.Suppressed);
        return true;
    }

    /// <summary>[나중에]. 연속 거절 횟수에 따라 15→30→60분 동안 묻지 않는다.</summary>
    public bool Reject()
    {
        if (State != AutoDndState.Suggesting) return false;
        RejectionCount++;
        var cooldowns = _options.RejectionCooldowns;
        var cooldown = cooldowns[Math.Min(RejectionCount, cooldowns.Count) - 1];
        _cooldownUntil = _time.GetUtcNow() + cooldown;
        SetState(AutoDndState.Cooldown);
        return true;
    }

    private bool HasStreakEnded(DateTimeOffset now, TimeSpan idle) =>
        idle >= _options.StreakReset || _lastWorkAt is null || now - _lastWorkAt.Value >= _options.StreakReset;

    private void Record(DateTimeOffset now, WorkCategory category)
    {
        var duration = TimeSpan.Zero;
        if (_lastSampleAt is { } last && now > last)
        {
            duration = now - last;
            if (duration > _options.MaxSampleGap) duration = TimeSpan.Zero;
        }
        _lastSampleAt = now;

        if (category == WorkCategory.Work) _lastWorkAt = now;
        _samples.Enqueue(new Sample(now, category, duration));
        Add(category, duration);

        var windowStart = now - _options.Window;
        while (_samples.Count > 0 && _samples.Peek().At <= windowStart)
        {
            var old = _samples.Dequeue();
            Add(old.Category, -old.Duration);
        }
    }

    private void Add(WorkCategory category, TimeSpan duration)
    {
        if (category == WorkCategory.Work) _work += duration;
        else if (category == WorkCategory.Leisure) _leisure += duration;
    }

    private void ResetTracking()
    {
        _samples.Clear();
        _work = TimeSpan.Zero;
        _leisure = TimeSpan.Zero;
        _lastSampleAt = null;
        _lastWorkAt = null;
    }

    private void SetState(AutoDndState state)
    {
        if (State == state) return;
        var previous = State;
        State = state;
        StateChanged?.Invoke(this, (previous, state));
    }

    private readonly record struct Sample(DateTimeOffset At, WorkCategory Category, TimeSpan Duration);
}

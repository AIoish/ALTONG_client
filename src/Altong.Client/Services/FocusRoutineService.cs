namespace Altong.Client.Services;

public enum FocusRoutinePhase { Idle, Focus, Break }

/// <summary>
/// Windows 집중 상태를 변경하지 않는 집중/휴식 타이머.
/// 한 번 시작한 루틴은 시작 당시 설정을 사용하고 지연된 tick도 경과 시간으로 보정한다.
/// </summary>
public sealed class FocusRoutineService(
    FocusModeService focusMode, FocusSettingsStore settings, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? _startedAt;
    private FocusTimerSettings _sessionSettings = new();

    public FocusRoutinePhase Phase { get; private set; }
    public TimeSpan Remaining { get; private set; }
    public event EventHandler? PhaseChanged;

    public void Refresh()
    {
        var previous = Phase;
        if (!focusMode.IsEnabled)
        {
            _startedAt = null;
            Phase = FocusRoutinePhase.Idle;
            Remaining = TimeSpan.Zero;
        }
        else
        {
            if (_startedAt is null)
            {
                _startedAt = _time.GetUtcNow();
                _sessionSettings = settings.Current;
            }

            var focus = TimeSpan.FromMinutes(_sessionSettings.FocusMinutes);
            var cycle = focus + TimeSpan.FromMinutes(_sessionSettings.BreakMinutes);
            long elapsed = Math.Max(0, (_time.GetUtcNow() - _startedAt.Value).Ticks);
            var position = TimeSpan.FromTicks(elapsed % cycle.Ticks);
            Phase = position < focus ? FocusRoutinePhase.Focus : FocusRoutinePhase.Break;
            Remaining = Phase == FocusRoutinePhase.Focus ? focus - position : cycle - position;
        }

        if (Phase != previous)
            PhaseChanged?.Invoke(this, EventArgs.Empty);
    }

    public string StatusText
    {
        get
        {
            if (Phase == FocusRoutinePhase.Idle)
                return "집중 모드를 켜면 타이머가 시작됩니다.";
            long seconds = (long)Math.Ceiling(Remaining.TotalSeconds);
            return $"{(Phase == FocusRoutinePhase.Focus ? "집중" : "휴식")} · {seconds / 60:00}:{seconds % 60:00} 남음";
        }
    }
}

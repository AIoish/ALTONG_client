namespace Altong.Client.Services;

public enum DockPresentation { Classic, RightNotch, Stealth }

public sealed record DockRoutineStatus(FocusRoutinePhase Phase)
{
    public static DockRoutineStatus Idle { get; } = new(FocusRoutinePhase.Idle);
    public string Label => Phase switch
    {
        FocusRoutinePhase.Focus => "집중 중",
        FocusRoutinePhase.Break => "휴식 중",
        FocusRoutinePhase.Completed => "집중 시간 완료 · 집중모드 켜짐",
        _ => "집중 대기",
    };
}

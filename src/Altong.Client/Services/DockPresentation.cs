namespace Altong.Client.Services;

public enum DockPresentation { Classic, RightNotch, Stealth, TopNotch }

/// <summary>상단 노치 디자인에서 화면 맨 위 띠의 색. 배경색은 바탕화면 그림 위쪽의 평균색을 따른다.</summary>
public enum TopBandStyle { Black, Wallpaper }

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

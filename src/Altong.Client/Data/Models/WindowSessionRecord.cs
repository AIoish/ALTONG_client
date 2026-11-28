namespace Altong.Client.Data.Models;

/// <summary>
/// SQLite window_sessions 테이블에 매핑되는 활성 창 작업 세션 엔티티.
/// ActiveWindowTracker가 [OUT] 시점에 기록한 세션별 총 체류 시간을 보관하며,
/// 팀원 3의 GitHub TIL(업무 일지) 자동 생성 원천 데이터로 사용됩니다.
/// </summary>
public record WindowSessionRecord(
    long Id,
    string ProcessName,
    string WindowTitle,
    DateTime StartedAt,
    DateTime EndedAt,
    int DurationSeconds,
    string? SessionId = null);

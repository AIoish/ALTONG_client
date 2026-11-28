namespace Altong.Client.Data.Models;

/// <summary>
/// SQLite focus_sessions 테이블에 매핑되는 집중 모드 세션 엔티티.
/// 뽀모도로/집중 모드의 시작, 종료, 차단된 알림 통계를 관리합니다.
/// </summary>
public record FocusSessionRecord(
    string SessionId,
    DateTime StartedAt,
    DateTime? EndedAt = null,
    int TargetDurationMinutes = 25,
    int BlockedCount = 0,
    bool IsCompleted = false);

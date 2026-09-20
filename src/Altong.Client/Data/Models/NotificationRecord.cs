namespace Altong.Client.Data.Models;

/// <summary>
/// SQLite notifications 테이블에 매핑되는 통합 알림 엔티티.
/// 수신된 알림 원본(RawNotification) 및 AI 실시간 필터 판단 결과(FilterResult)를 모두 포함합니다.
/// </summary>
public record NotificationRecord(
    string Id,
    string AppName,
    string? Sender,
    string Title,
    string Body,
    DateTime ReceivedAt,
    bool? IsPassed = null,
    int? UrgencyScore = null,
    int? RelevanceScore = null,
    string? Category = null,
    string? AiSummaryReason = null,
    string? SessionId = null,
    DateTime? CreatedAt = null);

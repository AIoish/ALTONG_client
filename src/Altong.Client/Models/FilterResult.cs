namespace Altong.Client.Models;

/// <summary>
/// 실시간 AI 필터 엔진이 알림과 현재 작업 맥락을 분석하여 도출한 평가 결과.
/// README §7.3 Data Contract 준수.
/// </summary>
public record FilterResult(
    string NotificationId,
    bool IsPassed,
    int UrgencyScore,
    int RelevanceScore,
    string Category,
    string? AiSummaryReason = null);

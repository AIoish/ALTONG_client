namespace Altong.Client.Models;

/// <summary>
/// 실시간 AI 필터 엔진이 알림과 현재 작업 맥락을 분석하여 도출한 평가 결과.
/// README §7.3 Data Contract 준수.
/// UrgencyScore는 1~5, RelevanceScore는 모델이 학습한 관련도 단계(filtering-v3부터 1~3, 3이 "지금 작업과 관련 있음")다.
/// 0은 평가하지 못했다는 뜻이다. filtering-v3 이전에 저장된 기록의 관련도는 1~5다.
/// </summary>
public record FilterResult(
    string NotificationId,
    bool IsPassed,
    int UrgencyScore,
    int RelevanceScore,
    string Category,
    string? AiSummaryReason = null);

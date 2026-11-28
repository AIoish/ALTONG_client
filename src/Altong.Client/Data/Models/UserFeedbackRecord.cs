namespace Altong.Client.Data.Models;

/// <summary>
/// SQLite user_feedbacks 테이블에 매핑되는 사용자 피드백 엔티티.
/// 대시보드에서 알림 선별에 대해 사용자가 평가한 피드백(적절, 오분류 등)을 보관합니다.
/// </summary>
public record UserFeedbackRecord(
    long Id,
    string NotificationId,
    string FeedbackType,
    DateTime CreatedAt);

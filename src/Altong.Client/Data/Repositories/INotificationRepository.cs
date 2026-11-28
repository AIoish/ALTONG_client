using Altong.Client.Data.Models;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// notifications 테이블 데이터 접근 계약.
/// </summary>
public interface INotificationRepository
{
    Task InsertAsync(NotificationRecord notification);
    Task UpdateFilterResultAsync(string id, bool isPassed, int urgencyScore, int relevanceScore, string category, string? reason);
    Task<NotificationRecord?> GetByIdAsync(string id);
    Task<IReadOnlyList<NotificationRecord>> GetBlockedNotificationsAsync(string? sessionId = null, int limit = 50);

    /// <summary>집중 세션의 알림을 통과·차단 구분 없이 최신순으로 가져온다 (요약 문맥용).</summary>
    Task<IReadOnlyList<NotificationRecord>> GetSessionNotificationsAsync(string sessionId, int limit = 200);
    Task<IReadOnlyList<NotificationRecord>> GetRecentNotificationsAsync(int limit = 50);
}

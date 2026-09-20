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
    Task<IReadOnlyList<NotificationRecord>> GetRecentNotificationsAsync(int limit = 50);
}

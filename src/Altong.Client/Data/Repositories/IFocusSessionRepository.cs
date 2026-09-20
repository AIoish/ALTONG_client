using Altong.Client.Data.Models;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// focus_sessions 테이블 데이터 접근 계약.
/// </summary>
public interface IFocusSessionRepository
{
    Task StartSessionAsync(FocusSessionRecord session);
    Task EndSessionAsync(string sessionId, DateTime endedAt, bool isCompleted);
    Task IncrementBlockedCountAsync(string sessionId);
    Task<FocusSessionRecord?> GetByIdAsync(string sessionId);
    Task<FocusSessionRecord?> GetActiveSessionAsync();
}

using Altong.Client.Data.Models;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// window_sessions 테이블 데이터 접근 계약 (TIL 업무 일지 원천 데이터).
/// </summary>
public interface IWindowSessionRepository
{
    Task InsertAsync(WindowSessionRecord session);
    Task<IReadOnlyList<WindowSessionRecord>> GetSessionsByDateRangeAsync(DateTime fromUtc, DateTime toUtc);
    Task<int> GetTotalDwellTimeSecondsAsync(DateTime fromUtc, DateTime toUtc);
    Task<IReadOnlyDictionary<string, int>> GetAppDwellTimeSummaryAsync(DateTime fromUtc, DateTime toUtc);
}

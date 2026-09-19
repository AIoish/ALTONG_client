namespace Altong.Client.Models;

/// <summary>
/// 사용자의 현재 작업 맥락 (활성 창 및 최근 작업 흐름 정보).
/// README §7.2 Data Contract 기반 확장.
/// </summary>
/// <param name="ActiveProcess">현재 활성 프로세스 이름 (예: Code.exe, chrome.exe)</param>
/// <param name="WindowTitle">현재 활성 창 제목</param>
/// <param name="LastUpdated">맥락이 캡처/갱신된 시각 (UTC)</param>
/// <param name="DurationSeconds">현재 창에 머무른 지속 시간 (초 단위, 5초 룰 통과 기준)</param>
/// <param name="RecentProcesses">최근 1~2분간 함께 교차 사용된 상위 프로세스 목록 (작업 세트 / 콤비 앱)</param>
public record CurrentContext(
    string ActiveProcess,
    string WindowTitle,
    DateTime LastUpdated,
    int DurationSeconds = 0,
    IReadOnlyList<string>? RecentProcesses = null)
{
    /// <summary>
    /// 초기화 시 또는 유효한 맥락이 없을 때 사용하는 기본값.
    /// </summary>
    public static CurrentContext Empty => new(
        ActiveProcess: string.Empty,
        WindowTitle: string.Empty,
        LastUpdated: DateTime.UtcNow);
}

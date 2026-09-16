namespace Altong.Client.Models;

/// <summary>
/// 사용자의 현재 작업 맥락 (활성 창 정보).
/// README §6.2 Data Contract.
/// </summary>
public record CurrentContext(
    string ActiveProcess,
    string WindowTitle,
    DateTime LastUpdated);

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 집중 모드 중 카카오톡 프로세스의 오디오 세션(알림음)을 제어(음소거/복원)하는 인터페이스.
/// </summary>
public interface IKakaoAudioOperator : IDisposable
{
    /// <summary>
    /// 현재 카카오톡 음소거 상태 여부
    /// </summary>
    bool IsMuted { get; }

    /// <summary>
    /// 카카오톡 프로세스를 Windows 볼륨 믹서에서 음소거(Mute)합니다.
    /// </summary>
    void Mute();

    /// <summary>
    /// 카카오톡 프로세스의 음소거를 해제(Unmute)하여 원래 볼륨으로 복원합니다.
    /// </summary>
    void Unmute();
}

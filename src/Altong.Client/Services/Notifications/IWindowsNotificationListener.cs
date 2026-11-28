using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// Windows 알림 센터로부터 들어오는 시스템 토스트 및 앱 알림을 수신하는 리스너 계약.
/// </summary>
public interface IWindowsNotificationListener : IDisposable
{
    /// <summary>
    /// 새로운 알림이 인입 및 파싱되었을 때 발행되는 이벤트.
    /// </summary>
    event EventHandler<RawNotification>? NotificationReceived;

    /// <summary>
    /// 알림 리스너가 현재 가동 중인지 여부.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// OS 알림 접근 권한을 확인하고 리스너 수신을 시작합니다.
    /// </summary>
    /// <returns>권한이 허용되어 정상적으로 수신을 시작했으면 true, 거부/실패 시 false.</returns>
    Task<bool> StartAsync();

    /// <summary>
    /// 알림 수신을 중지합니다.
    /// </summary>
    void Stop();
}

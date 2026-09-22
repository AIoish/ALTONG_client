using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 단위 테스트 및 CI 환경에서 실제 WinRT API 없이 동작하는 모의 알림 리스너.
/// </summary>
public sealed class FakeNotificationListener : IWindowsNotificationListener
{
    private bool _isRunning;

    public event EventHandler<RawNotification>? NotificationReceived;

    public bool IsRunning => _isRunning;

    public Task<bool> StartAsync()
    {
        _isRunning = true;
        return Task.FromResult(true);
    }

    public void Stop()
    {
        _isRunning = false;
    }

    /// <summary>
    /// 테스트에서 가상의 알림 수신 이벤트를 강제로 발생시킵니다.
    /// </summary>
    public void EmitNotification(RawNotification notification)
    {
        if (!_isRunning)
        {
            return;
        }

        NotificationReceived?.Invoke(this, notification);
    }

    public void Dispose()
    {
        Stop();
    }
}

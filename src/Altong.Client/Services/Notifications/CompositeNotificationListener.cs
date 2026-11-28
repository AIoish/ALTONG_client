using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 여러 개의 알림 리스너(Windows RT 표준 토스트 리스너, 카카오톡 전용 인터셉터 등)를
/// 단일 파이프라인 계약(IWindowsNotificationListener)으로 묶어주는 복합 리스너.
/// </summary>
public sealed class CompositeNotificationListener : IWindowsNotificationListener
{
    private readonly IReadOnlyList<IWindowsNotificationListener> _listeners;
    private readonly object _syncRoot = new();
    private bool _isDisposed;

    public event EventHandler<RawNotification>? NotificationReceived;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _listeners.Any(l => l.IsRunning);
            }
        }
    }

    public CompositeNotificationListener(params IWindowsNotificationListener[] listeners)
        : this((IEnumerable<IWindowsNotificationListener>)listeners)
    {
    }

    public CompositeNotificationListener(IEnumerable<IWindowsNotificationListener> listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        _listeners = listeners.ToList();

        foreach (var listener in _listeners)
        {
            listener.NotificationReceived += OnChildNotificationReceived;
        }
    }

    private void OnChildNotificationReceived(object? sender, RawNotification notification)
    {
        NotificationReceived?.Invoke(this, notification);
    }

    public async Task<bool> StartAsync()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        var startTasks = _listeners.Select(l => l.StartAsync()).ToList();
        var results = await Task.WhenAll(startTasks).ConfigureAwait(false);

        // 등록된 리스너 중 하나라도 성공적으로 시작되었으면 파이프라인 가동으로 인정
        return results.Any(success => success);
    }

    public void Stop()
    {
        foreach (var listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[CompositeListener] 리스너 중지 중 오류: {ex.Message}", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        foreach (var listener in _listeners)
        {
            try
            {
                listener.NotificationReceived -= OnChildNotificationReceived;
                listener.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[CompositeListener] 리스너 해제 중 오류: {ex.Message}", ex);
            }
        }
    }
}

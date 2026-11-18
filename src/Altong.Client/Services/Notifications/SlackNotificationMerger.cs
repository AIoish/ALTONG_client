using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>Prefer API DMs over matching Windows toasts. Other Slack alerts still flow through; matching is best effort.</summary>
internal sealed class SlackNotificationMerger : IWindowsNotificationListener, IPendingNotificationListener
{
    private readonly IWindowsNotificationListener _standard;
    private readonly IWindowsNotificationListener _slack;
    private readonly TimeSpan _toastDelay;
    private readonly object _sync = new();
    private readonly List<Recent> _recent = [];
    private CancellationTokenSource? _stop;
    private bool _disposed;
    public event EventHandler<RawNotification>? NotificationReceived;
    public event EventHandler<PendingNotification>? NotificationPending;
    public bool IsRunning => _standard.IsRunning || _slack.IsRunning;

    internal SlackNotificationMerger(IWindowsNotificationListener standard, IWindowsNotificationListener slack,
        TimeSpan? toastDelay = null)
    {
        _standard = standard;
        _slack = slack;
        _toastDelay = toastDelay ?? TimeSpan.FromSeconds(2);
        standard.NotificationReceived += OnStandard;
        slack.NotificationReceived += OnSlack;
        if (slack is IPendingNotificationListener pending)
            pending.NotificationPending += OnSlackPending;
    }

    public async Task<bool> StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stop is not null) return IsRunning;
            _stop = new();
        }
        var results = await Task.WhenAll(StartChildAsync(_standard), StartChildAsync(_slack)).ConfigureAwait(false);
        if (!results.Any(result => result)) Stop();
        return results.Any(result => result);
    }

    private static async Task<bool> StartChildAsync(IWindowsNotificationListener listener)
    {
        try { return await listener.StartAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Slack] 수집기 시작 실패 ({ex.GetType().Name})");
            return false;
        }
    }

    private void OnStandard(object? sender, RawNotification notification) => Route(notification, fromApi: false);
    private void OnSlack(object? sender, RawNotification notification) => Route(notification, fromApi: true);
    private void OnSlackPending(object? sender, PendingNotification pending) =>
        Route(pending.Notification, fromApi: true, pending.PrepareAsync);

    private void Route(RawNotification notification, bool fromApi, Func<Task<RawNotification?>>? prepare = null)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_stop is null || _stop.IsCancellationRequested) return;
            token = _stop.Token;
        }
        var pending = new PendingNotification(notification,
            () => PrepareForwardAsync(notification, fromApi, prepare, token));
        if (NotificationPending is { } handler) handler(this, pending);
        else _ = ForwardPreparedAsync(pending, token);
    }

    private async Task ForwardPreparedAsync(PendingNotification pending, CancellationToken token)
    {
        try
        {
            var notification = await pending.PrepareAsync().ConfigureAwait(false);
            if (notification is not null && !token.IsCancellationRequested)
                NotificationReceived?.Invoke(this, notification);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { AppLogger.Warn($"[Slack] 알림 전달 실패 ({ex.GetType().Name})"); }
    }

    private async Task<RawNotification?> PrepareForwardAsync(RawNotification notification, bool fromApi,
        Func<Task<RawNotification?>>? prepare, CancellationToken token)
    {
        try
        {
            if (prepare is not null)
            {
                var prepared = await prepare().WaitAsync(token).ConfigureAwait(false);
                if (prepared is null) return null;
                notification = prepared;
            }
            bool isSlack = notification.AppName.Equals("Slack", StringComparison.OrdinalIgnoreCase)
                || notification.AppName.Equals("Slack.exe", StringComparison.OrdinalIgnoreCase);
            if (isSlack && !fromApi && _slack.IsRunning)
                await Task.Delay(_toastDelay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (isSlack)
            {
                lock (_sync)
                {
                    var now = DateTime.UtcNow;
                    _recent.RemoveAll(entry => now - entry.SeenAt > TimeSpan.FromSeconds(20));
                    var match = _recent.FirstOrDefault(entry => !entry.Matched && entry.FromApi != fromApi
                        && AreSameMessage(fromApi ? notification : entry.Notification,
                            fromApi ? entry.Notification : notification));
                    if (match is not null) { match.Matched = true; return null; }
                    if (_recent.Count >= 512) _recent.RemoveAt(0);
                    _recent.Add(new(notification, fromApi, now));
                }
            }
            return token.IsCancellationRequested ? null : notification;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
    }

    internal static bool AreSameMessage(RawNotification api, RawNotification toast)
    {
        if (string.IsNullOrWhiteSpace(api.Body) || api.Body != toast.Body
            || (api.Timestamp - toast.Timestamp).Duration() > TimeSpan.FromSeconds(10)) return false;
        // Do not compare body alone: generic text such as "네" is common across different conversations.
        // A channel toast may have exactly the same sender and body as a DM. Only coalesce
        // recognizable DM titles, never sender equality by itself when another title is present.
        return api.Sender.Length > 0 && (api.Sender == toast.Title
            || toast.Title == api.Sender + " (DM)"
            || string.IsNullOrWhiteSpace(toast.Title) && api.Sender == toast.Sender);
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stop?.Cancel();
            _stop?.Dispose();
            _stop = null;
            _recent.Clear();
        }
        StopChild(_standard);
        StopChild(_slack);
    }

    private static void StopChild(IWindowsNotificationListener listener)
    {
        try { listener.Stop(); }
        catch (Exception ex) { AppLogger.Warn($"[Slack] 수집기 종료 실패 ({ex.GetType().Name})"); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _standard.NotificationReceived -= OnStandard;
        _slack.NotificationReceived -= OnSlack;
        if (_slack is IPendingNotificationListener pending)
            pending.NotificationPending -= OnSlackPending;
        DisposeChild(_standard);
        DisposeChild(_slack);
    }

    private static void DisposeChild(IWindowsNotificationListener listener)
    {
        try { listener.Dispose(); }
        catch (Exception ex) { AppLogger.Warn($"[Slack] 수집기 해제 실패 ({ex.GetType().Name})"); }
    }

    private sealed class Recent(RawNotification notification, bool fromApi, DateTime seenAt)
    {
        internal RawNotification Notification { get; } = notification;
        internal bool FromApi { get; } = fromApi;
        internal DateTime SeenAt { get; } = seenAt;
        internal bool Matched { get; set; }
    }
}

using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>Internal admission hook: capture session/context before asynchronous source preparation.</summary>
internal interface IPendingNotificationListener
{
    event EventHandler<PendingNotification>? NotificationPending;
}

internal sealed class PendingNotification(RawNotification notification, Func<Task<RawNotification?>> prepare)
{
    private readonly Lazy<Task<RawNotification?>> _prepared = new(prepare);
    internal RawNotification Notification { get; } = notification;
    internal Task<RawNotification?> PrepareAsync() => _prepared.Value;
}

namespace Altong.Client.Services;

public enum WindowsNotificationModeKind
{
    Unknown,
    Unrestricted,
    PriorityOnly,
    AlarmsOnly,
    Unsupported,
    Error,
}

public sealed record WindowsNotificationModeState(
    WindowsNotificationModeKind Kind,
    string? Detail = null)
{
    public static WindowsNotificationModeState Unknown { get; } =
        new(WindowsNotificationModeKind.Unknown);

    public bool IsRestricted => Kind is
        WindowsNotificationModeKind.PriorityOnly or
        WindowsNotificationModeKind.AlarmsOnly;
}

public sealed class WindowsNotificationModeChangedEventArgs(
    WindowsNotificationModeState state) : EventArgs
{
    public WindowsNotificationModeState State { get; } = state;
}

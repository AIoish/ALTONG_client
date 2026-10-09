using Windows.UI.Notifications;

namespace Altong.Client.Services;

/// <summary>SHQueryUserNotificationState values from shellapi.h.</summary>
public enum WindowsUserNotificationState
{
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

public static class WindowsNotificationModeResolver
{
    public static WindowsNotificationModeState Resolve(ToastNotificationMode mode,
        WindowsUserNotificationState? userState, WindowsNotificationModeState confirmedState)
    {
        // Screen clipping temporarily reports AlarmsOnly while the shell reports Busy.
        // Keep other native notification states compatible with the existing observer.
        if (mode == ToastNotificationMode.AlarmsOnly &&
            (userState is null or WindowsUserNotificationState.Busy))
            return confirmedState;

        return mode switch
        {
            ToastNotificationMode.Unrestricted => new(WindowsNotificationModeKind.Unrestricted),
            ToastNotificationMode.PriorityOnly => new(WindowsNotificationModeKind.PriorityOnly),
            ToastNotificationMode.AlarmsOnly => new(WindowsNotificationModeKind.AlarmsOnly),
            _ => new(WindowsNotificationModeKind.Unknown, "Windows가 알 수 없는 알림 모드를 반환했습니다."),
        };
    }
}

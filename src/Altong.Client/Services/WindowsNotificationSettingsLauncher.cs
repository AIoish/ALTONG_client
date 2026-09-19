using System.Diagnostics;

namespace Altong.Client.Services;

public sealed class WindowsNotificationSettingsLauncher : IWindowsNotificationSettingsLauncher
{
    private const string NotificationsSettingsUri = "ms-settings:notifications";

    public bool TryOpen(out string? errorMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(NotificationsSettingsUri)
            {
                UseShellExecute = true,
            });

            errorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            errorMessage =
                $"Windows 알림 설정을 열지 못했습니다. ({exception.GetType().Name})";
            return false;
        }
    }
}

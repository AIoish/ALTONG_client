namespace Altong.Client.Services;

public interface IWindowsNotificationSettingsLauncher
{
    bool TryOpen(out string? errorMessage);
}

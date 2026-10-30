using System.IO;
using System.Text.Json;

namespace Altong.Client.Services.NotificationControl;

public enum FocusControlBackend { Altong, Windows }

public sealed record NotificationSender(string Id, string Name, string? ExclusionReason)
{
    public bool IsEligible => ExclusionReason is null;
}

public sealed record SenderState(bool Enabled, bool ShowInActionCenter, int ShowBanner, string SoundFile);
public sealed record RegistrySetting(bool Exists, int Kind, string? Json);
public sealed record SenderBackup(string Id, SenderState Original, RegistrySetting BannerRegistry, RegistrySetting SoundRegistry);

// The narrow interface deliberately has no setters for Enabled, ActionCenter or global settings.
public interface INotificationSenderSettings
{
    string? CompatibilityError { get; }
    IReadOnlyList<NotificationSender> Enumerate();
    bool IsRegistered(string id);
    SenderState Read(string id);
    RegistrySetting ReadRegistry(string id, bool sound);
    void SetBanner(string id, int value);
    void SetSound(string id, string value);
    void RestoreRegistry(string id, bool sound, RegistrySetting value);
}

public static class NotificationSenderPolicy
{
    public static string? ExclusionReason(string id, bool validApp, bool enabled, bool inCenter)
    {
        if (id.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("windows.immersivecontrolpanel", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Microsoft.YourPhone_", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("NotifyIconGeneratedAumid_", StringComparison.OrdinalIgnoreCase))
            return "Windows 시스템·보안 알림 (보호됨)";
        if (!validApp) return "앱 정보 확인 불가·삭제된 앱";
        if (!enabled) return "Windows에서 알림이 이미 꺼져 있음";
        // The listener needs notifications retained in the center; never enable it on the user's behalf.
        if (!inCenter) return "Windows에서 알림센터 보관이 꺼져 있음";
        return null;
    }
}

public sealed class NotificationControlPreferences
{
    public FocusControlBackend Backend { get; set; } = FocusControlBackend.Altong;
    public HashSet<string> ExcludedSenders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altong");
    public static string FilePath => Path.Combine(DirectoryPath, "notification-control.json");

    public static NotificationControlPreferences Load(string? path = null)
    {
        path ??= FilePath;
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        var value = JsonSerializer.Deserialize<NotificationControlPreferences>(json)
            ?? throw new InvalidDataException("알림 제어 설정이 비어 있습니다.");
        if (!Enum.IsDefined(value.Backend) || value.ExcludedSenders is null
            || value.ExcludedSenders.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("알림 제어 설정 형식을 확인해 주세요.");
        value.ExcludedSenders = new(value.ExcludedSenders, StringComparer.OrdinalIgnoreCase);
        return value;
    }

    public void Save(string? path = null) => DurableJson.Write(path ?? FilePath, this);
}

internal static class DurableJson
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

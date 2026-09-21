using System.IO;
using System.Text.Json;

namespace Altong.Client.Services;

public sealed record FocusTimerSettings(int FocusMinutes = 25, int BreakMinutes = 5)
{
    public bool IsValid => FocusMinutes is >= 1 and <= 180 && BreakMinutes is >= 1 and <= 60;
}

/// <summary>로컬 타이머 설정. 저장이 완료된 뒤에만 메모리의 설정을 갱신한다.</summary>
public sealed class FocusSettingsStore
{
    private readonly string _path;
    public FocusTimerSettings Current { get; private set; } = new();
    public string? LoadWarning { get; private set; }

    public FocusSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "focus-settings.json");
        try
        {
            var settings = JsonSerializer.Deserialize<FocusTimerSettings>(File.ReadAllText(_path));
            if (settings is not { IsValid: true })
                throw new JsonException("Invalid timer settings.");
            Current = settings;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // First run uses the defaults without writing a file.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "저장된 설정을 읽지 못해 기본값을 표시합니다. 시간을 확인한 뒤 다시 저장해 주세요.";
        }
    }

    public void Save(FocusTimerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid)
            throw new ArgumentOutOfRangeException(nameof(settings));

        string path = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, path, overwrite: true);
            Current = settings;
            LoadWarning = null;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

using System.IO;
using System.Text.Json;
using Altong.Client.Services.NotificationControl;

namespace Altong.Client.Services.Focus;

public sealed record AutoDndSettings(bool Enabled = true);

/// <summary>
/// AutoDND(작업 감지 후 집중 제안) 켜짐 설정. 기본값은 켜짐이며, 집중 타이머 설정과 따로 저장해
/// 체크 즉시 반영된다. 저장이 끝난 뒤에만 메모리 값을 바꾼다.
/// </summary>
public sealed class AutoDndSettingsStore
{
    private readonly string _path;
    public AutoDndSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public AutoDndSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "auto-dnd-settings.json");
        try
        {
            Current = JsonSerializer.Deserialize<AutoDndSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            or IOException or UnauthorizedAccessException or JsonException)
        {
            // 처음 실행했거나 파일이 손상되면 기본값(켜짐)을 쓴다.
        }
    }

    public void Save(AutoDndSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // 임시 파일에 디스크까지 기록한 뒤 바꿔치기한다 (중간에 꺼져도 설정 파일이 깨지지 않음).
        DurableJson.Write(Path.GetFullPath(_path), settings);

        if (Current == settings) return;
        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

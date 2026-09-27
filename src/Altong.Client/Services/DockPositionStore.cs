using System.IO;
using System.Text.Json;

namespace Altong.Client.Services;

public sealed record DockPositionSettings(double VerticalRatio)
{
    public bool IsValid => double.IsFinite(VerticalRatio) && VerticalRatio is >= 0 and <= 1;
}

/// <summary>작업 영역 내 미니바의 세로 위치를 비율로 저장한다.</summary>
public sealed class DockPositionStore
{
    private readonly string _path;

    public double? VerticalRatio { get; private set; }

    public DockPositionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "dock-position.json");

        try
        {
            var settings = JsonSerializer.Deserialize<DockPositionSettings>(File.ReadAllText(_path));
            if (settings is not { IsValid: true })
                throw new JsonException("Invalid dock position.");
            VerticalRatio = settings.VerticalRatio;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 첫 실행은 기존 기본 위치를 사용한다.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 잘못된 설정 파일은 보존하고 기본 위치를 사용한다.
        }
    }

    public void Save(double verticalRatio)
    {
        var settings = new DockPositionSettings(verticalRatio);
        if (!settings.IsValid)
            throw new ArgumentOutOfRangeException(nameof(verticalRatio));

        string path = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, path, overwrite: true);
            VerticalRatio = verticalRatio;
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

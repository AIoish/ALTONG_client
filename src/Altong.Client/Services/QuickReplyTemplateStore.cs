using System.IO;
using System.Text.Json;

namespace Altong.Client.Services;

/// <summary>리포트의 답장 문구 목록. 저장이 끝난 뒤에만 메모리 값을 바꾼다.</summary>
public sealed class QuickReplyTemplateStore
{
    public const int MaxTemplates = 5;
    public const int MaxLength = 100;

    public static IReadOnlyList<string> Defaults { get; } =
    [
        "확인했습니다. 곧 답변드릴게요.",
        "지금 확인했어요. 바로 연락드릴게요.",
        "집중하느라 늦게 봤어요. 조금 뒤에 답장드릴게요."
    ];

    private readonly string? _path;
    public IReadOnlyList<string> Current { get; private set; } = Defaults;
    public string? LoadWarning { get; private set; }

    // No path means an in-memory store with the defaults, used by tests and standalone hosts.
    public QuickReplyTemplateStore(string? path = null)
    {
        _path = path;
        if (_path is null) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path));
            if (loaded is null || Normalize(loaded) is not { Count: > 0 } templates || templates.Count != loaded.Length)
                throw new JsonException("Invalid reply templates.");
            Current = templates;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // First run uses the defaults without writing a file.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "저장된 답장 문구를 읽지 못해 기본 문구를 표시합니다. 확인한 뒤 다시 저장해 주세요.";
        }
    }

    public static QuickReplyTemplateStore OpenDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altong", "quick-reply-templates.json"));

    /// <summary>앞뒤 공백·빈 줄·중복을 정리한다. 개수나 길이 제한을 넘으면 null을 돌려준다.</summary>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string?> lines)
    {
        var templates = lines.Select(line => line?.Trim() ?? "").Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        return templates.Length is > 0 and <= MaxTemplates && templates.All(line => line.Length <= MaxLength) ? templates : null;
    }

    public void Save(IEnumerable<string?> lines)
    {
        var templates = Normalize(lines)
            ?? throw new ArgumentException($"답장 문구는 1–{MaxTemplates}개, 문구마다 {MaxLength}자 이하로 입력해 주세요.");
        if (_path is not null)
        {
            string path = Path.GetFullPath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(templates));
                File.Move(temporaryPath, path, overwrite: true);
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
        Current = templates;
        LoadWarning = null;
    }
}

using System.IO;
using System.Text;

namespace Altong.Client.Services;

/// <summary>
/// 콘솔 출력과 로컬 파일 로그(%LOCALAPPDATA%\Altong\logs\altong.log)를 동시에 기록하는 공용 로거.
/// 파일이 MaxFileBytes를 넘으면 altong.log → altong.1.log → altong.2.log로 밀어내고 가장 오래된 것을 지운다.
/// 그래서 로그는 최대 MaxFileBytes × (KeptFiles + 1) 만큼만 남는다. 로그는 이 PC에만 저장하고 밖으로 보내지 않는다.
/// </summary>
public static class AppLogger
{
    public const long MaxFileBytes = 5L * 1024 * 1024;
    public const int KeptFiles = 2;

    private static readonly object SyncLock = new();
    private static readonly string LogPath;
    private static long _currentBytes = -1;

    static AppLogger()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong",
            "logs");

        try
        {
            Directory.CreateDirectory(logDir);
            LogPath = Path.Combine(logDir, "altong.log");
        }
        catch
        {
            LogPath = "altong.log";
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Log("ERROR", ex != null ? $"{message} - {ex.GetType().Name}: {ex.Message}" : message);

    private static void Log(string level, string message)
    {
        var consoleLine = $"[{DateTime.Now:HH:mm:ss}] [{level,-5}] {message}";
        var fileLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";

        Console.WriteLine(consoleLine);

        try
        {
            lock (SyncLock)
            {
                Append(LogPath, fileLine);
            }
        }
        catch
        {
            // 로깅 실패가 앱 동작을 방해하지 않음
        }
    }

    private static void Append(string path, string line)
    {
        if (_currentBytes < 0) _currentBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (_currentBytes >= MaxFileBytes)
        {
            Rotate(path, KeptFiles);
            _currentBytes = 0;
        }
        File.AppendAllText(path, line);
        _currentBytes += Encoding.UTF8.GetByteCount(line);
    }

    /// <summary>altong.log → altong.1.log → … → altong.{kept}.log로 밀어내고 가장 오래된 파일은 지운다.</summary>
    internal static void Rotate(string path, int kept)
    {
        string Numbered(int index) => Path.Combine(
            Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}.{index}{Path.GetExtension(path)}");

        if (File.Exists(Numbered(kept))) File.Delete(Numbered(kept));
        for (int index = kept - 1; index >= 1; index--)
            if (File.Exists(Numbered(index))) File.Move(Numbered(index), Numbered(index + 1));
        if (File.Exists(path)) File.Move(path, Numbered(1));
    }
}

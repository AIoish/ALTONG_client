using System.IO;

namespace Altong.Client.Services;

/// <summary>
/// 콘솔 출력과 로컬 파일 로그(%LOCALAPPDATA%\Altong\logs\altong.log)를 동시에 기록하는 공용 로거
/// </summary>
public static class AppLogger
{
    private static readonly object SyncLock = new();
    private static readonly string LogDir;
    private static readonly string LogPath;

    static AppLogger()
    {
        LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong",
            "logs");
        
        try
        {
            Directory.CreateDirectory(LogDir);
            LogPath = Path.Combine(LogDir, "altong.log");
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
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        
        // 1. 콘솔 출력
        Console.WriteLine(line);

        // 2. 파일 출력 (비동기 잠금)
        try
        {
            lock (SyncLock)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 로깅 실패가 앱 동작을 방해하지 않음
        }
    }
}

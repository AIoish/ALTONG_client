using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Altong.Client.Services.Notifications;

/// <summary>Personal development credentials, encrypted for the current Windows user by setup-slack.ps1.</summary>
internal sealed class SlackConnectionSettings
{
    public string AppToken { get; }
    public string UserToken { get; }
    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altong", "slack-tokens.dat");

    public SlackConnectionSettings(string appToken, string userToken)
    {
        if (string.IsNullOrWhiteSpace(appToken) || !appToken.StartsWith("xapp-", StringComparison.Ordinal)
            || appToken.Any(char.IsWhiteSpace)
            || string.IsNullOrWhiteSpace(userToken) || !userToken.StartsWith("xoxp-", StringComparison.Ordinal)
            || userToken.Any(char.IsWhiteSpace))
            throw new ArgumentException("Slack 앱 토큰과 사용자 토큰 형식을 확인해 주세요.");
        AppToken = appToken;
        UserToken = userToken;
    }

    internal static bool IsEnabled => Environment.GetEnvironmentVariable("ALTONG_ENABLE_SLACK") != "0";

    internal static SlackConnectionSettings? TryLoad(string? path = null)
    {
        path ??= FilePath;
        if (!IsEnabled || !File.Exists(path))
            return null;
        try { return Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or ArgumentException)
        {
            AppLogger.Warn($"[Slack] 토큰 파일을 읽지 못했습니다. setup-slack.ps1로 다시 설정해 주세요. ({ex.GetType().Name})");
            return null;
        }
    }

    internal static SlackConnectionSettings Load(string path)
    {
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("Slack 토큰 파일 크기가 잘못되었습니다.");
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<SlackConnectionSettings>(plain)
                ?? throw new JsonException("Slack 토큰 파일이 비어 있습니다.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public override string ToString() => "SlackConnectionSettings (credentials hidden)";
}

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Altong.Slack.Contracts;

namespace Altong.Client.Services.Notifications;

internal sealed class SlackRelaySessionStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altong", "slack-relay-session.dat");
    private sealed record SavedSession(string Server, SlackDeviceSession Session)
    { public override string ToString() => "Encrypted Altong Slack session"; }

    internal SlackRelaySessionStore SelectSingleConnection(Uri? server)
    {
        string previousDirectory = _path + ".workspaces";
        if (server is null || !Directory.Exists(previousDirectory)) return this;

        try
        {
            string selectionPath = Path.Combine(previousDirectory, $"single-{SlackConnectionProof.Hash(server.AbsoluteUri)}.txt");
            if (File.Exists(selectionPath))
            {
                string selection = File.ReadAllText(selectionPath);
                if (selection == "primary") return this;
                if (Guid.TryParseExact(selection, "N", out Guid id))
                    return new(Path.Combine(previousDirectory, id.ToString("N") + ".dat"));
            }

            // Keep the selection after disconnect so a different saved account cannot replace it at startup.
            if (Load(server) is null)
            {
                foreach (string path in Directory.EnumerateFiles(previousDirectory, "*.dat").Order(StringComparer.Ordinal))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!Guid.TryParseExact(name, "N", out _)) continue;
                    var previousStore = new SlackRelaySessionStore(path);
                    if (previousStore.Load(server) is null) continue;
                    File.WriteAllText(selectionPath, name);
                    return previousStore;
                }
            }

            File.WriteAllText(selectionPath, "primary");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"[Slack] 기존 기기 연결을 선택하지 못했습니다. ({ex.GetType().Name})");
        }
        return this;
    }

    internal SlackDeviceSession? Load(Uri server)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            if (new FileInfo(_path).Length > 32768) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<SavedSession>(plain);
                return saved?.Server == server.AbsoluteUri && saved.Session is { } session && SlackConnectionProof.IsValid(session.Token) ? session : null;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        { AppLogger.Warn($"[Slack] 저장된 기기 연결을 읽지 못했습니다. ({ex.GetType().Name})"); return null; }
    }
    internal void Save(Uri server, SlackDeviceSession session)
    {
        if (!SlackConnectionProof.IsValid(session.Token)) throw new InvalidDataException("Invalid device session.");
        string full = Path.GetFullPath(_path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new SavedSession(server.AbsoluteUri, session));
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, full, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void Clear() => File.Delete(_path);
}

internal static class SlackRelayConfiguration
{
    internal static Uri? ServerUrl()
    {
        string? configured = Environment.GetEnvironmentVariable("ALTONG_SLACK_SERVER_URL");
        if (string.IsNullOrWhiteSpace(configured))
        {
            string path = Path.Combine(AppContext.BaseDirectory, "slack-service.json");
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                configured = doc.RootElement.GetProperty("ServerUrl").GetString();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        }
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.AbsolutePath != "/") return null;
        if (uri.Scheme == "https") return uri;
#if DEBUG
        if (uri.IsLoopback && uri.Scheme == "http") return uri;
#endif
        return null;
    }
}

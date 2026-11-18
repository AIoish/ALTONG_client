namespace Altong.Slack.Server;

public sealed class SlackServerOptions
{
    public required Uri PublicBaseUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string AppId { get; init; }
    public required string AppToken { get; init; }
    public required string DataDirectory { get; init; }
    public required byte[] EncryptionKey { get; init; }
    public required TimeSpan Retention { get; init; }
    public string[] AllowedTeams { get; init; } = [];
    public Uri CallbackUrl => new(PublicBaseUrl, "oauth/slack/callback");

    public static SlackServerOptions FromEnvironment(bool development)
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Required server setting is missing: {name}");
        var url = new Uri(Required("ALTONG_SLACK_PUBLIC_URL"));
        if ((!url.IsAbsoluteUri || url.Scheme != "https") && !(development && url.IsLoopback && url.Scheme == "http")
            || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0 || url.UserInfo.Length > 0)
            throw new InvalidOperationException("ALTONG_SLACK_PUBLIC_URL must be an HTTPS origin (HTTP loopback in Development only).");
        var key = Convert.FromBase64String(Required("ALTONG_SLACK_ENCRYPTION_KEY"));
        if (key.Length != 32) throw new InvalidOperationException("ALTONG_SLACK_ENCRYPTION_KEY must encode 32 bytes.");
        if (!int.TryParse(Required("ALTONG_SLACK_RETENTION_HOURS"), out int hours) || hours is < 1 or > 168)
            throw new InvalidOperationException("ALTONG_SLACK_RETENTION_HOURS must be between 1 and 168.");
        return new()
        {
            PublicBaseUrl = url, ClientId = Required("ALTONG_SLACK_CLIENT_ID"),
            ClientSecret = Required("ALTONG_SLACK_CLIENT_SECRET"), AppId = Required("ALTONG_SLACK_APP_ID"),
            AppToken = Required("ALTONG_SLACK_APP_TOKEN"), EncryptionKey = key,
            DataDirectory = Path.GetFullPath(Required("ALTONG_SLACK_DATA_DIR")), Retention = TimeSpan.FromHours(hours),
            AllowedTeams = (Environment.GetEnvironmentVariable("ALTONG_SLACK_ALLOWED_TEAMS") ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        };
    }
    public override string ToString() => "Slack server configuration (credentials hidden)";
}

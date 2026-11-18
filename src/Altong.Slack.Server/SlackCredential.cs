namespace Altong.Slack.Server;

public sealed record SlackAccount(string TeamId, string UserId, string Account, string Workspace);
public sealed class SlackCredential
{
    public required SlackAccount Account { get; init; }
    public required string AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public required string[] Scopes { get; init; }
    public override string ToString() => "Slack user credential (tokens hidden)";
}
public sealed record SlackInstallation(string UserId, SlackCredential Credential);
public sealed record SlackDevice(string Id, string UserId);
public sealed record SlackInboxItem(string Id, string Payload, DateTimeOffset ReceivedAt);

public sealed class SlackServiceException(string code, TimeSpan? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan RetryAfter { get; } = retryAfter ?? TimeSpan.FromSeconds(5);
    public bool IsTerminal => Code is "invalid_auth" or "token_revoked" or "account_inactive" or "missing_scope"
        or "not_authed" or "invalid_refresh_token";
}

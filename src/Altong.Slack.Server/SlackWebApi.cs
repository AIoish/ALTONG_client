using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Altong.Slack.Server;

public sealed class SlackWebApi : IDisposable
{
    public static readonly string[] RequiredScopes = ["im:history", "im:read", "users:read"];
    private readonly SlackServerOptions _options;
    private readonly EncryptedSlackStore _store;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new();

    public SlackWebApi(SlackServerOptions options, EncryptedSlackStore store, HttpClient? http = null, TimeProvider? time = null)
    {
        _options = options; _store = store; _time = time ?? TimeProvider.System;
        _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = TimeSpan.FromSeconds(20);
    }
    public string AuthorizationUrl(string state) => "https://slack.com/oauth/v2/authorize?client_id=" + Uri.EscapeDataString(_options.ClientId)
        + "&user_scope=" + Uri.EscapeDataString(string.Join(',', RequiredScopes))
        + "&redirect_uri=" + Uri.EscapeDataString(_options.CallbackUrl.AbsoluteUri) + "&state=" + Uri.EscapeDataString(state);

    public async Task<SlackCredential> ExchangeAsync(string code, CancellationToken token)
    {
        using var result = await CallAsync("oauth.v2.access", null,
            [new("client_id", _options.ClientId), new("client_secret", _options.ClientSecret), new("code", code),
             new("redirect_uri", _options.CallbackUrl.AbsoluteUri)], token);
        var root = result.RootElement;
        if (Text(root, "app_id") != _options.AppId || !root.TryGetProperty("authed_user", out var user))
            throw new SlackServiceException("invalid_oauth_response");
        string access = Text(user, "access_token");
        var scopes = Text(user, "scope").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (Text(user, "token_type") != "user" || access.Length == 0 || !RequiredScopes.All(scopes.Contains))
            throw new SlackServiceException("missing_scope");
        using var auth = await CallAsync("auth.test", access, [], token);
        var identity = auth.RootElement;
        string team = Text(identity, "team_id"), id = Text(identity, "user_id");
        if (team.Length == 0 || id.Length == 0 || Text(identity, "bot_id").Length > 0 || id != Text(user, "id")
            || !root.TryGetProperty("team", out var oauthTeam) || team != Text(oauthTeam, "id")
            || _options.AllowedTeams.Length > 0 && !_options.AllowedTeams.Contains(team, StringComparer.Ordinal))
            throw new SlackServiceException("account_not_allowed");
        return new()
        {
            Account = new(team, id, Text(identity, "user"), Text(identity, "team")), AccessToken = access,
            RefreshToken = NullIfEmpty(Text(user, "refresh_token")), ExpiresAt = Expiry(user), Scopes = scopes
        };
    }

    // Serialize refresh, API calls and disconnect per user; no refresh token can be spent twice.
    private SemaphoreSlim AccountLock(SlackAccount account) => _accountLocks.GetOrAdd(account.TeamId + ":" + account.UserId, _ => new(1, 1));

    public async Task CompletePairingAsync(string id, SlackCredential credential, CancellationToken token)
    {
        var gate = AccountLock(credential.Account); await gate.WaitAsync(token);
        try { _store.CompletePairing(id, credential); }
        finally { gate.Release(); }
    }

    public async Task<T> ForUserAsync<T>(string userId, Func<SlackCredential, CancellationToken, Task<T>> action, CancellationToken token)
    {
        var account = _store.Installation(userId)?.Credential.Account ?? throw new SlackServiceException("token_revoked");
        var gate = AccountLock(account);
        await gate.WaitAsync(token);
        try
        {
            var credential = _store.Installation(userId)?.Credential ?? throw new SlackServiceException("token_revoked");
            if (credential.ExpiresAt is { } expiry && expiry <= _time.GetUtcNow().AddMinutes(2))
            {
                if (credential.RefreshToken is not { Length: > 0 } refresh) throw new SlackServiceException("invalid_refresh_token");
                using var result = await CallAsync("oauth.v2.access", null,
                    [new("client_id", _options.ClientId), new("client_secret", _options.ClientSecret), new("grant_type", "refresh_token"), new("refresh_token", refresh)], token);
                var root = result.RootElement;
                string access = Text(root, "access_token"), next = Text(root, "refresh_token");
                if (Text(root, "token_type") != "user" || access.Length == 0 || next.Length == 0 || Expiry(root) is not { } expires)
                    throw new SlackServiceException("invalid_refresh_token");
                credential.AccessToken = access; credential.RefreshToken = next; credential.ExpiresAt = expires;
                _store.UpdateCredential(userId, credential);
            }
            return await action(credential, token);
        }
        catch (SlackServiceException ex) when (ex.IsTerminal) { _store.Deactivate(userId); throw; }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(SlackDevice device, CancellationToken token)
    {
        var account = _store.Installation(device.UserId)?.Credential.Account;
        if (account is null) { _store.Disconnect(device); return; }
        var gate = AccountLock(account); await gate.WaitAsync(token);
        try
        {
            // Disable routing before contacting Slack, even if the external revoke call fails.
            if (_store.Disconnect(device) is { } last)
            {
                try { using var result = await CallAsync("auth.revoke", last.Credential.AccessToken, [], token); }
                catch (Exception ex) when (ex is SlackServiceException or HttpRequestException or OperationCanceledException) { }
            }
        }
        finally { gate.Release(); }
    }

    public Task<bool> VerifyCurrentGrantAsync(string userId, CancellationToken token) => ForUserAsync(userId, async (credential, ct) =>
    {
        using var result = await CallAsync("auth.test", credential.AccessToken, [], ct);
        if (Text(result.RootElement, "team_id") != credential.Account.TeamId || Text(result.RootElement, "user_id") != credential.Account.UserId)
            throw new SlackServiceException("invalid_auth");
        return true;
    }, token);

    public async Task<(string Team, string User)[]> AuthorizationsAsync(JsonElement payload, CancellationToken token)
    {
        var result = new HashSet<(string Team, string User)>();
        string context = Text(payload, "event_context");
        if (context.Length == 0)
        {
            if (payload.TryGetProperty("authorizations", out var direct) && direct.ValueKind == JsonValueKind.Array) Add(direct);
            return result.ToArray(); // No fallback to all workspace users.
        }
        string cursor = ""; var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var page = await CallAsync("apps.event.authorizations.list", _options.AppToken,
                [new("event_context", context), new("cursor", cursor), new("limit", "200")], token);
            if (!page.RootElement.TryGetProperty("authorizations", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new SlackServiceException("invalid_response");
            Add(list);
            cursor = page.RootElement.TryGetProperty("response_metadata", out var metadata) ? Text(metadata, "next_cursor") : "";
            if (cursor.Length > 0 && !cursors.Add(cursor)) throw new SlackServiceException("invalid_response");
        } while (cursor.Length > 0);
        return result.ToArray();

        void Add(JsonElement list)
        {
            foreach (var entry in list.EnumerateArray())
                if (entry.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.False
                    && Text(entry, "team_id") is { Length: > 0 } team && Text(entry, "user_id") is { Length: > 0 } user)
                    result.Add((team, user));
        }
    }

    public async Task<bool> CanReadDmAsync(SlackCredential credential, string channel, string sender, CancellationToken token)
    {
        if (sender == credential.Account.UserId) return false;
        using var info = await CallAsync("conversations.info", credential.AccessToken, [new("channel", channel)], token);
        return info.RootElement.TryGetProperty("channel", out var c) && c.TryGetProperty("is_im", out var im) && im.ValueKind == JsonValueKind.True;
    }
    public async Task<string> UserNameAsync(SlackCredential credential, string sender, CancellationToken token)
    {
        using var result = await CallAsync("users.info", credential.AccessToken, [new("user", sender)], token);
        if (!result.RootElement.TryGetProperty("user", out var user)) return sender;
        if (user.TryGetProperty("profile", out var profile))
            foreach (var key in new[] { "display_name", "real_name" }) if (Text(profile, key).Trim() is { Length: > 0 } name) return name;
        return Text(user, "name").Trim() is { Length: > 0 } fallback ? fallback : sender;
    }
    public async Task<Uri> OpenSocketAsync(CancellationToken token)
    {
        using var result = await CallAsync("apps.connections.open", _options.AppToken, [], token);
        if (!Uri.TryCreate(Text(result.RootElement, "url"), UriKind.Absolute, out var uri) || uri.Scheme != "wss"
            || !uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length > 0)
            throw new SlackServiceException("invalid_socket_url");
        return uri;
    }

    private DateTimeOffset? Expiry(JsonElement root) => root.TryGetProperty("expires_in", out var expires)
        && expires.TryGetInt32(out int seconds) && seconds > 0 ? _time.GetUtcNow().AddSeconds(seconds) : null;
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    public static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task<JsonDocument> CallAsync(string method, string? bearer, KeyValuePair<string, string>[] fields, CancellationToken token)
    {
        string key = method + ":" + (bearer is null ? "oauth" : Altong.Slack.Contracts.SlackConnectionProof.Hash(bearer));
        if (_cooldowns.TryGetValue(key, out var until) && until > _time.GetUtcNow())
            throw new SlackServiceException("ratelimited", until - _time.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await _http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - _time.GetUtcNow() ?? TimeSpan.FromSeconds(30);
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            _cooldowns[key] = _time.GetUtcNow() + delay;
            throw new SlackServiceException("ratelimited", delay);
        }
        if (!response.IsSuccessStatusCode) throw new SlackServiceException("slack_unavailable");
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); }
        catch (JsonException) { throw new SlackServiceException("invalid_response"); }
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return doc;
        string code = Text(root, "error"); doc.Dispose();
        throw new SlackServiceException(code is "invalid_auth" or "token_revoked" or "account_inactive" or "missing_scope" or "not_authed"
            or "invalid_refresh_token" or "invalid_code" or "channel_not_found" or "not_in_channel" or "not_allowed_token_type" ? code : "slack_api_error");
    }
    public void Dispose() => _http.Dispose();
}

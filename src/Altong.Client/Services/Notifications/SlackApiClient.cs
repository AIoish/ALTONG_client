using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Altong.Client.Services.Notifications;

internal sealed record SlackIdentity(string TeamId, string UserId, string TeamName = "", string UserName = "");

internal interface ISlackApiClient : IDisposable
{
    Task<SlackIdentity> AuthenticateAsync(CancellationToken token);
    Task<Uri> OpenConnectionAsync(CancellationToken token);
    Task<bool> IsPersonalDmAsync(string channel, CancellationToken token);
    Task<string> GetUserNameAsync(string user, CancellationToken token);
}

internal sealed class SlackApiException(string code, TimeSpan? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IsTerminal => Code is "invalid_auth" or "token_revoked" or "account_inactive" or "not_authed"
        or "missing_scope" or "not_allowed_token_type" or "user_token_required";
}

/// <summary>Never includes response bodies, tokens, or signed WebSocket URLs in errors.</summary>
internal sealed class SlackApiClient : ISlackApiClient
{
    private readonly SlackConnectionSettings _settings;
    private readonly HttpClient _http;
    private readonly object _rateSync = new();
    private readonly Dictionary<string, DateTimeOffset> _methodCooldowns = new(StringComparer.Ordinal);

    internal SlackApiClient(SlackConnectionSettings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<SlackIdentity> AuthenticateAsync(CancellationToken token)
    {
        using var doc = await CallAsync("auth.test", _settings.UserToken, [], token).ConfigureAwait(false);
        var root = doc.RootElement;
        if (SlackDmEvent.Text(root, "bot_id").Length > 0 || SlackDmEvent.Text(root, "user_id").Length == 0
            || SlackDmEvent.Text(root, "team_id").Length == 0)
            throw new SlackApiException("user_token_required");
        return new(SlackDmEvent.Text(root, "team_id"), SlackDmEvent.Text(root, "user_id"),
            SlackDmEvent.Text(root, "team"), SlackDmEvent.Text(root, "user"));
    }

    public async Task<Uri> OpenConnectionAsync(CancellationToken token)
    {
        using var doc = await CallAsync("apps.connections.open", _settings.AppToken, [], token).ConfigureAwait(false);
        if (!Uri.TryCreate(SlackDmEvent.Text(doc.RootElement, "url"), UriKind.Absolute, out var uri)
            || uri.Scheme != "wss" || !uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new SlackApiException("invalid_socket_url");
        return uri;
    }

    public async Task<bool> IsPersonalDmAsync(string channel, CancellationToken token)
    {
        // Events may be delivered for other installations of the same Slack app. Verify visibility
        // with THIS user's token before passing a message to AI, instead of trusting authorizations[0].
        using var doc = await CallAsync("conversations.info", _settings.UserToken,
            [new("channel", channel)], token).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("channel", out var info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("is_im", out var isIm) && isIm.ValueKind == JsonValueKind.True;
    }

    public async Task<string> GetUserNameAsync(string user, CancellationToken token)
    {
        using var doc = await CallAsync("users.info", _settings.UserToken, [new("user", user)], token).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("user", out var info) || info.ValueKind != JsonValueKind.Object) return user;
        if (info.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "display_name", "real_name" })
                if (SlackDmEvent.Text(profile, key).Trim() is { Length: > 0 } name) return name;
        }
        return SlackDmEvent.Text(info, "real_name").Trim() is { Length: > 0 } realName ? realName
            : SlackDmEvent.Text(info, "name").Trim() is { Length: > 0 } fallback ? fallback : user;
    }

    private async Task<JsonDocument> CallAsync(string method, string bearer,
        KeyValuePair<string, string>[] fields, CancellationToken token)
    {
        await WaitForCooldownAsync(method, token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw RateLimited(method, response);
        }
        if (!response.IsSuccessStatusCode) throw new SlackApiException($"http_{(int)response.StatusCode}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)); }
        catch (JsonException) { throw new SlackApiException("invalid_response"); }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new SlackApiException("invalid_response");
        }
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            // Only known codes are surfaced; an unexpected API response must not become a data log.
            string code = SlackDmEvent.Text(doc.RootElement, "error");
            doc.Dispose();
            if (code == "ratelimited") throw RateLimited(method, response);
            throw new SlackApiException(code is "invalid_auth" or "token_revoked" or "account_inactive"
                or "not_authed" or "missing_scope" or "not_allowed_token_type" or "channel_not_found"
                or "not_in_channel" or "ratelimited" or "link_disabled" ? code : "api_error");
        }
        return doc;
    }

    private SlackApiException RateLimited(string method, HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        lock (_rateSync) _methodCooldowns[method] = DateTimeOffset.UtcNow.Add(delay);
        return new SlackApiException("ratelimited", delay);
    }

    private async Task WaitForCooldownAsync(string method, CancellationToken token)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_rateSync) remaining = _methodCooldowns.GetValueOrDefault(method) - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();
}

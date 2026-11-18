using System.Globalization;
using System.Text.Json;
using Altong.Slack.Contracts;

namespace Altong.Slack.Server;

public sealed class SlackEventRouter(EncryptedSlackStore store, SlackWebApi api, SlackServerOptions options)
{
    public async Task RouteAsync(SlackInboxItem item, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(item.Payload);
        var payload = doc.RootElement;
        if (SlackWebApi.Text(payload, "api_app_id") != options.AppId || SlackWebApi.Text(payload, "type") != "event_callback"
            || !payload.TryGetProperty("event", out var message)) return;
        string teamId = SlackWebApi.Text(payload, "team_id");
        if (SlackWebApi.Text(message, "type") == "app_uninstalled")
        { if (teamId.Length > 0) store.DeactivateTeam(teamId); return; }
        if (SlackWebApi.Text(message, "type") == "tokens_revoked")
        {
            if (message.TryGetProperty("tokens", out var tokens) && tokens.TryGetProperty("oauth", out var users) && users.ValueKind == JsonValueKind.Array)
                foreach (var user in users.EnumerateArray())
                    if (user.ValueKind == JsonValueKind.String && store.InstallationFor(teamId, user.GetString()!) is { } installation)
                    {
                        // A delayed revocation event for an old grant must not erase a fresh OAuth grant.
                        try { await api.VerifyCurrentGrantAsync(installation.UserId, token); }
                        catch (SlackServiceException ex) when (ex.IsTerminal) { }
                    }
            return;
        }
        if (SlackWebApi.Text(message, "type") != "message" || SlackWebApi.Text(message, "channel_type") != "im"
            || SlackWebApi.Text(message, "subtype") is not ("" or "file_share" or "me_message")
            || SlackWebApi.Text(message, "bot_id").Length > 0
            || message.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) return;
        string channel = SlackWebApi.Text(message, "channel"), sender = SlackWebApi.Text(message, "user"), body = SlackWebApi.Text(message, "text");
        if (channel.Length == 0 || sender.Length == 0 || string.IsNullOrWhiteSpace(body) || body.Length > 262144
            || !decimal.TryParse(SlackWebApi.Text(message, "ts"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds)
            || seconds < 0) return;
        DateTime timestamp;
        try { timestamp = DateTime.UnixEpoch.AddTicks(checked((long)(seconds * TimeSpan.TicksPerSecond))); }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException) { return; }
        SlackServiceException? deferred = null;
        foreach (var (team, user) in await api.AuthorizationsAsync(payload, token))
        {
            var installation = store.InstallationFor(team, user);
            if (installation is null || sender == user) continue;
            try
            {
                await api.ForUserAsync(installation.UserId, async (credential, ct) =>
                {
                    if (!await api.CanReadDmAsync(credential, channel, sender, ct)) return false;
                    string name;
                    try { name = await api.UserNameAsync(credential, sender, ct); }
                    catch (SlackServiceException ex) when (!ex.IsTerminal || ex.Code == "missing_scope") { name = sender; }
                    string canonical = seconds.ToString("0.000000######################", CultureInfo.InvariantCulture);
                    var notification = new SlackRelayMessage($"slack:{team}:{channel}:{canonical}", name,
                        body.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal), timestamp);
                    store.Deliver(installation.UserId, notification, item.ReceivedAt);
                    return true;
                }, token);
            }
            catch (SlackServiceException ex) when (ex.IsTerminal || ex.Code is "channel_not_found" or "not_in_channel") { }
            catch (SlackServiceException ex) { if (deferred is null || ex.RetryAfter > deferred.RetryAfter) deferred = ex; }
            catch (HttpRequestException) { deferred ??= new SlackServiceException("slack_unavailable"); }
        }
        if (deferred is not null) throw deferred;
    }
}

public sealed class SlackRouterWorker(EncryptedSlackStore store, SlackEventRouter router, ILogger<SlackRouterWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var item = store.NextInbox();
            if (item is null) { await Task.Delay(500, stoppingToken); continue; }
            try { await router.RouteAsync(item, stoppingToken); store.CompleteInbox(item.Id); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (SlackServiceException ex)
            {
                logger.LogWarning("Slack routing deferred ({Code}).", ex.Code);
                store.DeferInbox(item.Id, ex.RetryAfter);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Slack routing deferred ({Type}).", ex.GetType().Name);
                store.DeferInbox(item.Id, TimeSpan.FromSeconds(5));
            }
        }
    }
}

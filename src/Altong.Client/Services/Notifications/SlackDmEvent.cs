using System.Globalization;
using System.Text.Json;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

internal sealed record SlackDmEvent(string Id, string Channel, string User, string Body, DateTime Timestamp)
{
    internal static SlackDmEvent? Parse(JsonElement payload, SlackIdentity identity)
    {
        if (Text(payload, "type") != "event_callback" || !IsAuthorizedWorkspace(payload, identity)
            || !payload.TryGetProperty("event", out var message) || Text(message, "type") != "message"
            || Text(message, "channel_type") != "im") return null;
        string subtype = Text(message, "subtype");
        if (subtype is not ("" or "file_share" or "me_message") || Text(message, "bot_id").Length > 0
            || message.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
            return null;
        string channel = Text(message, "channel"), user = Text(message, "user"), body = Text(message, "text");
        string ts = Text(message, "ts");
        if (channel.Length == 0 || user.Length == 0 || user == identity.UserId || string.IsNullOrWhiteSpace(body)
            || !decimal.TryParse(ts, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0) return null;
        try
        {
            var timestamp = DateTime.UnixEpoch.AddTicks(checked((long)(seconds * TimeSpan.TicksPerSecond)));
            // Stable across retries, reconnects and event IDs; do not turn edited messages into new alerts.
            string canonicalTs = seconds.ToString("0.000000######################", CultureInfo.InvariantCulture);
            return new($"slack:{identity.TeamId}:{channel}:{canonicalTs}", channel, user,
                DecodeText(body), timestamp);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException) { return null; }
    }

    // Slack DMs have no subject. Keep it empty instead of inventing text for the card or AI input.
    internal RawNotification ToNotification(string name) => new(Id, "Slack", name, string.Empty, Body, Timestamp);

    // Slack escapes only these three entities. HTML-decoding everything also changes literal
    // snippets such as "&#65;" and "&copy;" that the sender did not ask us to interpret.
    private static string DecodeText(string text) => text.Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    private static bool IsAuthorizedWorkspace(JsonElement payload, SlackIdentity identity)
    {
        if (Text(payload, "team_id") == identity.TeamId) return true;
        // Slack Connect can use a different outer team. Accept an explicit authorization for this
        // user, then still verify the DM itself with this user's token before exposing its text.
        return payload.TryGetProperty("authorizations", out var authorizations)
            && authorizations.ValueKind == JsonValueKind.Array
            && authorizations.EnumerateArray().Any(authorization => Text(authorization, "team_id") == identity.TeamId
                && Text(authorization, "user_id") == identity.UserId
                && authorization.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.False);
    }

    internal static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

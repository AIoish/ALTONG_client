using System.Security.Cryptography;
using System.Text;

namespace Altong.Slack.Contracts;

public sealed record BeginSlackConnection(string Challenge);
public sealed record SlackConnectionAttempt(string Id, string AuthorizationUrl, DateTimeOffset ExpiresAt);
public sealed record SlackPairingStatus(string Status, SlackDeviceSession? Session = null);
public sealed record SlackDeviceSession(string Token, string UserId, string Account, string Workspace)
{
    public override string ToString() => "Slack device session (credential hidden)";
}
public sealed record SlackSessionStatus(string UserId, string Account, string Workspace, bool ReceiverConnected)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DeliveryMode { get; init; }
}
public sealed record SlackRelayMessage(string Id, string Sender, string Body, DateTime Timestamp);
public sealed record SlackDelivery(string Id, SlackRelayMessage Message);
public sealed record SlackDeliveryBatch(bool ReceiverConnected, SlackDelivery[] Messages)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? PollAfterSeconds { get; init; }
}
public sealed record AcknowledgeSlackDeliveries(string[] Ids);

public static class SlackConnectionProof
{
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static bool IsValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool Matches(string secret, string expectedHash) => IsValid(secret) && IsValid(expectedHash)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(secret)), Convert.FromHexString(expectedHash));
}

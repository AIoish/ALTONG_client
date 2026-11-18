using System.Net.WebSockets;
using System.Text.Json;

namespace Altong.Slack.Server;

public sealed class SlackReceiverHealth
{
    private int _connected;
    public bool Connected { get => Volatile.Read(ref _connected) == 1; set => Volatile.Write(ref _connected, value ? 1 : 0); }
}

/// <summary>Exactly one central receiver per deployment. Never connects a Slack socket from a tester PC.</summary>
public sealed class SlackSocketWorker(SlackWebApi api, EncryptedSlackStore store, SlackReceiverHealth health, SlackServerOptions options,
    ILogger<SlackSocketWorker> logger) : BackgroundService
{
    private readonly FileStream _lease = new(Path.Combine(options.DataDirectory, "receiver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))));
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                var uri = await api.OpenSocketAsync(stoppingToken);
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                { connect.CancelAfter(TimeSpan.FromSeconds(20)); await socket.ConnectAsync(uri, connect.Token); }
                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    using var frame = await ReadAsync(socket, stoppingToken);
                    if (frame is null) break;
                    var root = frame.RootElement; string type = SlackWebApi.Text(root, "type");
                    if (type == "hello")
                    {
                        health.Connected = true; connectedAt = DateTimeOffset.UtcNow;
                        if (root.TryGetProperty("num_connections", out var connections) && connections.TryGetInt32(out int count) && count > 1)
                            logger.LogWarning("Other Slack app sockets are active. Disable local development receivers to avoid split delivery.");
                    }
                    if (type == "disconnect") break;
                    string envelope = SlackWebApi.Text(root, "envelope_id");
                    if (envelope.Length == 0) continue;
                    if (type == "events_api" && root.TryGetProperty("payload", out var payload))
                    {
                        string eventId = SlackWebApi.Text(payload, "event_id");
                        if (eventId.Length > 0) store.Enqueue(eventId, payload.GetRawText());
                    }
                    // ACK only after the event is durably encrypted. Profile lookup and AI never block it.
                    await socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelope })),
                        WebSocketMessageType.Text, true, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (SlackServiceException ex) { delay = ex.RetryAfter; logger.LogWarning("Slack receiver retry ({Code}).", ex.Code); }
            catch (Exception ex) { logger.LogWarning("Slack receiver retry ({Type}).", ex.GetType().Name); }
            finally { health.Connected = false; }
            if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30)) failures = 0;
            await Task.Delay(delay, stoppingToken);
        }
    }
    public override void Dispose() { base.Dispose(); _lease.Dispose(); }

    internal static async Task<JsonDocument?> ReadAsync(WebSocket socket, CancellationToken token)
    {
        byte[] bytes = new byte[8192]; using var buffer = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text || buffer.Length + received.Count > 1024 * 1024)
                throw new InvalidDataException("Invalid Slack frame.");
            buffer.Write(bytes, 0, received.Count);
        } while (!received.EndOfMessage);
        return JsonDocument.Parse(buffer.ToArray());
    }
}

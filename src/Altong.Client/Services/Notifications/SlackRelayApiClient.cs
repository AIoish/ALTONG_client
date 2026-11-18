using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Altong.Slack.Contracts;

namespace Altong.Client.Services.Notifications;

internal sealed class SlackRelayException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

/// <summary>Only Altong device credentials leave the PC; Slack credentials stay on the server.</summary>
internal sealed class SlackRelayApiClient : IDisposable
{
    private readonly Uri _server;
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker _socketTransport = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    internal SlackRelayApiClient(Uri server, HttpClient? http = null)
    {
        _server = server;
        _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = TimeSpan.FromSeconds(30);
    }
    internal Task<SlackConnectionAttempt> BeginAsync(string challenge, CancellationToken token) =>
        CallAsync<SlackConnectionAttempt>(HttpMethod.Post, "v1/slack/connections", null, new BeginSlackConnection(challenge), null, token);
    internal Task<SlackPairingStatus> PairingAsync(string id, string proof, CancellationToken token) =>
        CallAsync<SlackPairingStatus>(HttpMethod.Get, "v1/slack/connections/" + Uri.EscapeDataString(id), null, null, proof, token);
    internal Task CancelPairingAsync(string id, string proof, CancellationToken token) =>
        CallAsync<object>(HttpMethod.Delete, "v1/slack/connections/" + Uri.EscapeDataString(id), null, null, proof, token);
    internal Task<SlackSessionStatus> StatusAsync(string session, CancellationToken token) =>
        CallAsync<SlackSessionStatus>(HttpMethod.Get, "v1/slack/session", session, null, null, token);
    internal Task<SlackDeliveryBatch> PollAsync(string session, CancellationToken token) =>
        CallAsync<SlackDeliveryBatch>(HttpMethod.Get, "v1/slack/messages", session, null, null, token);
    internal Task AckAsync(string session, string[] ids, CancellationToken token) =>
        CallAsync<object>(HttpMethod.Post, "v1/slack/messages/ack", session, new AcknowledgeSlackDeliveries(ids), null, token);
    internal Task DisconnectAsync(string session, CancellationToken token) =>
        CallAsync<object>(HttpMethod.Delete, "v1/slack/session", session, null, null, token);

    internal async Task<ClientWebSocket> ConnectStreamAsync(string session, CancellationToken token)
    {
        var endpoint = new UriBuilder(new Uri(_server, "v1/slack/stream"))
        {
            Scheme = _server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        };
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + session);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(endpoint.Uri, _socketTransport, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            if (status == HttpStatusCode.Unauthorized) throw new SlackRelayException("session_expired");
            throw;
        }
    }

    internal static async Task WaitForMessagesAsync(ClientWebSocket socket, CancellationToken token)
    {
        Memory<byte> signal = new byte[1];
        var result = await socket.ReceiveAsync(signal, token).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            if ((int?)socket.CloseStatus == 4401) throw new SlackRelayException("session_expired");
            throw new SlackRelayException("service_unavailable");
        }
        if (result.MessageType != WebSocketMessageType.Binary || !result.EndOfMessage || result.Count != 1 || signal.Span[0] != 1)
            throw new SlackRelayException("invalid_response");
    }

    private async Task<T> CallAsync<T>(HttpMethod method, string path, string? session, object? body, string? proof, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, new Uri(_server, path));
        if (path == "v1/slack/messages") request.Headers.Add("X-Altong-Short-Poll", "1");
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        if (proof is not null) request.Headers.Add("X-Altong-Pairing-Proof", proof);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SlackRelayException("session_expired");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new SlackRelayException("pairing_expired");
        if (!response.IsSuccessStatusCode) throw new SlackRelayException("service_unavailable");
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, token).ConfigureAwait(false);
        try { return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token).ConfigureAwait(false) ?? throw new SlackRelayException("invalid_response"); }
        catch (JsonException) { throw new SlackRelayException("invalid_response"); }
    }
    public void Dispose()
    {
        _http.Dispose();
        _socketTransport.Dispose();
    }
}

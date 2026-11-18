using System.IO;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>Opt-in, single-workspace personal DM reader. Socket acknowledgements never wait for AI.</summary>
internal sealed class SlackSocketModeListener : IWindowsNotificationListener, IPendingNotificationListener
{
    private readonly ISlackApiClient _api;
    private readonly Func<WebSocket> _createSocket;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _queueCapacity;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _stop;
    private WebSocket? _socket;
    private Task _runTask = Task.CompletedTask;
    private bool _disposed;
    private bool _running;
    private SlackIdentity? _identity;
    internal SlackIdentity? ConnectedIdentity { get { lock (_sync) return _identity; } }
    internal string Status { get; private set; } = "Slack 연결 대기";
    internal Task Completion { get { lock (_sync) return _runTask; } }
    public bool IsRunning { get { lock (_sync) return _running; } }
    public event EventHandler<RawNotification>? NotificationReceived;
    public event EventHandler<PendingNotification>? NotificationPending;

    internal SlackSocketModeListener(SlackConnectionSettings settings)
        : this(new SlackApiClient(settings), CreateClientSocket) { }

    internal SlackSocketModeListener(ISlackApiClient api, Func<WebSocket> createSocket,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int queueCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _api = api;
        _createSocket = createSocket;
        _delay = delay ?? Task.Delay;
        _queueCapacity = queueCapacity;
    }

    private static WebSocket CreateClientSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        return socket;
    }

    public async Task<bool> StartAsync()
    {
        while (true)
        {
            Task previous;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_stop is not null && !_stop.IsCancellationRequested) return true;
                if (_runTask.IsCompleted)
                {
                    var stop = new CancellationTokenSource();
                    _stop = stop;
                    _runTask = Task.Run(() => RunAsync(stop));
                    return true;
                }
                previous = _runTask;
            }
            // Stop/Start in quick succession must wait for the old receiver to release its socket.
            await previous.ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stop?.Cancel();
            try { _socket?.Abort(); }
            catch (ObjectDisposedException) { } // A reconnect may be disposing this socket concurrently.
            _running = false;
        }
    }

    private async Task RunAsync(CancellationTokenSource stop)
    {
        var token = stop.Token;
        var queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var processing = ProcessAsync(queue.Reader, token);
        int failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan retry = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));
                long? connectedAt = null;
                try
                {
                    var identity = await _api.AuthenticateAsync(token).ConfigureAwait(false);
                    lock (_sync) _identity = identity;
                    var uri = await _api.OpenConnectionAsync(token).ConfigureAwait(false);
                    using var socket = _createSocket();
                    lock (_sync)
                    {
                        token.ThrowIfCancellationRequested();
                        _socket = socket;
                    }
                    // WebSocket is abstract for deterministic offline transport tests.
                    if (socket is ClientWebSocket client)
                    {
                        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connecting.CancelAfter(TimeSpan.FromSeconds(20));
                        await client.ConnectAsync(uri, connecting.Token).ConfigureAwait(false);
                    }
                    else if (socket.State != WebSocketState.Open)
                        throw new InvalidOperationException("Socket factory must provide an open test socket.");
                    token.ThrowIfCancellationRequested();
                    lock (_sync) _running = true;
                    connectedAt = Stopwatch.GetTimestamp();
                    SetStatus($"개인 DM 수신 연결됨 · 워크스페이스 {identity.TeamId}");
                    await ReceiveAsync(socket, identity, queue.Writer, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (SlackApiException ex)
                {
                    SetStatus($"연결 확인 필요 ({ex.Code})");
                    if (ex.IsTerminal || ex.Code == "link_disabled") break;
                    retry = ex.RetryAfter ?? retry;
                }
                catch (Exception ex)
                {
                    // Http/WebSocket exceptions can contain the signed connection URL. Log type only.
                    SetStatus($"연결 재시도 ({ex.GetType().Name})");
                }
                finally
                {
                    lock (_sync) { _socket = null; _running = false; }
                    // A short-lived connection is still a failure. Otherwise repeated immediate
                    // disconnects reset the backoff and keep hammering apps.connections.open.
                    if (connectedAt is { } started && Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                    { failures = 0; retry = TimeSpan.FromSeconds(1); }
                }
                failures++;
                await _delay(retry, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            queue.Writer.TryComplete();
            stop.Cancel();
            try { await processing.ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { SetStatus($"수신 처리 종료 ({ex.GetType().Name})"); }
            lock (_sync)
            {
                _running = false;
                if (ReferenceEquals(_stop, stop)) _stop = null;
                stop.Dispose();
            }
        }
    }

    private async Task ReceiveAsync(WebSocket socket, SlackIdentity identity, ChannelWriter<Delivery> queue,
        CancellationToken token)
    {
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var message = await ReadMessageAsync(socket, token).ConfigureAwait(false);
            if (message is null) return;
            var root = message.RootElement;
            string type = SlackDmEvent.Text(root, "type");
            if (type == "hello" && root.TryGetProperty("num_connections", out var connections)
                && connections.TryGetInt32(out int count) && count > 1)
                AppLogger.Warn("[Slack] 동일 앱의 수신 연결이 여러 개입니다. 테스트 수신기와 알통을 함께 켜면 이벤트가 나뉘어 전달될 수 있습니다.");
            if (type == "disconnect")
            {
                if (SlackDmEvent.Text(root, "reason") == "link_disabled") throw new SlackApiException("link_disabled");
                return;
            }
            string envelope = SlackDmEvent.Text(root, "envelope_id");
            if (envelope.Length == 0) continue; // hello, etc.

            Delivery? delivery = null;
            if (type == "events_api" && root.TryGetProperty("payload", out var payload)
                && SlackDmEvent.Parse(payload, identity) is { } dm)
            {
                delivery = new(dm);
                if (!queue.TryWrite(delivery))
                    throw new SlackApiException("receive_queue_full");
            }
            bool sent = false;
            try
            {
                byte[] ack = JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelope });
                await socket.SendAsync(new ArraySegment<byte>(ack), WebSocketMessageType.Text, true, token)
                    .ConfigureAwait(false);
                sent = true;
                if (delivery is not null)
                {
                    token.ThrowIfCancellationRequested();
                    var accepted = delivery;
                    var pending = new PendingNotification(accepted.Message.ToNotification(accepted.Message.User),
                        () => accepted.Prepared.Task);
                    if (NotificationPending is { } handler) handler(this, pending);
                    else _ = ForwardPreparedAsync(pending, token);
                }
            }
            // Publish the receipt before allowing profile/access I/O to start in the worker.
            finally { delivery?.Acknowledged.TrySetResult(sent); }
        }
    }

    private async Task ForwardPreparedAsync(PendingNotification pending, CancellationToken token)
    {
        try
        {
            var notification = await pending.PrepareAsync().WaitAsync(token).ConfigureAwait(false);
            if (notification is not null && !token.IsCancellationRequested)
                NotificationReceived?.Invoke(this, notification);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus($"알림 전달 실패 ({ex.GetType().Name})"); }
    }

    internal static async Task<JsonDocument?> ReadMessageAsync(WebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[8192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (frame.MessageType != WebSocketMessageType.Text || stream.Length + frame.Count > 1024 * 1024)
                throw new InvalidDataException("Unsupported Slack frame.");
            stream.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }

    private async Task ProcessAsync(ChannelReader<Delivery> queue, CancellationToken token)
    {
        try
        {
            await foreach (var delivery in queue.ReadAllAsync(token).ConfigureAwait(false))
            {
              try
              {
                if (!await delivery.Acknowledged.Task.WaitAsync(token).ConfigureAwait(false)) continue;
                var dm = delivery.Message;
                if (_seen.Contains(dm.Id)) continue;
                // Recheck every incoming message so revoking this user's access also takes effect
                // when another installation of the same app keeps the app-level socket alive.
                if (!await CanReadDmAsync(dm.Channel, token).ConfigureAwait(false)) continue;
                // Display names can change while connected. Do not retain names (or lookup failures)
                // for the lifetime of the listener; each new DM gets the current Slack profile name.
                string name;
                try { name = await _api.GetUserNameAsync(dm.User, token).WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (SlackApiException ex) when (ex.IsTerminal && ex.Code != "missing_scope") { throw; }
                catch { name = dm.User; } // Name permission failure must not discard the message.
                AppLogger.Info("[Slack API] DM 수신 · 사용자 접근 확인 완료 · 파이프라인 전달");
                _seen.Add(dm.Id);
                _seenOrder.Enqueue(dm.Id);
                if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
                delivery.Prepared.TrySetResult(dm.ToNotification(name));
              }
              catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
              catch (SlackApiException ex)
              {
                SetStatus($"DM 접근 확인 실패 ({ex.Code})");
                if (ex.IsTerminal) { Stop(); return; }
              }
              catch (Exception ex) { SetStatus($"DM 처리 실패 ({ex.GetType().Name})"); }
              finally { delivery.Prepared.TrySetResult(null); }
            }
        }
        finally { while (queue.TryRead(out var abandoned)) abandoned.Prepared.TrySetResult(null); }
    }

    private async Task<bool> CanReadDmAsync(string channel, CancellationToken token)
    {
        while (true)
        {
            TimeSpan delay = TimeSpan.FromSeconds(5);
            try { return await _api.IsPersonalDmAsync(channel, token).WaitAsync(token).ConfigureAwait(false); }
            catch (SlackApiException ex) when (ex.Code == "ratelimited" || ex.Code.StartsWith("http_5", StringComparison.Ordinal))
            {
                SetStatus($"DM 접근 확인 재시도 ({ex.Code})");
                delay = ex.RetryAfter ?? delay;
            }
            catch (System.Net.Http.HttpRequestException) { SetStatus("DM 접근 확인 재시도 (네트워크)"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { SetStatus("DM 접근 확인 재시도 (시간 초과)"); }
            await _delay(delay, token).ConfigureAwait(false);
        }
    }

    private void SetStatus(string message)
    {
        Status = message;
        AppLogger.Info("[Slack] " + message);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _ = _runTask.ContinueWith(_ => _api.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private sealed class Delivery(SlackDmEvent message)
    {
        internal SlackDmEvent Message { get; } = message;
        internal TaskCompletionSource<bool> Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RawNotification?> Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

using System.Diagnostics;
using System.Net.WebSockets;
using Altong.Client.Models;
using Altong.Slack.Contracts;

namespace Altong.Client.Services.Notifications;

internal interface ISlackNotificationConnection : ISlackConnectionController, IWindowsNotificationListener, IPendingNotificationListener;

internal static class SlackConnectionFactory
{
    internal static ISlackNotificationConnection Create() => Environment.GetEnvironmentVariable("ALTONG_SLACK_LOCAL_DEVELOPMENT") == "1"
        ? new SlackConnectionController() : new SlackRelayConnectionController(SlackRelayConfiguration.ServerUrl());
}

internal sealed class SlackRelayConnectionController : ISlackNotificationConnection
{
    private readonly Uri? _server;
    private readonly SlackRelayApiClient? _api;
    private readonly SlackRelaySessionStore _store;
    private readonly Action<Uri> _openBrowser;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private CancellationTokenSource? _stop;
    private Task _run = Task.CompletedTask;
    private SlackDeviceSession? _session;
    private string? _attemptId, _attemptProof;
    private SlackConnectionView _current = new(SlackConnectionPhase.NotConnected);
    private string? _feedback;
    private bool _disposed;
    private long _lifecycle;
    public bool UsesOAuth => true;
    public string? Feedback { get { lock (_sync) return _feedback; } }
    public SlackConnectionView Current { get { lock (_sync) return _current; } }
    public bool IsRunning => Current.Phase == SlackConnectionPhase.Connected;
    public event EventHandler<RawNotification>? NotificationReceived;
    public event EventHandler<PendingNotification>? NotificationPending;
    internal Task Completion { get { lock (_sync) return _run; } }

    internal SlackRelayConnectionController(Uri? server, SlackRelayApiClient? api = null, SlackRelaySessionStore? store = null,
        Action<Uri>? openBrowser = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _server = server; _api = api ?? (server is null ? null : new SlackRelayApiClient(server));
        _store = store ?? new SlackRelaySessionStore().SelectSingleConnection(server);
        _delay = delay ?? Task.Delay;
        _openBrowser = openBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
    }
    public Task<bool> StartAsync() => StartAsync(interactive: false);
    public Task<bool> ConnectAsync() => StartAsync(interactive: true);
    private async Task<bool> StartAsync(bool interactive)
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            Task previous; long lifecycle;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!SlackConnectionSettings.IsEnabled) { _feedback = "Slack 수신이 이 PC에서 비활성화되어 있습니다."; return false; }
                if (_server is null || _api is null) { _feedback = "Slack 연결 서비스가 아직 설정되지 않았어요. 배포 관리자에게 문의해 주세요."; return false; }
                if (_stop is { IsCancellationRequested: false }) return true;
                previous = _run;
                lifecycle = _lifecycle;
            }
            await previous.ConfigureAwait(false);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (lifecycle != _lifecycle) return false;
                _session ??= _store.Load(_server!);
                if (_session is null && !interactive) return false; // Never open a browser during startup.
                var stop = new CancellationTokenSource(); _stop = stop; _feedback = null;
                _current = new(SlackConnectionPhase.Connecting, _session?.Account ?? "", _session?.Workspace ?? "");
                _run = Task.Run(() => RunAsync(interactive, stop));
                return true;
            }
        }
        finally { _actions.Release(); }
    }

    private async Task RunAsync(bool interactive, CancellationTokenSource stop)
    {
        var token = stop.Token;
        ClientWebSocket? stream = null;
        try
        {
            if (_attemptId is not null) await CancelApprovalAsync().ConfigureAwait(false);
            if (_session is not null)
            {
                try { await SessionStatusAsync(_session.Token, token).ConfigureAwait(false); }
                catch (SlackRelayException ex) when (ex.Code == "session_expired") { _store.Clear(); _session = null; }
            }
            if (_session is null)
            {
                if (!interactive) throw new SlackRelayException("session_expired");
                string proof = SlackConnectionProof.NewSecret();
                var attempt = await _api!.BeginAsync(SlackConnectionProof.Hash(proof), token).ConfigureAwait(false);
                _attemptId = attempt.Id; _attemptProof = proof;
                if (!Uri.TryCreate(attempt.AuthorizationUrl, UriKind.Absolute, out var authorization)
                    || authorization.Scheme != "https" || authorization.Host != "slack.com" || authorization.AbsolutePath != "/oauth/v2/authorize"
                    || authorization.UserInfo.Length > 0) throw new SlackRelayException("invalid_response");
                var remaining = attempt.ExpiresAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(15)) throw new SlackRelayException("pairing_expired");
                using var approval = CancellationTokenSource.CreateLinkedTokenSource(token); approval.CancelAfter(remaining);
                SetView(SlackConnectionPhase.AwaitingApproval);
                token.ThrowIfCancellationRequested();
                _openBrowser(authorization);
                while (true)
                {
                    var result = await _api.PairingAsync(attempt.Id, proof, approval.Token).ConfigureAwait(false);
                    if (result.Status == "complete" && result.Session is { } session && SlackConnectionProof.IsValid(session.Token))
                    {
                        token.ThrowIfCancellationRequested();
                        _store.Save(_server!, session);
                        _session = session;
                        _attemptId = _attemptProof = null;
                        break;
                    }
                    if (result.Status == "denied") throw new SlackRelayException("approval_denied");
                    if (result.Status is not ("pending" or "authorizing")) throw new SlackRelayException("approval_failed");
                    await _delay(TimeSpan.FromSeconds(2), approval.Token).ConfigureAwait(false);
                }
            }
            var linked = _session ?? throw new SlackRelayException("invalid_response");
            var initial = await SessionStatusAsync(linked.Token, token).ConfigureAwait(false);
            if (initial.UserId != linked.UserId) throw new SlackRelayException("invalid_response");
            bool useStream = initial.DeliveryMode == "websocket";
            SetView(initial.ReceiverConnected && !useStream ? SlackConnectionPhase.Connected : SlackConnectionPhase.Connecting);
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (useStream)
                    {
                        if (stream is null)
                        {
                            stream = await _api!.ConnectStreamAsync(linked.Token, token).ConfigureAwait(false);
                            SetView(SlackConnectionPhase.Connected);
                        }
                        await SlackRelayApiClient.WaitForMessagesAsync(stream, token).ConfigureAwait(false);
                    }
                    SlackDeliveryBatch batch;
                    do { batch = await ReceiveBatchAsync(linked.Token, token).ConfigureAwait(false); }
                    while (useStream && batch.Messages.Length == 10);
                    failures = 0;
                    if (useStream) continue;
                    if (batch.PollAfterSeconds is int pollAfter)
                        await _delay(TimeSpan.FromSeconds(Math.Clamp(pollAfter, 1, 60)), token).ConfigureAwait(false);
                    else if (!batch.ReceiverConnected || batch.Messages.Length > 0)
                        await _delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (SlackRelayException ex) when (ex.Code == "session_expired") { _store.Clear(); _session = null; throw; }
                catch (SlackRelayException ex) when (ex.Code == "invalid_response") { throw; }
                catch (Exception ex)
                {
                    stream?.Dispose();
                    stream = null;
                    SetView(SlackConnectionPhase.Connecting);
                    AppLogger.Warn($"[Slack] 수신 재연결 ({ex.GetType().Name})");
                    await _delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5)))), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _feedback = ex is SlackRelayException error ? error.Code switch
                {
                    "approval_denied" => "Slack 연결을 취소했어요. 다시 연결할 수 있습니다.",
                    "session_expired" => "Slack 승인을 다시 받아야 해요. 다시 연결해 주세요.",
                    "pairing_expired" => "Slack 승인 시간이 지났어요. 다시 연결해 주세요.",
                    _ => "Slack 연결을 완료하지 못했어요. 다시 시도해 주세요."
                } : "Slack 연결을 완료하지 못했어요. 다시 시도해 주세요.";
                _current = new(SlackConnectionPhase.NeedsAttention, _session?.Account ?? _current.Account, _session?.Workspace ?? _current.Workspace);
            }
            AppLogger.Warn($"[Slack] 연결 확인 필요 ({ex.GetType().Name})");
        }
        finally
        {
            stream?.Dispose();
            if (token.IsCancellationRequested && _attemptId is not null)
            {
                try { await CancelApprovalAsync().ConfigureAwait(false); }
                catch (Exception ex) { AppLogger.Warn($"[Slack] 승인 요청 취소 대기 ({ex.GetType().Name})"); }
            }
            lock (_sync) { if (ReferenceEquals(_stop, stop)) _stop = null; stop.Dispose(); }
        }
    }
    private async Task<SlackDeliveryBatch> ReceiveBatchAsync(string session, CancellationToken token)
    {
        var batch = await _api!.PollAsync(session, token).ConfigureAwait(false);
        SetView(batch.ReceiverConnected ? SlackConnectionPhase.Connected : SlackConnectionPhase.Connecting);
        if (batch.Messages is null || batch.Messages.Length > 10) throw new SlackRelayException("invalid_response");
        var accepted = new List<string>();
        foreach (var delivery in batch.Messages)
        {
            token.ThrowIfCancellationRequested();
            if (!SlackConnectionProof.IsValid(delivery.Id) || delivery.Message is not { } message ||
                !message.Id.StartsWith("slack:", StringComparison.Ordinal)) throw new SlackRelayException("invalid_response");
            if (_seen.Add(delivery.Id))
            {
                try
                {
                    var notification = new RawNotification(message.Id, "Slack", message.Sender, "", message.Body, message.Timestamp);
                    var pending = new PendingNotification(notification, () => Task.FromResult<RawNotification?>(notification));
                    if (NotificationPending is { } handler) handler(this, pending);
                    else NotificationReceived?.Invoke(this, notification);
                }
                catch { _seen.Remove(delivery.Id); throw; }
                _seenOrder.Enqueue(delivery.Id);
                if (_seenOrder.Count > 4096) _seen.Remove(_seenOrder.Dequeue());
            }
            accepted.Add(delivery.Id);
        }
        if (accepted.Count > 0) await _api.AckAsync(session, accepted.ToArray(), token).ConfigureAwait(false);
        return batch;
    }
    private async Task<SlackSessionStatus> SessionStatusAsync(string session, CancellationToken token)
    {
        int failures = 0;
        while (true)
        {
            try { return await _api!.StatusAsync(session, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException
                || ex is SlackRelayException { Code: "service_unavailable" })
            {
                SetView(SlackConnectionPhase.Connecting);
                await _delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5)))), token).ConfigureAwait(false);
            }
        }
    }
    private void SetView(SlackConnectionPhase phase)
    {
        lock (_sync)
        {
            if (_stop?.IsCancellationRequested != false || _disposed) return;
            _current = new(phase, _session?.Account ?? "", _session?.Workspace ?? "");
        }
    }

    public async Task DisconnectAsync()
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            Task previous; lock (_sync) { _stop?.Cancel(); previous = _run; }
            await previous.ConfigureAwait(false);
            await CancelApprovalAsync().ConfigureAwait(false);
            var session = _session ?? (_server is null ? null : _store.Load(_server));
            if (session is not null && _api is not null)
            {
                try { await _api.DisconnectAsync(session.Token, CancellationToken.None).ConfigureAwait(false); }
                catch { lock (_sync) _current = new(SlackConnectionPhase.NeedsAttention, session.Account, session.Workspace); throw; }
            }
            _store.Clear(); _session = null;
            lock (_sync) { _current = new(SlackConnectionPhase.NotConnected); _feedback = null; }
        }
        finally { _actions.Release(); }
    }
    private async Task CancelApprovalAsync()
    {
        if (_attemptId is not { } id || _attemptProof is not { } proof || _api is null) return;
        try { await _api.CancelPairingAsync(id, proof, CancellationToken.None).ConfigureAwait(false); }
        catch (SlackRelayException ex) when (ex.Code == "pairing_expired") { }
        _attemptId = _attemptProof = null;
    }
    public void Stop() { lock (_sync) { _lifecycle++; _stop?.Cancel(); _current = new(SlackConnectionPhase.NotConnected); } }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true; Stop();
            _ = _run.ContinueWith(_ => _api?.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

using System.IO;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

public enum SlackConnectionPhase { NotConnected, Connecting, Connected, NeedsAttention, Paused, AwaitingApproval }

public sealed record SlackConnectionView(
    SlackConnectionPhase Phase, string Account = "", string Workspace = "");

public interface ISlackConnectionController
{
    SlackConnectionView Current { get; }
    bool UsesOAuth => false;
    string? Feedback => null;
    Task<bool> ConnectAsync();
    Task DisconnectAsync();
}

/// <summary>
/// Stable pipeline source, including when credentials are absent at startup.
/// Until OAuth is available, disconnect pauses local receipt and preserves encrypted credentials.
/// </summary>
internal sealed class SlackConnectionController : ISlackNotificationConnection
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly string _credentialPath;
    private readonly string _pausePath;
    private readonly Func<SlackConnectionSettings, SlackSocketModeListener> _createListener;
    private SlackSocketModeListener? _listener;
    private bool _paused;
    private bool _started;
    private bool _disposed;

    public event EventHandler<RawNotification>? NotificationReceived;
    public event EventHandler<PendingNotification>? NotificationPending;
    public bool IsRunning { get { lock (_sync) return !_paused && _started && _listener?.IsRunning == true; } }

    internal SlackConnectionController(string? credentialPath = null,
        Func<SlackConnectionSettings, SlackSocketModeListener>? createListener = null)
    {
        _credentialPath = credentialPath ?? SlackConnectionSettings.FilePath;
        _pausePath = _credentialPath + ".paused";
        _paused = File.Exists(_pausePath);
        _createListener = createListener ?? (settings => new SlackSocketModeListener(settings));
    }

    internal SlackConnectionController(SlackSocketModeListener listener, string credentialPath)
        : this(credentialPath)
    {
        AttachListener(listener);
    }

    public SlackConnectionView Current
    {
        get
        {
            lock (_sync)
            {
                if (_paused) return new(SlackConnectionPhase.Paused);
                if (!_started || _listener is null) return new(SlackConnectionPhase.NotConnected);
                var identity = _listener.ConnectedIdentity;
                return new(_listener.IsRunning ? SlackConnectionPhase.Connected
                        : _listener.Completion.IsCompleted ? SlackConnectionPhase.NeedsAttention : SlackConnectionPhase.Connecting,
                    identity?.UserName is { Length: > 0 } account ? account : identity?.UserId ?? "",
                    identity?.TeamName is { Length: > 0 } workspace ? workspace : identity?.TeamId ?? "");
            }
        }
    }

    // Pipeline startup respects the saved pause; only an explicit user action resumes receipt.
    public Task<bool> StartAsync() => StartAsync(resume: false);
    public Task<bool> ConnectAsync() => StartAsync(resume: true);

    private async Task<bool> StartAsync(bool resume)
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            Task<bool> starting;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!SlackConnectionSettings.IsEnabled || !resume && _paused) return false;
                var listener = _listener;
                if (listener is null)
                {
                    var settings = SlackConnectionSettings.TryLoad(_credentialPath);
                    if (settings is null) return false; // Interactive OAuth requires the future server.
                    listener = _createListener(settings);
                    AttachListener(listener);
                }
                if (resume)
                {
                    File.Delete(_pausePath);
                    _paused = false;
                }
                _started = true;
                starting = listener.StartAsync();
            }
            bool started = await starting.ConfigureAwait(false);
            lock (_sync)
            {
                // Pipeline shutdown may race a restart waiting for the old socket to exit.
                if (_disposed || !_started || _paused)
                {
                    _listener?.Stop();
                    return false;
                }
                return started;
            }
        }
        finally { _actions.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _actions.WaitAsync().ConfigureAwait(false);
        try
        {
            Task completion;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Save the user's choice before reporting success. Never delete the only
                // recoverable copy of the tokens while interactive reauthorization is unavailable.
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_pausePath))!);
                File.WriteAllText(_pausePath, "Local Slack DM receipt is paused. Credentials are retained.");
                _paused = true;
                _started = false;
                _listener?.Stop();
                completion = _listener?.Completion ?? Task.CompletedTask;
            }
            await completion.ConfigureAwait(false);
        }
        finally { _actions.Release(); }
    }

    private void AttachListener(SlackSocketModeListener listener)
    {
        _listener = listener;
        listener.NotificationReceived += OnReceived;
        listener.NotificationPending += OnPending;
    }

    private void OnReceived(object? sender, RawNotification notification)
    {
        lock (_sync)
            if (!_paused && _started && !_disposed) NotificationReceived?.Invoke(this, notification);
    }

    private void OnPending(object? sender, PendingNotification pending)
    {
        lock (_sync)
        {
            if (_paused || !_started || _disposed) return;
            if (NotificationPending is { } handler) handler(this, pending);
            else _ = ForwardPreparedAsync(pending);
        }
    }

    private async Task ForwardPreparedAsync(PendingNotification pending)
    {
        try
        {
            if (await pending.PrepareAsync().ConfigureAwait(false) is { } notification)
                OnReceived(this, notification);
        }
        catch (Exception ex) { AppLogger.Warn($"[Slack] 알림 전달 실패 ({ex.GetType().Name})"); }
    }

    // Pipeline shutdown is temporary and must not change the user's saved choice.
    public void Stop()
    {
        lock (_sync)
        {
            _started = false;
            _listener?.Stop();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            if (_listener is not { } listener) return;
            listener.NotificationReceived -= OnReceived;
            listener.NotificationPending -= OnPending;
            listener.Dispose();
        }
    }
}

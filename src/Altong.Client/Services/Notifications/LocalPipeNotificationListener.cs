using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// Win32 언패키징 개발 환경에서 Altong.MockGenerator로부터
/// 모의 알림을 초저지연(<1ms)으로 직접 수신하는 로컬 NamedPipe 리스너.
/// </summary>
public sealed class LocalPipeNotificationListener : IWindowsNotificationListener
{
    public const string PipeName = "Altong_Notification_Pipe";

    private CancellationTokenSource? _cts;
    private Task? _listeningTask;
    private bool _isRunning;
    private bool _isDisposed;
    private readonly object _syncRoot = new();

    public event EventHandler<RawNotification>? NotificationReceived;

    public bool IsRunning => _isRunning;

    public Task<bool> StartAsync()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isRunning)
            {
                return Task.FromResult(true);
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _isRunning = true;
            _listeningTask = Task.Run(() => ListenLoopAsync(cts.Token));
        }

        Console.WriteLine($"[LocalPipeNotificationListener] 로컬 모의 알림 파이프(\\.\\pipe\\{PipeName}) 수신 대기 시작");
        return Task.FromResult(true);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_syncRoot)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        string id = root.TryGetProperty("id", out var idProp)
                            ? idProp.GetString() ?? $"pipe_{DateTime.UtcNow.Ticks}"
                            : $"pipe_{DateTime.UtcNow.Ticks}";

                        string appName = root.TryGetProperty("app_name", out var appProp)
                            ? appProp.GetString() ?? "알 수 없는 앱"
                            : "알 수 없는 앱";

                        string sender = root.TryGetProperty("sender", out var senderProp)
                            ? senderProp.GetString() ?? ""
                            : "";

                        string title = root.TryGetProperty("title", out var titleProp)
                            ? titleProp.GetString() ?? ""
                            : "";

                        string body = root.TryGetProperty("body", out var bodyProp)
                            ? bodyProp.GetString() ?? ""
                            : "";

                        DateTime timestamp = DateTime.UtcNow;
                        if (root.TryGetProperty("timestamp", out var timeProp) &&
                            timeProp.TryGetDateTime(out var parsedTime))
                        {
                            timestamp = parsedTime.ToUniversalTime();
                        }

                        var noti = new RawNotification(id, appName, sender, title, body, timestamp);
                        NotificationReceived?.Invoke(this, noti);
                    }
                    catch (JsonException ex)
                    {
                        Console.WriteLine($"[LocalPipeNotificationListener] JSON 역직렬화 실패: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                Console.WriteLine($"[LocalPipeNotificationListener] 파이프 연결 대기 중 오류: {ex.Message}");
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        Stop();
    }
}

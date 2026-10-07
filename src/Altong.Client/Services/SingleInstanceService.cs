using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Altong.Client.Services;

/// <summary>같은 Windows 사용자·로그인 세션에서 한 프로세스만 수집하도록 한다.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    private bool _disposed;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public bool IsPrimary { get; }

    // Mutex 소유권은 생성 스레드에 있으므로 Dispose도 같은 스레드에서 호출한다.
    public SingleInstanceService(string? instanceKey = null)
    {
        string key = instanceKey ?? $"Altong.Client.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
        _pipeName = key;
        _mutex = new Mutex(false, $"Local\\{key}");
        try
        {
            IsPrimary = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 이전 프로세스가 비정상 종료된 경우에도 이 호출이 소유권을 획득한다.
            IsPrimary = true;
        }
    }

    public void StartListening(Action activate)
    {
        if (!IsPrimary || _listener is not null)
            throw new InvalidOperationException("Only the primary instance can start listening once.");
        _listener = ListenAsync(activate);
    }

    public async Task<bool> ActivateExistingAsync(TimeSpan timeout)
    {
        if (IsPrimary)
            throw new InvalidOperationException("The primary instance cannot activate itself through IPC.");
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client, leaveOpen: true);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            string? processId = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (!uint.TryParse(processId, out uint primaryPid))
                return false;
            // 사용자가 실행한 새 프로세스의 전경 활성화 권한을 기존 앱에 전달한다.
            AllowSetForegroundWindow(primaryPid);
            await writer.WriteLineAsync("activate".AsMemory(), deadline.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) == "ok";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Action activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                // 연결 후 응답하지 않는 클라이언트가 다음 실행 요청을 막지 않도록 제한한다.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString().AsMemory(), deadline.Token).ConfigureAwait(false);
                if (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) == "activate")
                {
                    activate();
                    await writer.WriteLineAsync("ok".AsMemory(), deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Error("[SingleInstance] 기존 창 활성화 연결을 열 수 없습니다.", ex);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        // UI 디스패처를 기다리지 않는 리스너이므로 종료 시 정리할 수 있다.
        _listener?.GetAwaiter().GetResult();
        _stop.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Altong.Client.Services.NotificationControl;

public sealed class NotificationRecoveryGuardian : IDisposable
{
    /// <summary>미리 띄운 감시 프로세스가 복구 파일이 생기기를 기다리는 최대 시간. 켜기가 실패하면 이 시간 뒤 스스로 끝난다.</summary>
    internal static readonly TimeSpan JournalWait = TimeSpan.FromSeconds(60);
    private Process? _process;
    private EventWaitHandle? _ready;
    private string? _preparedToken;
    public bool IsAlive => _process is { HasExited: false };

    public void Start(NotificationRecoveryJournal journal)
    {
        if (_preparedToken != journal.Token) Prepare(journal.OwnerPid, journal.OwnerStartTicks, journal.Token);
        WaitReady();
    }

    /// <summary>
    /// 감시 프로세스를 띄우고 준비를 기다리지 않는다. 프로세스 기동(약 0.5초)을 발신자 조회와 겹치기 위해 켜기 시작 때 부른다.
    /// 감시는 복구 파일이 생긴 뒤 사라지면 끝나고, 소유자가 죽으면 그 토큰의 복구 파일을 복구한다.
    /// 알림 설정을 바꾸기 전에는 반드시 <see cref="WaitReady"/>(또는 <see cref="Start"/>)로 준비를 확인한다.
    /// </summary>
    public void Prepare(int ownerPid, long ownerStartTicks, string token)
    {
        Abort();
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\Altong.NotificationGuardian." + token);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Altong.Client.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "--notification-guardian", ownerPid.ToString(CultureInfo.InvariantCulture),
                     ownerStartTicks.ToString(CultureInfo.InvariantCulture), token }) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new IOException("알림 복구 감시 프로세스를 시작하지 못했습니다.");
        _preparedToken = token;
    }

    /// <summary>미리 띄운 감시 프로세스가 준비될 때까지 기다린다. 준비되지 않으면 예외로 알림 설정 변경을 막는다.</summary>
    public void WaitReady()
    {
        if (_ready is null || !_ready.WaitOne(TimeSpan.FromSeconds(8)) || !IsAlive)
            throw new IOException("알림 복구 감시 프로세스가 준비되지 않았습니다.");
        _ready.Dispose();
        _ready = null;
    }

    /// <summary>켜기가 설정을 바꾸기 전에 실패했을 때 미리 띄운 감시 프로세스를 끝낸다. 감시는 아직 아무것도 하지 않은 상태다.</summary>
    public void Abort()
    {
        _ready?.Dispose();
        _ready = null;
        _preparedToken = null;
        if (_process is { HasExited: false })
        {
            try { _process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        _process?.Dispose();
        _process = null;
    }

    // Runs before normal startup and single-instance checks. It never constructs the dashboard or AI models.
    public static int Run(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out int pid) || !long.TryParse(args[2], out long ticks)
            || !Guid.TryParseExact(args[3], "N", out _)) return 2;
        try
        {
            // Open before checking owner liveness: a parent can die between launching us and this point.
            using var ready = EventWaitHandle.OpenExisting("Local\\Altong.NotificationGuardian." + args[3]);
            ready.Set();
            // 켜기 시작 때 미리 띄워질 수 있으므로, 복구 파일이 생긴 뒤 사라졌을 때(정상 종료) 끝난다.
            // 복구 파일이 끝내 생기지 않으면(켜기 실패) 기다리다가 끝난다.
            bool journalSeen = false;
            var waitStarted = DateTime.UtcNow;
            while (NotificationSuppressionTransaction.IsOwnerAlive(pid, ticks))
            {
                if (File.Exists(NotificationSuppressionTransaction.DefaultJournalPath)) journalSeen = true;
                else if (journalSeen || DateTime.UtcNow - waitStarted > JournalWait) return 0;
                Thread.Sleep(journalSeen ? 500 : 100);
            }
            var transaction = new NotificationSuppressionTransaction(new WindowsNotificationSenderSettings());
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try { transaction.Restore(abandonedOnly: true, expectedToken: args[3]); return 0; }
                catch { Thread.Sleep(1000); }
            }
            // A reboot/logoff may kill both processes. The durable journal is also recovered on the next launch.
            return 1;
        }
        catch { return 1; }
    }

    public void Dispose()
    {
        _ready?.Dispose();
        _ready = null;
        _process?.Dispose();
        _process = null;
    }
}

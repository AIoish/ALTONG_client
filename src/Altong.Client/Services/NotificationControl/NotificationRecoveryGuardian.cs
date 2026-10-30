using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Altong.Client.Services.NotificationControl;

public sealed class NotificationRecoveryGuardian : IDisposable
{
    private Process? _process;
    public bool IsAlive => _process is { HasExited: false };

    public void Start(NotificationRecoveryJournal journal)
    {
        _process?.Dispose();
        string eventName = "Local\\Altong.NotificationGuardian." + journal.Token;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Altong.Client.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "--notification-guardian", journal.OwnerPid.ToString(CultureInfo.InvariantCulture),
                     journal.OwnerStartTicks.ToString(CultureInfo.InvariantCulture), journal.Token }) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new IOException("알림 복구 감시 프로세스를 시작하지 못했습니다.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(8)) || !IsAlive)
            throw new IOException("알림 복구 감시 프로세스가 준비되지 않았습니다.");
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
            while (NotificationSuppressionTransaction.IsOwnerAlive(pid, ticks))
            {
                if (!File.Exists(NotificationSuppressionTransaction.DefaultJournalPath)) return 0;
                Thread.Sleep(500);
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

    public void Dispose() { _process?.Dispose(); _process = null; }
}

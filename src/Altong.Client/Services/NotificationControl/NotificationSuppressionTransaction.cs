using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Altong.Client.Services.NotificationControl;

public sealed class NotificationRecoveryJournal
{
    public int Version { get; set; } = 1;
    public int OwnerPid { get; set; }
    public long OwnerStartTicks { get; set; }
    public string Token { get; set; } = Guid.NewGuid().ToString("N");
    public List<SenderBackup> Senders { get; set; } = [];
}

/// <summary>Write-ahead undo journal. No mutation is possible until the complete backup and guardian are ready.</summary>
public sealed class NotificationSuppressionTransaction
{
    public static string DefaultJournalPath => Path.Combine(NotificationControlPreferences.DirectoryPath, "notification-recovery.json");
    private readonly INotificationSenderSettings _settings;
    private readonly string _path;
    private readonly string _mutexName;
    public bool HasPendingRecovery => File.Exists(_path);

    public NotificationSuppressionTransaction(INotificationSenderSettings settings, string? journalPath = null)
    {
        _settings = settings;
        _path = Path.GetFullPath(journalPath ?? DefaultJournalPath);
        _mutexName = "Local\\Altong.NotificationRecovery." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..24];
    }

    public int Begin(IEnumerable<string> selected, Action<NotificationRecoveryJournal> startGuardian)
    {
        using var gate = Enter();
        if (HasPendingRecovery) throw new InvalidOperationException("이전 알림 설정을 먼저 복구해 주세요.");
        if (_settings.CompatibilityError is { } error) throw new NotSupportedException(error);
        var eligible = _settings.Enumerate().Where(s => s.IsEligible).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] ids = selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("제어할 앱을 한 개 이상 선택해 주세요.");
        if (ids.Any(id => !eligible.Contains(id))) throw new InvalidOperationException("제어 대상 앱 목록이 바뀌었습니다. 목록을 새로 조회해 주세요.");
        using var process = Process.GetCurrentProcess();
        var journal = new NotificationRecoveryJournal { OwnerPid = process.Id, OwnerStartTicks = process.StartTime.ToUniversalTime().Ticks };
        foreach (string id in ids)
            journal.Senders.Add(new(id, _settings.Read(id), _settings.ReadRegistry(id, false), _settings.ReadRegistry(id, true)));
        DurableJson.Write(_path, journal);
        try
        {
            startGuardian(journal);
            foreach (var item in journal.Senders)
            {
                if (_settings.Read(item.Id) != item.Original)
                    throw new InvalidOperationException("시작 중 Windows 알림 설정이 변경되었습니다. 다시 시도해 주세요.");
                _settings.SetBanner(item.Id, 0);
                _settings.SetSound(item.Id, "");
                var actual = _settings.Read(item.Id);
                if (actual != item.Original with { ShowBanner = 0, SoundFile = "" })
                    throw new IOException("배너·알림음 설정 적용을 확인하지 못했습니다.");
            }
            return journal.Senders.Count;
        }
        catch (Exception applyError)
        {
            try { RestoreCore(journal); }
            catch (Exception restoreError) { throw new AggregateException("적용에 실패했고 일부 설정은 복구 대기 중입니다.", applyError, restoreError); }
            throw;
        }
    }

    public void Restore(bool abandonedOnly = false, string? expectedToken = null)
    {
        using var gate = Enter();
        if (!HasPendingRecovery) return;
        var journal = ReadJournal();
        if (expectedToken is not null && journal.Token != expectedToken) return;
        bool ownerAlive = IsOwnerAlive(journal.OwnerPid, journal.OwnerStartTicks);
        if (ownerAlive && (abandonedOnly || journal.OwnerPid != Environment.ProcessId))
            throw new InvalidOperationException("다른 실행 중인 알통이 알림을 제어하고 있습니다.");
        RestoreCore(journal);
    }

    private void RestoreCore(NotificationRecoveryJournal journal)
    {
        var failures = new List<Exception>();
        foreach (var item in journal.Senders.ToArray())
        {
            try
            {
                // Never recreate settings for an uninstalled sender. Other senders must still recover.
                // Access/COM failures are not proof of removal and must retain their backup for retry.
                if (_settings.IsRegistered(item.Id))
                {
                    RestoreField(item, sound: false);
                    RestoreField(item, sound: true);
                }
                journal.Senders.Remove(item);
                // A crash halfway through restoration must leave the remaining work on disk.
                DurableJson.Write(_path, journal);
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("일부 앱의 알림 설정을 복구하지 못했습니다. 복구를 다시 시도해 주세요.", failures);
        File.Delete(_path);
    }

    private void RestoreField(SenderBackup item, bool sound)
    {
        var current = _settings.Read(item.Id);
        // Restore only values still owned by Altong. Preserve changes made by the user during focus.
        bool ours = sound ? current.SoundFile == "" || current.SoundFile == item.Original.SoundFile
            : current.ShowBanner == 0 || current.ShowBanner == item.Original.ShowBanner;
        if (!ours) return;
        RegistrySetting originalRegistry = sound ? item.SoundRegistry : item.BannerRegistry;
        RegistrySetting currentRegistry = _settings.ReadRegistry(item.Id, sound);
        RegistrySetting suppressedRegistry = sound
            ? new(true, (int)Microsoft.Win32.RegistryValueKind.String, JsonSerializer.Serialize(""))
            : new(true, (int)Microsoft.Win32.RegistryValueKind.DWord, "0");
        bool registryOurs = currentRegistry == originalRegistry || currentRegistry == suppressedRegistry;
        // An externally changed registry value takes precedence even if Windows' COM cache lags behind it.
        if (!registryOurs) return;
        if (sound) _settings.SetSound(item.Id, item.Original.SoundFile);
        else _settings.SetBanner(item.Id, item.Original.ShowBanner);
        _settings.RestoreRegistry(item.Id, sound, originalRegistry);
        var restored = _settings.Read(item.Id);
        if ((sound ? restored.SoundFile != item.Original.SoundFile : restored.ShowBanner != item.Original.ShowBanner)
            || _settings.ReadRegistry(item.Id, sound) != originalRegistry)
            throw new IOException("원래 알림 설정으로의 복구 확인에 실패했습니다.");
    }

    private NotificationRecoveryJournal ReadJournal()
    {
        var journal = JsonSerializer.Deserialize<NotificationRecoveryJournal>(File.ReadAllText(_path));
        if (journal is null || journal.Version != 1 || journal.OwnerPid <= 0 || journal.OwnerStartTicks <= 0
            || !Guid.TryParseExact(journal.Token, "N", out _) || journal.Senders is null
            || journal.Senders.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Original is null || s.BannerRegistry is null || s.SoundRegistry is null))
            throw new InvalidDataException("알림 복구 파일 형식이 올바르지 않습니다. 파일을 보존하고 제어를 중단합니다.");
        return journal;
    }

    public static bool IsOwnerAlive(int pid, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // Access denied is not evidence that another process has exited.
    }

    private IDisposable Enter()
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("알림 복구 작업이 진행 중입니다."); }
            catch (AbandonedMutexException) { /* We now own the abandoned lock; the journal is the source of truth. */ }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}

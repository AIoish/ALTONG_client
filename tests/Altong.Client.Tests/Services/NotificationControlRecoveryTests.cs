using System.Text.Json;
using Altong.Client.Services;
using Altong.Client.Services.NotificationControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationControlRecoveryTests
{
    private string _directory = null!;
    private string PreferencesPath => Path.Combine(_directory, "notification-control.json");
    private string JournalPath => Path.Combine(_directory, "notification-recovery.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Altong.NotificationRecoveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void RemovedSender_DoesNotBlockOtherRestorationOrWindowsSwitch()
    {
        var settings = new MemorySettings();
        var originalOther = settings.Apps["Other"].State;
        using var controller = new NativeFocusController(settings, PreferencesPath, JournalPath);
        new NotificationSuppressionTransaction(settings, JournalPath).Begin(["App", "Other"], _ => { });
        settings.Apps.Remove("App");
        int writesBeforeRemovalRecovery = settings.Writes["App"];

        controller.Stop();
        controller.SetBackend(FocusControlBackend.Windows);

        Assert.IsFalse(File.Exists(JournalPath));
        Assert.AreEqual(originalOther, settings.Read("Other"));
        Assert.IsFalse(settings.Apps["Other"].Banner.Exists);
        Assert.IsFalse(settings.Apps["Other"].Sound.Exists);
        Assert.AreEqual(writesBeforeRemovalRecovery, settings.Writes["App"]);
        Assert.IsFalse(settings.Apps.ContainsKey("App"), "Recovery must not recreate an uninstalled sender.");
        Assert.AreEqual(FocusControlBackend.Windows, controller.Backend);
        // A fresh focus transaction remains possible after recovering the remaining app.
        var next = new NotificationSuppressionTransaction(settings, JournalPath);
        next.Begin(["Other"], _ => { });
        next.Restore();
        Assert.AreEqual(originalOther, settings.Read("Other"));
    }

    [TestMethod]
    public void RemovedSender_AfterCrashDoesNotBlockStartupRecovery()
    {
        var settings = new MemorySettings();
        var original = settings.Read("Other");
        new NotificationSuppressionTransaction(settings, JournalPath).Begin(["App", "Other"], _ => { });
        var journal = JsonSerializer.Deserialize<NotificationRecoveryJournal>(File.ReadAllText(JournalPath))!;
        journal.OwnerStartTicks = 1;
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal));
        settings.Apps.Remove("App");

        using var controller = new NativeFocusController(settings, PreferencesPath, JournalPath);

        Assert.IsFalse(File.Exists(JournalPath));
        Assert.AreEqual(original, settings.Read("Other"));
        Assert.IsFalse(controller.Status.Contains("복구 대기", StringComparison.Ordinal));
        controller.SetBackend(FocusControlBackend.Windows);
        Assert.AreEqual(FocusControlBackend.Windows, controller.Backend);
    }

    [TestMethod]
    public void SenderAccessFailure_IsNotMistakenForDeletionAndCanRetry()
    {
        var settings = new MemorySettings();
        var original = settings.Read("App");
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        transaction.Begin(["App"], _ => { });
        settings.FailRegistrationRead = true;

        Assert.ThrowsException<AggregateException>(() => transaction.Restore());
        Assert.IsTrue(transaction.HasPendingRecovery);
        Assert.AreEqual("", settings.Apps["App"].State.SoundFile);

        settings.FailRegistrationRead = false;
        transaction.Restore();
        Assert.AreEqual(original, settings.Read("App"));
        Assert.IsFalse(transaction.HasPendingRecovery);
    }

    [DataTestMethod]
    [DataRow("{ broken")]
    [DataRow("null")]
    [DataRow("{\"Backend\":999}")]
    [DataRow("{\"ExcludedSenders\":null}")]
    [DataRow("{\"ExcludedSenders\":[null]}")]
    [DataRow("{\"ExcludedSenders\":[\"\"]}")]
    [DataRow("{\"Backend\":{}}")]
    public void CorruptPreferences_ArePreservedAndWindowsModeRemainsUsable(string damagedJson)
    {
        File.WriteAllText(PreferencesPath, damagedJson);
        byte[] damagedBytes = File.ReadAllBytes(PreferencesPath);
        var settings = new MemorySettings();
        using var controller = new NativeFocusController(settings, PreferencesPath, JournalPath);
        var backups = Directory.GetFiles(_directory, "notification-control.json.corrupt-*.json");

        Assert.AreEqual(1, backups.Length);
        CollectionAssert.AreEqual(damagedBytes, File.ReadAllBytes(backups[0]));
        Assert.AreEqual(FocusControlBackend.Windows, controller.Backend);
        Assert.AreEqual(FocusControlBackend.Windows, NotificationControlPreferences.Load(PreferencesPath).Backend);
        Assert.AreEqual(0, settings.Writes.Values.Sum(), "Preference repair must not mutate Windows notification settings.");
        StringAssert.Contains(controller.Status, "별도 보관");

        var focus = new FocusModeService();
        using (var coordinator = new FocusModeCoordinator(focus, new Observer(), new Launcher(), new Dispatcher(), controller))
        {
            coordinator.Start();
            Assert.IsTrue(focus.IsEnabled, "The existing Windows mode must work after preferences are repaired.");
            controller.SetSelected("App", false);
            coordinator.SwitchBackend(FocusControlBackend.Altong);
            Assert.AreEqual(FocusControlBackend.Altong, controller.Backend);
            Assert.IsFalse(focus.IsEnabled);
            Assert.IsNull(coordinator.Guidance);
        }

        using var reopened = new NativeFocusController(settings, PreferencesPath, JournalPath);
        Assert.AreEqual(FocusControlBackend.Altong, reopened.Backend);
        Assert.IsFalse(reopened.IsSelected("App"));
        Assert.AreEqual(1, Directory.GetFiles(_directory, "notification-control.json.corrupt-*.json").Length);
    }

    [TestMethod]
    public void FailedPreferenceRepair_PreservesOriginalAndUsesWindowsInMemory()
    {
        const string damaged = "{ broken";
        File.WriteAllText(PreferencesPath, damaged);
        Directory.CreateDirectory(PreferencesPath + ".tmp"); // Deterministic atomic-save failure.
        var settings = new MemorySettings();
        using var controller = new NativeFocusController(settings, PreferencesPath, JournalPath);

        Assert.AreEqual(damaged, File.ReadAllText(PreferencesPath));
        Assert.AreEqual(1, Directory.GetFiles(_directory, "notification-control.json.corrupt-*.json").Length);
        Assert.AreEqual(FocusControlBackend.Windows, controller.Backend);
        StringAssert.Contains(controller.Status, "이 실행에서는 Windows");
        Assert.ThrowsException<InvalidOperationException>(() => controller.Start());
        var focus = new FocusModeService();
        var launcher = new Launcher();
        using var coordinator = new FocusModeCoordinator(focus, new Observer(), launcher, new Dispatcher(), controller);
        coordinator.Start();
        Assert.IsTrue(focus.IsEnabled);
        coordinator.RequestToggle();
        Assert.AreEqual(1, launcher.OpenCount);
        Assert.AreEqual(0, settings.Writes.Values.Sum());
    }

    [TestMethod]
    public void PreferencesPathIsDirectory_UsesWindowsWithoutOverwritingIt()
    {
        Directory.CreateDirectory(PreferencesPath);
        var settings = new MemorySettings();
        using var controller = new NativeFocusController(settings, PreferencesPath, JournalPath);
        Assert.AreEqual(FocusControlBackend.Windows, controller.Backend);
        Assert.IsTrue(Directory.Exists(PreferencesPath));
        Assert.AreEqual(0, settings.Writes.Values.Sum());
    }

    [TestMethod]
    public void CorruptRecoveryJournal_IsPreservedAndNeverResetAsPreferences()
    {
        const string damagedJournal = "{ broken backup";
        File.WriteAllText(JournalPath, damagedJournal);
        new NotificationControlPreferences { Backend = FocusControlBackend.Windows }.Save(PreferencesPath);
        var settings = new MemorySettings();
        using var controller = new NativeFocusController(settings, PreferencesPath, JournalPath);

        Assert.AreEqual(damagedJournal, File.ReadAllText(JournalPath));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "notification-control.json.corrupt-*.json").Length);
        StringAssert.Contains(controller.Status, "복구 대기");
        Assert.ThrowsException<InvalidOperationException>(() => controller.Start());
        Assert.AreEqual(0, settings.Writes.Values.Sum());
    }

    [TestMethod]
    public void ValidPreferences_AreLoadedWithoutResetOrBackup()
    {
        var preferences = new NotificationControlPreferences { Backend = FocusControlBackend.Windows };
        preferences.ExcludedSenders.Add("APP");
        preferences.Save(PreferencesPath);
        byte[] originalBytes = File.ReadAllBytes(PreferencesPath);
        using var controller = new NativeFocusController(new MemorySettings(), PreferencesPath, JournalPath);
        Assert.AreEqual(FocusControlBackend.Windows, controller.Backend);
        Assert.IsFalse(controller.IsSelected("App"));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(PreferencesPath));
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.corrupt-*.json").Length);
    }

    private sealed class MemorySettings : INotificationSenderSettings
    {
        public sealed class AppSettings
        {
            public SenderState State = new(true, true, 1, "*default*");
            public RegistrySetting Banner = new(false, 0, null), Sound = new(false, 0, null);
        }
        public Dictionary<string, AppSettings> Apps { get; } = new() { ["App"] = new(), ["Other"] = new() };
        public Dictionary<string, int> Writes { get; } = new() { ["App"] = 0, ["Other"] = 0 };
        public bool FailRegistrationRead;
        public string? CompatibilityError => null;
        public IReadOnlyList<NotificationSender> Enumerate() => Apps.Keys.Select(id => new NotificationSender(id, id, null)).ToArray();
        public bool IsRegistered(string id) => FailRegistrationRead ? throw new UnauthorizedAccessException("access denied") : Apps.ContainsKey(id);
        public SenderState Read(string id) => Apps[id].State;
        public RegistrySetting ReadRegistry(string id, bool sound) => sound ? Apps[id].Sound : Apps[id].Banner;
        public void SetBanner(string id, int value)
        {
            Writes[id]++;
            Apps[id].State = Apps[id].State with { ShowBanner = value };
            Apps[id].Banner = new(true, 4, value.ToString());
        }
        public void SetSound(string id, string value)
        {
            Writes[id]++;
            Apps[id].State = Apps[id].State with { SoundFile = value };
            Apps[id].Sound = new(true, 1, JsonSerializer.Serialize(value));
        }
        public void RestoreRegistry(string id, bool sound, RegistrySetting value)
        {
            Writes[id]++;
            if (sound) Apps[id].Sound = value; else Apps[id].Banner = value;
        }
    }

    private sealed class Dispatcher : IUiDispatcher { public void Invoke(Action action) => action(); }
    private sealed class Launcher : IWindowsNotificationSettingsLauncher
    {
        public int OpenCount;
        public bool TryOpen(out string? errorMessage) { OpenCount++; errorMessage = null; return true; }
    }
    private sealed class Observer : IWindowsNotificationModeObserver
    {
        public WindowsNotificationModeState CurrentState => new(WindowsNotificationModeKind.PriorityOnly);
        public event EventHandler<WindowsNotificationModeChangedEventArgs>? ModeChanged;
        public void Start() => ModeChanged?.Invoke(this, new(CurrentState));
        public WindowsNotificationModeState Refresh() => CurrentState;
        public void Dispose() { }
    }
}

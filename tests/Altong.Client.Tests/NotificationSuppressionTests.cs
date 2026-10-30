using System.Diagnostics;
using System.Text.Json;
using Altong.Client.Services.NotificationControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationSuppressionTests
{
    private string _directory = null!;
    private string JournalPath => Path.Combine(_directory, "undo.json");
    [TestInitialize] public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), "Altong.NotificationTests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [DataTestMethod]
    [DataRow("Windows.ActionCenter.SmartOptOut")]
    [DataRow("Windows.SystemToast.WindowsUpdate.MoNotification2")]
    [DataRow("Windows.Defender.SecurityCenter")]
    [DataRow("Microsoft.Windows.DynamicLightingToastHandler")]
    [DataRow("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")]
    public void SystemSendersAreNeverEligible(string id) => Assert.IsNotNull(NotificationSenderPolicy.ExclusionReason(id, true, true, true));

    [DataTestMethod]
    [DataRow("Chrome")]
    [DataRow("Microsoft.Office.POWERPNT.EXE.15")]
    [DataRow("com.tinyspeck.slackdesktop_8yrtsj140pw4g!Slack")]
    public void OrdinaryAppsRemainEligible(string id) => Assert.IsNull(NotificationSenderPolicy.ExclusionReason(id, true, true, true));

    [TestMethod]
    public void DisabledUnregisteredAndNonRetainedAppsAreExcluded()
    {
        Assert.IsNotNull(NotificationSenderPolicy.ExclusionReason("Deleted", false, true, true));
        Assert.IsNotNull(NotificationSenderPolicy.ExclusionReason("Off", true, false, true));
        Assert.IsNotNull(NotificationSenderPolicy.ExclusionReason("NoCenter", true, true, false));
    }

    [TestMethod]
    public void BackupAndGuardianPrecedeMutations_AndExactSettingsRestore()
    {
        var settings = new FakeSettings();
        var original = settings.State;
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        bool ready = false;
        settings.BeforeWrite = () => { Assert.IsTrue(ready); Assert.IsTrue(File.Exists(JournalPath)); };
        int count = transaction.Begin(["App"], journal =>
        {
            Assert.AreEqual(0, settings.Writes);
            Assert.AreEqual(original, journal.Senders.Single().Original);
            Assert.IsTrue(File.Exists(JournalPath));
            ready = true;
        });
        Assert.AreEqual(1, count);
        Assert.AreEqual(original with { ShowBanner = 0, SoundFile = "" }, settings.State);
        transaction.Restore();
        Assert.AreEqual(original, settings.State);
        Assert.IsFalse(settings.BannerRegistry.Exists);
        Assert.IsFalse(settings.SoundRegistry.Exists);
        Assert.IsFalse(File.Exists(JournalPath));
    }

    [TestMethod]
    public void GuardianFailureNeverSuppressesAnything()
    {
        var settings = new FakeSettings();
        var original = settings.State;
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        Assert.ThrowsException<IOException>(() => transaction.Begin(["App"], _ => throw new IOException("guardian failed")));
        Assert.AreEqual(original, settings.State);
        Assert.IsFalse(transaction.HasPendingRecovery);
    }

    [TestMethod]
    public void PartialApplyFailureRollsBackBannerAndSound()
    {
        var settings = new FakeSettings { FailNextSoundWrite = true };
        var original = settings.State;
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        Assert.ThrowsException<IOException>(() => transaction.Begin(["App"], _ => { }));
        Assert.AreEqual(original, settings.State);
        Assert.IsFalse(transaction.HasPendingRecovery);
    }

    [TestMethod]
    public void ExternalChangesArePreservedPerField()
    {
        var settings = new FakeSettings();
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        transaction.Begin(["App"], _ => { });
        settings.State = settings.State with { Enabled = false, ShowInActionCenter = false, SoundFile = "custom.wav" };
        settings.SoundRegistry = new(true, 1, JsonSerializer.Serialize("custom.wav"));
        transaction.Restore();
        Assert.IsFalse(settings.State.Enabled);
        Assert.IsFalse(settings.State.ShowInActionCenter);
        Assert.AreEqual(1, settings.State.ShowBanner);
        Assert.AreEqual("custom.wav", settings.State.SoundFile);
        Assert.AreEqual(new RegistrySetting(true, 1, JsonSerializer.Serialize("custom.wav")), settings.SoundRegistry);
    }

    [TestMethod]
    public void RestoreFailureRetainsJournalForRetry()
    {
        var settings = new FakeSettings();
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        var original = settings.State;
        transaction.Begin(["App"], _ => { });
        settings.FailNextSoundWrite = true;
        Assert.ThrowsException<AggregateException>(() => transaction.Restore());
        Assert.IsTrue(transaction.HasPendingRecovery);
        transaction.Restore();
        Assert.AreEqual(original, settings.State);
        Assert.IsFalse(transaction.HasPendingRecovery);
    }

    [TestMethod]
    public void AnotherLiveOwnerAndMalformedJournalBlockNewWrites()
    {
        var settings = new FakeSettings();
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        transaction.Begin(["App"], _ => { });
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Restore(abandonedOnly: true));
        transaction.Restore();
        File.WriteAllText(JournalPath, "{ broken json");
        Assert.ThrowsException<JsonException>(() => transaction.Restore());
        int writes = settings.Writes;
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Begin(["App"], _ => { }));
        Assert.AreEqual(writes, settings.Writes);
        Assert.AreEqual("{ broken json", File.ReadAllText(JournalPath));
    }

    [TestMethod]
    public void AbandonedJournalCanBeRestoredByFreshInstance()
    {
        var settings = new FakeSettings();
        var original = settings.State;
        new NotificationSuppressionTransaction(settings, JournalPath).Begin(["App"], _ => { });
        var journal = JsonSerializer.Deserialize<NotificationRecoveryJournal>(File.ReadAllText(JournalPath))!;
        journal.OwnerStartTicks = 1; // PID reuse must not prevent recovery.
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(journal));
        new NotificationSuppressionTransaction(settings, JournalPath).Restore(abandonedOnly: true);
        Assert.AreEqual(original, settings.State);
        Assert.IsFalse(File.Exists(JournalPath));
    }

    [TestMethod]
    public void ProtectedAndStaleSelectionsNeverMutate()
    {
        var settings = new FakeSettings();
        var transaction = new NotificationSuppressionTransaction(settings, JournalPath);
        Assert.ThrowsException<InvalidOperationException>(() => transaction.Begin(["Windows.ActionCenter.SmartOptOut"], _ => { }));
        Assert.AreEqual(0, settings.Writes);
        Assert.IsFalse(transaction.HasPendingRecovery);
    }

    private sealed class FakeSettings : INotificationSenderSettings
    {
        public string? CompatibilityError => null;
        public SenderState State = new(true, true, 1, "ms-winsoundevent:Notification.Default");
        public RegistrySetting BannerRegistry = new(false, 0, null), SoundRegistry = new(false, 0, null);
        public int Writes;
        public bool FailNextSoundWrite;
        public Action? BeforeWrite;
        public IReadOnlyList<NotificationSender> Enumerate() => [new("App", "Test app", null)];
        public bool IsRegistered(string id) => id == "App";
        public SenderState Read(string id) => State;
        public RegistrySetting ReadRegistry(string id, bool sound) => sound ? SoundRegistry : BannerRegistry;
        public void SetBanner(string id, int value)
        {
            BeforeWrite?.Invoke(); Writes++;
            State = State with { ShowBanner = value };
            BannerRegistry = new(true, 4, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        public void SetSound(string id, string value)
        {
            BeforeWrite?.Invoke(); Writes++;
            if (FailNextSoundWrite) { FailNextSoundWrite = false; throw new IOException("sound write failed"); }
            State = State with { SoundFile = value };
            SoundRegistry = new(true, 1, JsonSerializer.Serialize(value));
        }
        public void RestoreRegistry(string id, bool sound, RegistrySetting value)
        {
            if (sound) SoundRegistry = value; else BannerRegistry = value;
        }
    }
}

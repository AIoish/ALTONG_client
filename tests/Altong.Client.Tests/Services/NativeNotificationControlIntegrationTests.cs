using System.Diagnostics;
using System.Text.Json;
using Altong.Client.Services.NotificationControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>Explicit local integration checks. Never mutate notification preferences in ordinary CI runs.</summary>
[TestClass]
[TestCategory("NativeNotificationControl")]
public sealed class NativeNotificationControlIntegrationTests
{
    [TestMethod]
    public void ReadRegisteredSendersOnValidatedWindows()
    {
        if (Environment.GetEnvironmentVariable("ALTONG_NATIVE_NOTIFICATION_TESTS") != "1") Assert.Inconclusive("Local Windows check is opt-in.");
        var settings = new WindowsNotificationSenderSettings();
        Assert.IsNull(settings.CompatibilityError, settings.CompatibilityError);
        var rows = settings.Enumerate();
        Assert.IsTrue(rows.Count > 0);
        foreach (var row in rows) Console.WriteLine($"{(row.IsEligible ? "APP" : "EXCLUDED")} {row.Id}: {row.Name} {row.ExclusionReason}");
        Assert.IsTrue(rows.Any(r => r.IsEligible), "COM metadata must resolve at least one real app.");
        Assert.IsTrue(rows.Any(r => r.Id == "Chrome" && r.IsEligible));
        Assert.IsTrue(rows.Where(r => r.Id.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)).All(r => !r.IsEligible));
    }

    [TestMethod]
    public void SuppressAndRestoreOnlyOwnedMockSender()
    {
        if (Environment.GetEnvironmentVariable("ALTONG_NATIVE_NOTIFICATION_WRITE_TESTS") != "1") Assert.Inconclusive("Local write check is opt-in.");
        var settings = new WindowsNotificationSenderSettings();
        string id = GetMockId(settings);
        var original = settings.Read(id);
        var banner = settings.ReadRegistry(id, false); var sound = settings.ReadRegistry(id, true);
        var transaction = new NotificationSuppressionTransaction(settings);
        using var guardian = new NotificationRecoveryGuardian();
        try
        {
            transaction.Begin([id], guardian.Start);
            Assert.IsTrue(guardian.IsAlive);
            Assert.AreEqual(original with { ShowBanner = 0, SoundFile = "" }, settings.Read(id));
        }
        finally { transaction.Restore(); }
        Assert.AreEqual(original, settings.Read(id));
        Assert.AreEqual(banner, settings.ReadRegistry(id, false)); Assert.AreEqual(sound, settings.ReadRegistry(id, true));
        Assert.IsFalse(transaction.HasPendingRecovery);
    }

    [TestMethod]
    public void CrashOwner_OptInOnly()
    {
        if (Environment.GetEnvironmentVariable("ALTONG_NOTIFICATION_CRASH_OWNER") != "1") Assert.Inconclusive("External controller must explicitly terminate this isolated test host.");
        var settings = new WindowsNotificationSenderSettings();
        string id = GetMockId(settings);
        using var guardian = new NotificationRecoveryGuardian();
        new NotificationSuppressionTransaction(settings).Begin([id], guardian.Start);
        Console.WriteLine($"GUARDIAN_READY owner={Environment.ProcessId}");
        // External controller kills this test host, not the runner. The test intentionally has no graceful restoration.
        Thread.Sleep(TimeSpan.FromMinutes(2));
        new NotificationSuppressionTransaction(settings).Restore();
        Assert.Fail("Crash controller did not terminate the isolated owner.");
    }

    [TestMethod]
    public void VerifyCrashRestoration_OptInOnly()
    {
        string? backupPath = Environment.GetEnvironmentVariable("ALTONG_NOTIFICATION_CRASH_EXPECTED_FILE");
        if (string.IsNullOrWhiteSpace(backupPath)) Assert.Inconclusive("Crash verification is opt-in.");
        var journal = JsonSerializer.Deserialize<NotificationRecoveryJournal>(File.ReadAllText(backupPath!))!;
        var settings = new WindowsNotificationSenderSettings();
        foreach (var item in journal.Senders)
        {
            Assert.AreEqual(item.Original, settings.Read(item.Id));
            Assert.AreEqual(item.BannerRegistry, settings.ReadRegistry(item.Id, false));
            Assert.AreEqual(item.SoundRegistry, settings.ReadRegistry(item.Id, true));
        }
        Assert.IsFalse(File.Exists(NotificationSuppressionTransaction.DefaultJournalPath));
    }

    private static string GetMockId(WindowsNotificationSenderSettings settings) => settings.Enumerate().Single(r => r.IsEligible
        && r.Id.Replace('\\', '/').Equals("C:/Altong/tools/Altong.MockGenerator/bin/Debug/net9.0-windows10.0.17763.0/Altong.MockGenerator.exe", StringComparison.OrdinalIgnoreCase)).Id;
}

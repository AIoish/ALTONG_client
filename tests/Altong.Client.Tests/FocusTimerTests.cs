using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class FocusTimerTests
{
    private string _directory = null!;
    private string SettingsPath => Path.Combine(_directory, "focus-settings.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "AltongFocusTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void FirstRun_UsesDefaultsWithoutWriting()
    {
        var store = new FocusSettingsStore(SettingsPath);
        Assert.AreEqual(new FocusTimerSettings(25, 5), store.Current);
        Assert.IsFalse(File.Exists(SettingsPath));
        Assert.IsNull(store.LoadWarning);
    }

    [TestMethod]
    public void Save_ReloadsCustomDurations()
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(50, 10));
        Assert.AreEqual(new FocusTimerSettings(50, 10), new FocusSettingsStore(SettingsPath).Current);
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [DataTestMethod]
    [DataRow(0, 5)]
    [DataRow(181, 5)]
    [DataRow(25, 0)]
    [DataRow(25, 61)]
    public void InvalidSettings_DoNotReplaceSavedValues(int focus, int rest)
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(50, 10));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.Save(new(focus, rest)));
        Assert.AreEqual(new FocusTimerSettings(50, 10), store.Current);
        Assert.AreEqual(store.Current, new FocusSettingsStore(SettingsPath).Current);
    }

    [DataTestMethod]
    [DataRow("{broken")]
    [DataRow("{\"FocusMinutes\":0,\"BreakMinutes\":5}")]
    [DataRow("null")]
    public void CorruptSettings_FallBackWithoutOverwritingFile(string content)
    {
        File.WriteAllText(SettingsPath, content);
        var store = new FocusSettingsStore(SettingsPath);
        Assert.AreEqual(new FocusTimerSettings(25, 5), store.Current);
        Assert.IsNotNull(store.LoadWarning);
        Assert.AreEqual(content, File.ReadAllText(SettingsPath));
    }

    [TestMethod]
    public void FailedSave_KeepsCurrentValuesAndCleansTemporaryFile()
    {
        var store = new FocusSettingsStore(SettingsPath);
        Directory.CreateDirectory(SettingsPath); // A directory cannot be replaced by the settings file.
        try
        {
            store.Save(new(50, 10));
            Assert.Fail("Saving over a directory must fail.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert.AreEqual(new FocusTimerSettings(25, 5), store.Current);
        Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public void Routine_TransitionsAtConfiguredBoundariesAndStopsWithFocusMode()
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(2, 1));
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        int transitions = 0;
        routine.PhaseChanged += (_, _) => transitions++;

        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Idle, routine.Phase);
        mode.Start();
        routine.Refresh();
        Assert.AreEqual(TimeSpan.FromMinutes(2), routine.Remaining);
        clock.Advance(TimeSpan.FromSeconds(1));
        routine.Refresh();
        Assert.IsTrue(routine.StatusText.Contains("01:59"));
        clock.Advance(TimeSpan.FromSeconds(119));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Break, routine.Phase);
        Assert.AreEqual(TimeSpan.FromMinutes(1), routine.Remaining);
        Assert.IsTrue(mode.IsEnabled); // Timer never changes Windows-derived focus state.
        clock.Advance(TimeSpan.FromMinutes(1));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Focus, routine.Phase);
        mode.Stop();
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Idle, routine.Phase);
        Assert.AreEqual(TimeSpan.Zero, routine.Remaining);
        Assert.AreEqual(4, transitions);
    }

    [TestMethod]
    public void ChangedSettings_ApplyOnlyAfterRestart()
    {
        var store = new FocusSettingsStore(SettingsPath);
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        mode.Start();
        routine.Refresh();
        store.Save(new(50, 10));
        clock.Advance(TimeSpan.FromMinutes(25));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Break, routine.Phase);
        Assert.AreEqual(TimeSpan.FromMinutes(5), routine.Remaining);
        mode.Stop();
        routine.Refresh();
        mode.Start();
        routine.Refresh();
        Assert.AreEqual(TimeSpan.FromMinutes(50), routine.Remaining);
    }

    [TestMethod]
    public void DelayedTick_CatchesUpWithoutReplayingAllTransitions()
    {
        var store = new FocusSettingsStore(SettingsPath);
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        mode.Start();
        routine.Refresh();
        clock.Advance(TimeSpan.FromMinutes(87));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Break, routine.Phase);
        Assert.AreEqual(TimeSpan.FromMinutes(3), routine.Remaining);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}

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
        Assert.AreEqual(new FocusTimerSettings(25, 0, false), store.Current);
        Assert.IsFalse(File.Exists(SettingsPath));
        Assert.IsNull(store.LoadWarning);
    }

    [TestMethod]
    public void Save_ReloadsCustomDurations()
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(50, 10, true));
        Assert.AreEqual(new FocusTimerSettings(50, 10, true), new FocusSettingsStore(SettingsPath).Current);
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [DataTestMethod]
    [DataRow(0, 5)]
    [DataRow(181, 5)]
    [DataRow(25, -1)]
    [DataRow(25, 61)]
    public void InvalidSettings_DoNotReplaceSavedValues(int focus, int rest)
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(50, 10, true));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.Save(new(focus, rest, true)));
        Assert.AreEqual(new FocusTimerSettings(50, 10, true), store.Current);
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
        Assert.AreEqual(new FocusTimerSettings(25, 0, false), store.Current);
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
            store.Save(new(50, 10, true));
            Assert.Fail("Saving over a directory must fail.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert.AreEqual(new FocusTimerSettings(25, 0, false), store.Current);
        Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public void Routine_TransitionsAtConfiguredBoundariesAndStopsWithFocusMode()
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(2, 1, true));
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
        store.Save(new(25, 5, true));
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        mode.Start();
        routine.Refresh();
        store.Save(new(50, 10, true));
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
        store.Save(new(25, 5, true));
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

    [TestMethod]
    public void DefaultMode_HasNoTimeLimitOrBreakPhase()
    {
        var store = new FocusSettingsStore(SettingsPath);
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        mode.Start();
        routine.Refresh();
        clock.Advance(TimeSpan.FromHours(3));
        routine.Refresh();
        Assert.IsFalse(routine.UsesTimer);
        Assert.AreEqual(FocusRoutinePhase.Focus, routine.Phase);
        Assert.AreEqual(TimeSpan.Zero, routine.Remaining);
        Assert.AreEqual(TimeSpan.FromHours(3), routine.Elapsed);
        Assert.IsTrue(mode.IsEnabled);
        mode.Stop();
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Idle, routine.Phase);
        Assert.AreEqual(TimeSpan.Zero, routine.Elapsed);
    }

    [TestMethod]
    public void FocusOnlyTimer_CompletesOnceWithoutBreakOrChangingFocusMode()
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(2, 0, true));
        Assert.AreEqual(store.Current, new FocusSettingsStore(SettingsPath).Current);
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        int completions = 0;
        routine.PhaseChanged += (_, _) => { if (routine.Phase == FocusRoutinePhase.Completed) completions++; };
        mode.Start();
        routine.Refresh();
        clock.Advance(TimeSpan.FromSeconds(119));
        routine.Refresh();
        Assert.AreEqual(TimeSpan.FromSeconds(1), routine.Remaining);
        clock.Advance(TimeSpan.FromSeconds(1));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Completed, routine.Phase);
        Assert.AreEqual("집중 시간 완료 · 집중모드 켜짐", new DockRoutineStatus(routine.Phase).Label);
        Assert.AreEqual(TimeSpan.Zero, routine.Remaining);
        clock.Advance(TimeSpan.FromHours(1));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Completed, routine.Phase);
        Assert.AreEqual(1, completions);
        Assert.IsTrue(mode.IsEnabled);
        mode.Stop();
        routine.Refresh();
        mode.Start();
        routine.Refresh();
        Assert.AreEqual(TimeSpan.FromMinutes(2), routine.Remaining);
    }

    [TestMethod]
    public void LegacySettings_KeepDurationsWithTimerDisabled()
    {
        const string legacy = "{\"FocusMinutes\":50,\"BreakMinutes\":10}";
        File.WriteAllText(SettingsPath, legacy);
        var store = new FocusSettingsStore(SettingsPath);
        Assert.AreEqual(new FocusTimerSettings(50, 10, false), store.Current);
        Assert.AreEqual(legacy, File.ReadAllText(SettingsPath));
        Assert.IsNull(store.LoadWarning);
    }

    [TestMethod]
    public void DisableTimer_AppliesAtNextStartAndKeepsDurations()
    {
        var store = new FocusSettingsStore(SettingsPath);
        store.Save(new(2, 1, true));
        var mode = new FocusModeService();
        var clock = new ManualClock();
        var routine = new FocusRoutineService(mode, store, clock);
        mode.Start();
        routine.Refresh();
        store.Save(store.Current with { TimerEnabled = false });
        clock.Advance(TimeSpan.FromMinutes(2));
        routine.Refresh();
        Assert.AreEqual(FocusRoutinePhase.Break, routine.Phase);
        mode.Stop();
        routine.Refresh();
        mode.Start();
        routine.Refresh();
        clock.Advance(TimeSpan.FromMinutes(10));
        routine.Refresh();
        Assert.IsFalse(routine.UsesTimer);
        Assert.AreEqual(FocusRoutinePhase.Focus, routine.Phase);
        Assert.AreEqual(new FocusTimerSettings(2, 1, false), new FocusSettingsStore(SettingsPath).Current);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }
}

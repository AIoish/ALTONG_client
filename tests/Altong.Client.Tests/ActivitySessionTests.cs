using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ActivitySessionTests
{
    private IAltongDatabase _database = null!;
    private SqliteActivitySessionRepository _repository = null!;
    private readonly DateTime _start = new(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Setup()
    {
        _database = SqliteDatabase.CreateInMemory();
        _database.Initialize();
        _repository = new SqliteActivitySessionRepository(_database);
    }

    [TestCleanup]
    public void Cleanup() => _database.Dispose();

    [TestMethod]
    public async Task Lifecycle_StartRestoreTouchAndComplete_PersistsState()
    {
        var service = new ActivitySessionService(_repository);
        var started = await service.StartAsync(_start);

        Assert.IsTrue(service.IsRecording);
        Assert.AreEqual(_start, started.StartedAt);

        var restoredService = new ActivitySessionService(_repository);
        await restoredService.RestoreAsync(_start.AddSeconds(30));
        Assert.IsTrue(restoredService.IsRecording);
        Assert.AreEqual(started.ActivitySessionId, restoredService.Current!.ActivitySessionId);

        await restoredService.TouchIfDueAsync(_start.AddMinutes(1), TimeSpan.Zero);
        var touched = await _repository.GetByIdAsync(started.ActivitySessionId);
        Assert.AreEqual(_start.AddMinutes(1), touched!.LastSeenAt);

        var completed = await restoredService.CompleteAsync(_start.AddHours(1));
        Assert.IsNotNull(completed);
        Assert.AreEqual(ActivitySessionStatus.Completed, completed.Status);
        Assert.AreEqual(_start.AddHours(1), completed.EndedAt);
        Assert.IsFalse(restoredService.IsRecording);
        Assert.IsNull(await _repository.GetOpenAsync());
    }

    [TestMethod]
    public async Task Start_WhenAnOpenRecordExists_ReusesIt()
    {
        var first = new ActivitySessionService(_repository);
        var original = await first.StartAsync(_start);
        var second = new ActivitySessionService(_repository);

        var reused = await second.StartAsync(_start.AddMinutes(10));

        Assert.AreEqual(original.ActivitySessionId, reused.ActivitySessionId);
        Assert.AreEqual(_start, reused.StartedAt);
    }

    [TestMethod]
    public async Task FocusCapture_MultipleOnPeriods_ExcludesOffTimeAfterRestore()
    {
        var service = new ActivitySessionService(_repository);
        var activity = await service.StartAsync(_start);
        await ObserveUntilAsync(service, _start, _start.AddMinutes(5));
        await service.SetFocusCaptureAsync(true, _start.AddMinutes(5));
        await ObserveUntilAsync(service, _start.AddMinutes(5), _start.AddMinutes(15));
        await service.SetFocusCaptureAsync(false, _start.AddMinutes(15));
        await ObserveUntilAsync(service, _start.AddMinutes(15), _start.AddMinutes(30));
        await service.SetFocusCaptureAsync(true, _start.AddMinutes(30));
        await ObserveUntilAsync(service, _start.AddMinutes(30), _start.AddMinutes(35));
        await service.PauseForShutdownAsync(_start.AddMinutes(35));

        var restored = new ActivitySessionService(_repository);
        await restored.RestoreAsync(_start.AddMinutes(50));
        await ObserveUntilAsync(restored, _start.AddMinutes(50), _start.AddMinutes(55));
        await restored.SetFocusCaptureAsync(true, _start.AddMinutes(55));
        await ObserveUntilAsync(restored, _start.AddMinutes(55), _start.AddMinutes(60));
        await restored.CompleteAsync(_start.AddMinutes(60));

        Assert.AreEqual(TimeSpan.FromMinutes(20),
            await restored.GetRecordedDurationAsync(activity.ActivitySessionId));
        Assert.AreEqual(3, (await _repository.GetCaptureIntervalsAsync(
            activity.ActivitySessionId, _start.AddMinutes(60))).Count);
    }

    [TestMethod]
    public async Task ActivityElapsedTime_AdvancesWhileFocusModeIsOff()
    {
        var service = new ActivitySessionService(_repository);
        var activity = await service.StartAsync(_start);

        Assert.AreEqual(TimeSpan.FromMinutes(5), service.GetElapsedDuration(_start.AddMinutes(5)));

        await ObserveUntilAsync(service, _start, _start.AddMinutes(5));
        await service.SetFocusCaptureAsync(true, _start.AddMinutes(5));
        Assert.AreEqual(TimeSpan.FromMinutes(10), service.GetElapsedDuration(_start.AddMinutes(10)));

        await ObserveUntilAsync(service, _start.AddMinutes(5), _start.AddMinutes(10));
        await service.SetFocusCaptureAsync(false, _start.AddMinutes(10));
        Assert.AreEqual(TimeSpan.FromMinutes(15), service.GetElapsedDuration(_start.AddMinutes(15)));
        Assert.AreEqual(TimeSpan.FromMinutes(5),
            await service.GetRecordedDurationAsync(activity.ActivitySessionId));
    }

    [TestMethod]
    public async Task ActivityResult_ClipsAppUsageToOnPeriodsOnly()
    {
        var activity = new ActivitySessionService(_repository);
        var started = await activity.StartAsync(_start);
        await ObserveUntilAsync(activity, _start, _start.AddMinutes(10));
        await activity.SetFocusCaptureAsync(true, _start.AddMinutes(10));
        await ObserveUntilAsync(activity, _start.AddMinutes(10), _start.AddMinutes(20));
        await activity.SetFocusCaptureAsync(false, _start.AddMinutes(20));
        await ObserveUntilAsync(activity, _start.AddMinutes(20), _start.AddMinutes(40));
        await activity.SetFocusCaptureAsync(true, _start.AddMinutes(40));
        await ObserveUntilAsync(activity, _start.AddMinutes(40), _start.AddMinutes(50));
        await activity.CompleteAsync(_start.AddMinutes(50));

        var windows = new SqliteWindowSessionRepository(_database);
        await windows.InsertAsync(new WindowSessionRecord(0, "Code", "", _start,
            _start.AddMinutes(50), 3000, started.ActivitySessionId));
        await windows.InsertAsync(new WindowSessionRecord(0, "Unrelated", "", _start.AddMinutes(10),
            _start.AddMinutes(20), 600, "another-activity"));

        var results = new SessionResultsService(_database, new SqliteFocusSessionRepository(_database));
        var report = await results.BuildActivityResultAsync(_start, _start.AddMinutes(50),
            CurrentContext.Empty, recordedDuration: await activity.GetRecordedDurationAsync(started.ActivitySessionId),
            activitySessionId: started.ActivitySessionId);

        Assert.AreEqual(TimeSpan.FromMinutes(20), report.Duration);
        Assert.AreEqual(1, report.ApplicationCount);
        Assert.AreEqual(1200d, report.Apps[0].Seconds);
        Assert.AreEqual("Code", report.Apps[0].AppName);
    }

    [TestMethod]
    public async Task CaptureIntervals_RespectUpperBound_ForClosedAndLaterSegments()
    {
        var service = new ActivitySessionService(_repository);
        var activity = await service.StartAsync(_start);
        await service.SetFocusCaptureAsync(true, _start);
        await ObserveUntilAsync(service, _start, _start.AddMinutes(10));
        await service.SetFocusCaptureAsync(false, _start.AddMinutes(10));
        await ObserveUntilAsync(service, _start.AddMinutes(10), _start.AddMinutes(20));
        await service.SetFocusCaptureAsync(true, _start.AddMinutes(20));
        await ObserveUntilAsync(service, _start.AddMinutes(20), _start.AddMinutes(30));
        await service.SetFocusCaptureAsync(false, _start.AddMinutes(30));

        var intervals = await service.GetCaptureIntervalsAsync(activity.ActivitySessionId, _start.AddMinutes(5));
        Assert.AreEqual(1, intervals.Count);
        Assert.AreEqual(_start.AddMinutes(5), intervals[0].End);
        Assert.AreEqual(TimeSpan.FromMinutes(5),
            await _repository.GetCapturedDurationAsync(activity.ActivitySessionId, _start.AddMinutes(5)));
    }

    [TestMethod]
    public async Task LongObservationGap_IsExcludedFromBothActivityAndCaptureDuration()
    {
        var service = new ActivitySessionService(_repository);
        var activity = await service.StartAsync(_start);
        await service.SetFocusCaptureAsync(true, _start);
        await service.TouchIfDueAsync(_start.AddSeconds(5));
        await service.TouchIfDueAsync(_start.AddMinutes(30));
        await service.TouchIfDueAsync(_start.AddMinutes(30).AddSeconds(5));
        await service.CompleteAsync(_start.AddMinutes(30).AddSeconds(5));

        Assert.AreEqual(TimeSpan.FromSeconds(10), service.GetElapsedDuration(_start.AddHours(1)));
        Assert.AreEqual(TimeSpan.FromSeconds(10), await service.GetRecordedDurationAsync(activity.ActivitySessionId));
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("off")]
    [DataRow("shutdown")]
    public async Task TransitionBeforeFirstResumeHeartbeat_ExcludesTheUnobservedGap(string transition)
    {
        var service = new ActivitySessionService(_repository);
        var activity = await service.StartAsync(_start);
        await service.SetFocusCaptureAsync(true, _start);
        await service.TouchIfDueAsync(_start.AddSeconds(5));
        var resumedAt = _start.AddMinutes(30);
        switch (transition)
        {
            case "complete": await service.CompleteAsync(resumedAt); break;
            case "off": await service.SetFocusCaptureAsync(false, resumedAt); break;
            case "shutdown": await service.PauseForShutdownAsync(resumedAt); break;
        }
        Assert.AreEqual(TimeSpan.FromSeconds(5), await service.GetRecordedDurationAsync(activity.ActivitySessionId));
        Assert.AreEqual(TimeSpan.FromSeconds(5), service.GetElapsedDuration(resumedAt));
    }

    private static async Task ObserveUntilAsync(ActivitySessionService service, DateTime from, DateTime to)
    {
        for (var time = from.AddSeconds(5); time <= to; time = time.AddSeconds(5))
            await service.TouchIfDueAsync(time);
    }

    [TestMethod]
    public async Task ActivityResult_UsesOnlyTheRequestedActivityRange()
    {
        var windows = new SqliteWindowSessionRepository(_database);
        await windows.InsertAsync(new(0, "Before", "", _start.AddMinutes(-5), _start.AddMinutes(-1), 240));
        await windows.InsertAsync(new(0, "Code", "", _start.AddMinutes(-1), _start.AddMinutes(5), 360));
        await windows.InsertAsync(new(0, "Browser", "", _start.AddMinutes(7), _start.AddMinutes(15), 480));

        var results = new SessionResultsService(
            _database,
            new SqliteFocusSessionRepository(_database));
        var result = await results.BuildActivityResultAsync(
            _start,
            _start.AddMinutes(10),
            CurrentContext.Empty);

        Assert.AreEqual(2, result.ApplicationCount);
        Assert.AreEqual(300d, result.Apps.Single(app => app.AppName == "Code").Seconds);
        Assert.AreEqual(180d, result.Apps.Single(app => app.AppName == "Browser").Seconds);
        Assert.AreSame(result, results.Latest);
    }
}

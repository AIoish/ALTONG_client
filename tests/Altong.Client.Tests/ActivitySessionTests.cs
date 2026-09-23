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
        await restoredService.RestoreAsync();
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

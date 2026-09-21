using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SessionResultsTests
{
    private IAltongDatabase _database = null!;
    private SqliteNotificationRepository _notifications = null!;
    private SessionResultsService _results = null!;
    private readonly DateTime _start = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Setup()
    {
        _database = SqliteDatabase.CreateInMemory();
        _database.Initialize();
        _notifications = new SqliteNotificationRepository(_database);
        _results = new SessionResultsService(_database, new SqliteFocusSessionRepository(_database));
    }

    [TestCleanup]
    public void Cleanup() => _database.Dispose();

    [TestMethod]
    public async Task Results_AppearOnlyAfterEndAndAllClassificationsComplete()
    {
        Assert.IsNull(_results.Latest);
        await _notifications.InsertAsync(new("pending", "Slack", null, "title", "", _start.AddMinutes(1)));
        _results.Begin(_start, 25);
        await _results.RefreshAsync();
        Assert.IsNull(_results.Latest);
        _results.End(_start.AddMinutes(2), CurrentContext.Empty);
        await _results.RefreshAsync();
        Assert.IsNull(_results.Latest);
        Assert.IsTrue(_results.IsCollecting);
        await _notifications.UpdateFilterResultAsync("pending", false, 0, 0, "", null);
        await _results.RefreshAsync();
        Assert.IsNotNull(_results.Latest);
        Assert.AreEqual(1, _results.Latest.BlockedCount);
        Assert.IsFalse(_results.IsCollecting);
        _results.Begin(_start.AddMinutes(3), 25);
        Assert.IsNull(_results.Latest);
    }

    [TestMethod]
    public async Task Statistics_UseHalfOpenSessionRangeWithoutFiftyItemLimit()
    {
        await _notifications.InsertAsync(new("before", "Slack", null, "", "", _start.AddSeconds(-1), false));
        await _notifications.InsertAsync(new("end", "Slack", null, "", "", _start.AddMinutes(2), false));
        for (int i = 0; i < 65; i++)
            await _notifications.InsertAsync(new($"n{i}", "Slack", null, "", "", _start.AddSeconds(i), i % 2 == 0));
        _results.Begin(_start, 25);
        _results.End(_start.AddMinutes(2), CurrentContext.Empty);
        await _results.RefreshAsync();
        Assert.IsNotNull(_results.Latest);
        Assert.AreEqual(65, _results.Latest.NotificationCount);
        Assert.AreEqual(33, _results.Latest.PassedCount);
        Assert.AreEqual(32, _results.Latest.BlockedCount);
    }

    [TestMethod]
    public async Task AppUsage_ClipsToSessionAndDeduplicatesLiveSnapshot()
    {
        var windows = new SqliteWindowSessionRepository(_database);
        await windows.InsertAsync(new(0, "Code", "", _start.AddMinutes(-1), _start.AddMinutes(1), 120));
        await windows.InsertAsync(new(0, "Code", "", _start.AddMinutes(1), _start.AddMinutes(3), 120));
        _results.Begin(_start, 25);
        _results.End(_start.AddMinutes(2),
            new CurrentContext("Code", "", _start.AddMinutes(2), 60));
        await _results.RefreshAsync();
        Assert.IsNotNull(_results.Latest);
        Assert.AreEqual(1, _results.Latest.ApplicationCount);
        Assert.AreEqual(120d, _results.Latest.Apps[0].Seconds);
    }

    [TestMethod]
    public async Task EmptySession_HasValidZeroStatistics()
    {
        _results.Begin(_start, 25);
        _results.End(_start.AddSeconds(5), CurrentContext.Empty);
        await _results.RefreshAsync();
        Assert.IsNotNull(_results.Latest);
        Assert.AreEqual(0, _results.Latest.NotificationCount);
        Assert.AreEqual(0, _results.Latest.ApplicationCount);
    }

    [TestMethod]
    public async Task OldAggregation_CannotPublishOverNewSession()
    {
        var writes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _results.Begin(_start, 25);
        _results.End(_start.AddMinutes(1), CurrentContext.Empty, writes.Task);
        var previousRefresh = _results.RefreshAsync();
        Assert.IsNull(_results.Latest);
        _results.Begin(_start.AddMinutes(2), 25);
        writes.SetResult();
        await previousRefresh;
        Assert.IsNull(_results.Latest);
        Assert.IsFalse(_results.IsCollecting);
        _results.End(_start.AddMinutes(3), CurrentContext.Empty);
        await _results.RefreshAsync();
        Assert.AreEqual(_start.AddMinutes(2), _results.Latest!.StartedAt);
    }

    [TestMethod]
    public void NotificationLabels_DoNotTreatPendingAsBlocked()
    {
        var notification = new NotificationRecord("n", "Slack", null, "", "", _start);
        Assert.AreEqual("분류 중", new NotificationDisplayItem(notification).StatusText);
        Assert.AreEqual("차단", new NotificationDisplayItem(notification with { IsPassed = false }).StatusText);
        Assert.AreEqual("통과", new NotificationDisplayItem(notification with { IsPassed = true }).StatusText);
    }

    [TestMethod]
    public async Task FocusedAppNames_IncludeOnlyWindowsOverlappingTodaysFocusSessions()
    {
        var sessions = new SqliteFocusSessionRepository(_database);
        var windows = new SqliteWindowSessionRepository(_database);
        await sessions.StartSessionAsync(new("today", _start, _start.AddMinutes(10), IsCompleted: true));
        await windows.InsertAsync(new(0, "Code", "", _start.AddMinutes(1), _start.AddMinutes(3), 120));
        await windows.InsertAsync(new(0, "Browser", "", _start.AddMinutes(11), _start.AddMinutes(12), 60));

        var apps = await _results.ReadFocusedAppNamesAsync(_start, _start.AddDays(1));

        CollectionAssert.AreEqual(new[] { "Code" }, apps.ToArray());
    }
}

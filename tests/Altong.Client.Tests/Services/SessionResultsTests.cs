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
    public async Task FocusModeOff_CreatesSeparateReportsWithoutActivityRecording()
    {
        var windows = new SqliteWindowSessionRepository(_database);
        _results.Begin(_start, 25);
        string firstId = _results.CurrentSessionId!;
        await _notifications.InsertAsync(new("first-blocked", "Messenger", "동료",
            "확인 요청", "내용", _start.AddSeconds(10), false, SessionId: firstId));
        await windows.InsertAsync(new(0, "Editor.exe", "", _start, _start.AddMinutes(1), 60, firstId));

        var first = await _results.CompleteFocusSessionAsync(_start.AddMinutes(1));

        Assert.IsNotNull(first);
        Assert.IsTrue(first.IsFocusSessionReport);
        Assert.AreEqual(1, first.BlockedCount);
        Assert.AreEqual(1, first.ApplicationCount);
        Assert.AreEqual("Editor.exe", first.Apps[0].AppName);
        Assert.AreEqual(1, first.QuickReplies.Count);
        StringAssert.Contains(first.NotificationSummary, "차단 1개");
        Assert.AreEqual(1, first.NotificationHighlights.Count);

        _results.Begin(_start.AddMinutes(2), 25);
        string secondId = _results.CurrentSessionId!;
        await _notifications.InsertAsync(new("second-passed", "Calendar", null,
            "회의", "내용", _start.AddMinutes(2).AddSeconds(10), true, SessionId: secondId));

        var second = await _results.CompleteFocusSessionAsync(_start.AddMinutes(3));

        Assert.IsNotNull(second);
        Assert.AreEqual(1, second.NotificationCount);
        Assert.AreEqual(0, second.BlockedCount);
        Assert.AreEqual(0, second.ApplicationCount);
        Assert.AreEqual(0, second.QuickReplies.Count);
        Assert.AreEqual(_start.AddMinutes(2), second.StartedAt);
    }

    [TestMethod]
    public async Task FocusModeOff_ReportSurvivesAnImmediateNextFocusSession()
    {
        _results.Begin(_start, 25);
        var pendingWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReport = _results.CompleteFocusSessionAsync(_start.AddMinutes(1), pendingWrite.Task);
        _results.Begin(_start.AddMinutes(2), 25);
        pendingWrite.SetResult();

        var first = await firstReport;

        Assert.IsNotNull(first);
        Assert.AreEqual(_start, first.StartedAt);
        Assert.IsNull(_results.Latest, "이전 결과가 새 세션의 최신 결과를 덮어쓰면 안 됩니다.");
        var second = await _results.CompleteFocusSessionAsync(_start.AddMinutes(3));
        Assert.AreEqual(_start.AddMinutes(2), second!.StartedAt);
    }

    [TestMethod]
    public async Task FocusModeOff_NotificationFailureKeepsStoredDataAndPersistsTheWarning()
    {
        _results.Begin(_start, 25);
        string focusId = _results.CurrentSessionId!;
        await _notifications.InsertAsync(new("saved-before-failure", "Slack", null,
            "가상 알림", "", _start.AddSeconds(10), false, SessionId: focusId));
        var report = await _results.CompleteFocusSessionAsync(_start.AddMinutes(1),
            notificationWrites: Task.FromException(new IOException("synthetic notification failure")));
        Assert.IsNotNull(report);
        Assert.AreEqual(1, report.NotificationCount);
        StringAssert.Contains(report.NotificationSummary, "일부 알림 처리가 실패했습니다");
        Assert.IsTrue((await new SqliteFocusSessionRepository(_database).GetByIdAsync(focusId))!.IsCompleted);
        await _results.Reports.SaveAsync(report);
        Assert.AreEqual(report.NotificationSummary, (await _results.Reports.GetSnapshotAsync(focusId))!.NotificationSummary);
    }

    [TestMethod]
    public void PendingSave_WaitsForEveryCompletedSessionWithoutPumpingTheUi()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try
            {
                using var database = SqliteDatabase.CreateInMemory();
                database.Initialize();
                var sessions = new SqliteFocusSessionRepository(database);
                var results = new SessionResultsService(database, sessions);
                var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                results.Begin(_start, 0);
                string firstId = results.CurrentSessionId!;
                var first = results.CompleteFocusSessionAsync(_start.AddMinutes(1), notificationWrites: firstGate.Task);
                results.Begin(_start.AddMinutes(2), 0);
                string secondId = results.CurrentSessionId!;
                var second = results.CompleteFocusSessionAsync(_start.AddMinutes(3), notificationWrites: secondGate.Task);
                Assert.IsNull(results.ActiveSession);
                var pending = results.PendingSave;
                Assert.IsFalse(pending.IsCompleted, "빠른 ON/OFF 후에도 이전 세션의 저장 작업을 추적합니다.");
                firstGate.SetResult();
                Assert.IsFalse(pending.IsCompleted, "두 번째 세션의 저장도 기다립니다.");
                secondGate.SetResult();
                pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                Assert.IsTrue(sessions.GetByIdAsync(firstId).GetAwaiter().GetResult()!.IsCompleted);
                Assert.IsTrue(sessions.GetByIdAsync(secondId).GetAwaiter().GetResult()!.IsCompleted);
                Assert.IsFalse(first.IsCompleted || second.IsCompleted,
                    "앱 종료는 UI에 결과를 표시하는 후속 처리를 기다리지 않습니다.");
            }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "UI 스레드에서 종료 저장 대기가 멈추면 안 됩니다.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    [TestMethod]
    public async Task FocusModeOff_StillShowsNotificationsWhenAnAppWriteFails()
    {
        _results.Begin(_start, 25);
        string focusId = _results.CurrentSessionId!;
        await _notifications.InsertAsync(new("saved-notification", "Slack", null,
            "확인 요청", "", _start.AddSeconds(10), false, SessionId: focusId));

        var report = await _results.CompleteFocusSessionAsync(_start.AddMinutes(1),
            Task.FromException(new IOException("app write failed")));

        Assert.IsNotNull(report);
        Assert.AreEqual(1, report.NotificationCount);
        Assert.AreEqual(1, report.QuickReplies.Count);
    }

    [TestMethod]
    public async Task Journal_ReadsSavedWindowsInReverseOrder_AndClipsRange()
    {
        var windows = new SqliteWindowSessionRepository(_database);
        await windows.InsertAsync(new(0, "Code", "이전 작업", _start.AddMinutes(-1), _start.AddMinutes(1), 120));
        await windows.InsertAsync(new(0, "Browser", "현재 작업", _start.AddMinutes(1), _start.AddMinutes(3), 120));
        await windows.InsertAsync(new(0, "Excluded", "", _start.AddMinutes(-2), _start, 120));
        var entries = await _results.ReadActivityJournalAsync(_start, _start.AddMinutes(2));
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual("Browser", entries[0].AppName);
        Assert.AreEqual("현재 작업", entries[0].WindowTitle);
        Assert.AreEqual(_start.AddMinutes(2), entries[0].EndedAt);
        Assert.AreEqual(_start, entries[1].StartedAt);
        Assert.AreEqual("1분 0초", entries[1].DurationText);
    }

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
    public async Task ClearResult_InvalidatesAnInFlightFocusReport()
    {
        var writes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _results.Begin(_start, 25);
        _results.End(_start.AddMinutes(1), CurrentContext.Empty, writes.Task);
        var refresh = _results.RefreshAsync();
        _results.ClearResult();
        writes.SetResult();
        await refresh;
        Assert.IsNull(_results.Latest);
        Assert.IsFalse(_results.IsCollecting);
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

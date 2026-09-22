using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationPipelineCoordinatorTests
{
    private IAltongDatabase _database = null!;
    private INotificationRepository _notificationRepo = null!;
    private FakeNotificationListener _fakeListener = null!;
    private FakeActiveWindowTracker _fakeTracker = null!;
    private RuleBasedFilterEngine _filterEngine = null!;

    [TestInitialize]
    public void Setup()
    {
        _database = SqliteDatabase.CreateInMemory();
        _database.Initialize();
        _notificationRepo = new SqliteNotificationRepository(_database);
        _fakeListener = new FakeNotificationListener();
        _fakeTracker = new FakeActiveWindowTracker();
        _filterEngine = new RuleBasedFilterEngine();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _fakeListener.Dispose();
        _database.Dispose();
    }

    [TestMethod]
    public async Task NotificationPipeline_WhenFocusModeOff_InsertsRecordWithoutPassedFlag()
    {
        bool isFocusMode = false;
        string? sessionId = null;

        using var coordinator = new NotificationPipelineCoordinator(
            _fakeListener,
            _fakeTracker,
            _notificationRepo,
            _filterEngine,
            () => isFocusMode,
            () => sessionId);

        await coordinator.StartAsync();

        var noti = new RawNotification(
            "noti_test_01", "Slack", "김철수", "일반 회의 알림", "내일 회의 시간 확인", DateTime.UtcNow);

        NotificationRecord? processedRecord = null;
        var tcs = new TaskCompletionSource<NotificationRecord>();
        coordinator.NotificationProcessed += (_, r) =>
        {
            processedRecord = r;
            tcs.TrySetResult(r);
        };

        _fakeListener.EmitNotification(noti);

        // 비동기 파이프라인 완료 대기
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.AreEqual(tcs.Task, completed, "파이프라인이 2초 이내에 처리를 완료해야 합니다.");

        Assert.IsNotNull(processedRecord);
        Assert.AreEqual("noti_test_01", processedRecord.Id);
        Assert.IsNull(processedRecord.IsPassed, "집중 모드 OFF일 때는 IsPassed가 null(일반 기록)이어야 합니다.");
        Assert.IsNull(processedRecord.SessionId);

        // DB 검증
        var fromDb = await _notificationRepo.GetByIdAsync("noti_test_01");
        Assert.IsNotNull(fromDb);
        Assert.AreEqual("Slack", fromDb.AppName);
        Assert.AreEqual("일반 회의 알림", fromDb.Title);
        Assert.IsNull(fromDb.IsPassed);
    }

    [TestMethod]
    public async Task NotificationPipeline_WhenFocusModeOn_UrgentNotification_SetsPassedAndSessionId()
    {
        bool isFocusMode = true;
        string? sessionId = "sess_focus_999";

        _fakeTracker.CurrentContext = new CurrentContext(
            ActiveProcess: "Code.exe",
            WindowTitle: "Altong.Client - Visual Studio Code",
            LastUpdated: DateTime.UtcNow,
            DurationSeconds: 120);

        using var coordinator = new NotificationPipelineCoordinator(
            _fakeListener,
            _fakeTracker,
            _notificationRepo,
            _filterEngine,
            () => isFocusMode,
            () => sessionId);

        await coordinator.StartAsync();

        var noti = new RawNotification(
            "noti_urgent_01", "Slack", "팀장님", "[긴급] 프로덕션 서버 장애 발생", "즉시 확인 필요", DateTime.UtcNow);

        var tcs = new TaskCompletionSource<NotificationRecord>();
        coordinator.NotificationProcessed += (_, r) => tcs.TrySetResult(r);

        _fakeListener.EmitNotification(noti);

        await Task.WhenAny(tcs.Task, Task.Delay(2000));
        var record = await tcs.Task;

        Assert.AreEqual("noti_urgent_01", record.Id);
        Assert.AreEqual(true, record.IsPassed, "긴급 알림은 집중 모드 중에도 IsPassed == true여야 합니다.");
        Assert.AreEqual(5, record.UrgencyScore);
        Assert.AreEqual("sess_focus_999", record.SessionId);

        // DB 영속성 검증
        var fromDb = await _notificationRepo.GetByIdAsync("noti_urgent_01");
        Assert.IsNotNull(fromDb);
        Assert.AreEqual(true, fromDb.IsPassed);
        Assert.AreEqual("sess_focus_999", fromDb.SessionId);
    }

    [TestMethod]
    public async Task NotificationPipeline_WhenFocusModeOn_CasualNotification_SetsBlockedAndSessionId()
    {
        bool isFocusMode = true;
        string? sessionId = "sess_focus_999";

        _fakeTracker.CurrentContext = new CurrentContext("Code.exe", "Main.cs", DateTime.UtcNow);

        using var coordinator = new NotificationPipelineCoordinator(
            _fakeListener,
            _fakeTracker,
            _notificationRepo,
            _filterEngine,
            () => isFocusMode,
            () => sessionId);

        await coordinator.StartAsync();

        var noti = new RawNotification(
            "noti_casual_01", "Coupang", "", "[특가] 오늘만 50% 할인 쿠폰 도착!", "쿠폰 받기", DateTime.UtcNow);

        var tcs = new TaskCompletionSource<NotificationRecord>();
        coordinator.NotificationProcessed += (_, r) => tcs.TrySetResult(r);

        _fakeListener.EmitNotification(noti);

        await Task.WhenAny(tcs.Task, Task.Delay(2000));
        var record = await tcs.Task;

        Assert.AreEqual("noti_casual_01", record.Id);
        Assert.AreEqual(false, record.IsPassed, "쇼핑/할인 알림은 집중 모드 중에 IsPassed == false(차단)여야 합니다.");
        Assert.AreEqual(1, record.UrgencyScore);
        Assert.AreEqual("sess_focus_999", record.SessionId);

        // DB 영속성 검증
        var fromDb = await _notificationRepo.GetByIdAsync("noti_casual_01");
        Assert.IsNotNull(fromDb);
        Assert.AreEqual(false, fromDb.IsPassed);
        Assert.AreEqual("sess_focus_999", fromDb.SessionId);
    }

    private sealed class FakeActiveWindowTracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext { get; set; } = CurrentContext.Empty;
#pragma warning disable CS0067
        public event EventHandler<CurrentContext>? ContextChanged;
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;
#pragma warning restore CS0067
        public void Start() { }
        public void Stop() { }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Dispose() { }
    }
}

using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationPipelineCoordinatorTests
{
    [TestMethod]
    public async Task DrainSession_WaitsForOldSessionOnly_AndIncludesItsDelayedNotification()
    {
        bool enabled = true;
        string id = "old-session";
        var filter = new SessionDrainFilterEngine();
        using var coordinator = new NotificationPipelineCoordinator(_fakeListener, _fakeTracker,
            _notificationRepo, filter, () => enabled, () => id);
        await coordinator.StartAsync();
        _fakeListener.EmitNotification(new("delayed-old", "Messenger", "", "가상 알림", "", DateTime.UtcNow));
        enabled = false;
        var drain = coordinator.DrainSessionAsync(id);
        Assert.IsFalse(drain.IsCompleted);
        enabled = true;
        id = "new-session";
        _fakeListener.EmitNotification(new("delayed-new", "Messenger", "", "가상 알림", "", DateTime.UtcNow));
        filter.Old.SetResult(new("delayed-old", false, 1, 1, "fake"));
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("old-session", (await _notificationRepo.GetByIdAsync("delayed-old"))!.SessionId);
        Assert.IsNull(await _notificationRepo.GetByIdAsync("delayed-new"));
        enabled = false;
        var newDrain = coordinator.DrainSessionAsync(id);
        filter.New.SetResult(new("delayed-new", true, 1, 1, "fake"));
        await newDrain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("new-session", (await _notificationRepo.GetByIdAsync("delayed-new"))!.SessionId);
    }

    private sealed class SessionDrainFilterEngine : IFilterEngine
    {
        public TaskCompletionSource<FilterResult> Old { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<FilterResult> New { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FilterResult> EvaluateAsync(RawNotification notification, CurrentContext context) =>
            notification.Id == "delayed-old" ? Old.Task : New.Task;
    }

    [TestMethod]
    public async Task DrainSession_UiSubscriberFailureDoesNotInvalidateTheStoredNotification()
    {
        using var coordinator = new NotificationPipelineCoordinator(_fakeListener, _fakeTracker,
            _notificationRepo, _filterEngine, () => true, () => "session-ui-failure");
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.NotificationProcessed += (_, _) =>
        {
            delivered.SetResult();
            throw new InvalidOperationException("synthetic UI failure");
        };
        await coordinator.StartAsync();
        _fakeListener.EmitNotification(new("saved-ui-failure", "Messenger", "", "가상 알림", "", DateTime.UtcNow));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DrainSessionAsync("session-ui-failure").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(await _notificationRepo.GetByIdAsync("saved-ui-failure"));
    }
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

        // DB 검증 (집중 모드/활동 캡처 세션 외부의 알림은 DB 트리거 정책에 의해 저장되지 않고 무시됨)
        var fromDb = await _notificationRepo.GetByIdAsync("noti_test_01");
        Assert.IsNull(fromDb, "집중 모드 및 활동 세션 외부에서 수신된 알림은 DB 트리거 정책에 따라 저장되지 않아야 합니다.");
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

    [TestMethod]
    public async Task NotificationPipeline_WithKakaoInterceptor_EndToEnd_HidesAndClosesBlockedKakaoNotification()
    {
        bool isFocusMode = true;
        string? sessionId = "sess_kakao_01";
        nint kakaoHwnd = 112233;

        var fakeOp = new FakeKakaoWindowOperator();
        fakeOp.NotificationWindows.Add(kakaoHwnd);
        fakeOp.TextExtractor = _ => new KakaoNotificationText("친구", "친구", "야 주말에 뭐하냐 롤이나 하자");

        using var kakaoInterceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);
        using var compositeListener = new CompositeNotificationListener(kakaoInterceptor);

        using var coordinator = new NotificationPipelineCoordinator(
            compositeListener,
            _fakeTracker,
            _notificationRepo,
            _filterEngine,
            () => isFocusMode,
            () => sessionId);

        coordinator.NotificationProcessed += (_, r) => kakaoInterceptor.OnNotificationProcessed(r);

        await coordinator.StartAsync();

        var tcs = new TaskCompletionSource<NotificationRecord>();
        coordinator.NotificationProcessed += (_, r) => tcs.TrySetResult(r);

        // 카카오톡 알림 팝업 창 등장 (EVENT_OBJECT_SHOW 시뮬레이션)
        kakaoInterceptor.HandleWindowShowEvent(kakaoHwnd);

        // 1. 즉시 숨김(SW_HIDE) 검증
        Assert.IsTrue(fakeOp.HiddenWindows.Contains(kakaoHwnd), "포착 즉시 스텔스 숨김 처리되어야 합니다.");

        // 2. 파이프라인 처리 완료 대기
        var record = await tcs.Task;
        Assert.AreEqual(false, record.IsPassed, "잡담 알림은 집중 모드 중 차단되어야 합니다.");

        // 3. 차단 후 완전 소멸(WM_CLOSE) 검증
        Assert.IsTrue(fakeOp.ClosedWindows.Contains(kakaoHwnd), "차단 판정 후 윈도우가 조용히 닫혀야 합니다.");
        Assert.AreEqual(0, fakeOp.ShownWindows.Count, "차단 알림은 다시 표시되지 않아야 합니다.");
    }

    [TestMethod]
    public async Task NotificationPipeline_WithKakaoInterceptor_EndToEnd_ClosesUrgentPopupAndDeliversContent()
    {
        bool isFocusMode = true;
        string? sessionId = "sess_kakao_02";
        nint kakaoHwnd = 445566;

        var fakeOp = new FakeKakaoWindowOperator();
        fakeOp.NotificationWindows.Add(kakaoHwnd);
        fakeOp.TextExtractor = _ => new KakaoNotificationText("교수님", "교수님", "[긴급] 프로젝트 피드백 확인 바랍니다.");

        using var kakaoInterceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);
        using var compositeListener = new CompositeNotificationListener(kakaoInterceptor);

        using var coordinator = new NotificationPipelineCoordinator(
            compositeListener,
            _fakeTracker,
            _notificationRepo,
            _filterEngine,
            () => isFocusMode,
            () => sessionId);

        coordinator.NotificationProcessed += (_, r) => kakaoInterceptor.OnNotificationProcessed(r);

        await coordinator.StartAsync();

        var tcs = new TaskCompletionSource<NotificationRecord>();
        coordinator.NotificationProcessed += (_, r) => tcs.TrySetResult(r);

        // 카카오톡 알림 팝업 창 등장
        kakaoInterceptor.HandleWindowShowEvent(kakaoHwnd);

        // 1. 즉시 숨김(SW_HIDE)
        Assert.IsTrue(fakeOp.HiddenWindows.Contains(kakaoHwnd));

        // 2. 파이프라인 처리 완료 대기
        var record = await tcs.Task;
        Assert.AreEqual(true, record.IsPassed, "교수님 긴급 알림은 통과되어야 합니다.");

        // 3. 원래 팝업은 닫되, 미니바에 필요한 원문과 판정은 전달한다.
        Assert.IsTrue(fakeOp.ClosedWindows.Contains(kakaoHwnd));
        Assert.AreEqual(0, fakeOp.ShownWindows.Count);
        Assert.AreEqual("교수님", record.Sender);
        Assert.AreEqual("[긴급] 프로젝트 피드백 확인 바랍니다.", record.Body);
        Assert.AreEqual("sess_kakao_02", record.SessionId);
    }

    [TestMethod]
    public async Task DelayedVerdict_KeepsOriginalSession_AndDockRejectsItAfterNextSessionStarts()
    {
        string currentSession = "old-session";
        var filter = new DelayedFilterEngine();
        var dock = new DockNotificationState();
        dock.BeginSession(currentSession);
        using var coordinator = new NotificationPipelineCoordinator(_fakeListener, _fakeTracker,
            _notificationRepo, filter, () => true, () => currentSession);
        var completion = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.NotificationProcessed += (_, record) => completion.TrySetResult(record);
        await coordinator.StartAsync();
        _fakeListener.EmitNotification(new RawNotification("delayed", "Slack", "팀원", "긴급", "원본 내용", DateTime.UtcNow));

        dock.EndSession();
        currentSession = "new-session";
        dock.BeginSession(currentSession);
        filter.Completion.SetResult(new FilterResult("delayed", true, 5, 4, "긴급 업무"));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual("old-session", result.SessionId);
        Assert.IsFalse(dock.Receive(result));
        Assert.AreEqual(0, dock.Items.Count);
        Assert.IsNotNull(await _notificationRepo.GetByIdAsync("delayed"), "늦은 결과도 DB 기록은 보존합니다.");
    }

    private sealed class DelayedFilterEngine : IFilterEngine
    {
        public TaskCompletionSource<FilterResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<FilterResult> EvaluateAsync(RawNotification notification, CurrentContext context) => Completion.Task;
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

using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.Data;

/// <summary>
/// 로컬 SQLite 데이터베이스 및 Repository 단위 테스트.
/// 인메모리 DB(Data Source=:memory:)를 사용하여 완벽히 격리된 환경에서 검증합니다.
/// </summary>
[TestClass]
public class SqliteDatabaseTests
{
    private IAltongDatabase _database = null!;
    private INotificationRepository _notificationRepo = null!;
    private IWindowSessionRepository _windowSessionRepo = null!;
    private IFocusSessionRepository _focusSessionRepo = null!;

    [TestInitialize]
    public void Setup()
    {
        _database = SqliteDatabase.CreateInMemory();
        _database.Initialize();

        _notificationRepo = new SqliteNotificationRepository(_database);
        _windowSessionRepo = new SqliteWindowSessionRepository(_database);
        _focusSessionRepo = new SqliteFocusSessionRepository(_database);

    }

    [TestCleanup]
    public void Cleanup()
    {
        _database.Dispose();
    }

    [TestMethod]
    public async Task NotificationRepository_InsertAndGetById_PreservesAllFieldsIncludingRelevanceScore()
    {
        // Arrange: README §7.1 및 §7.3에 정의된 모든 데이터 필드 준비
        var now = DateTime.UtcNow;
        var record = new NotificationRecord(
            Id: "noti_20260920_001",
            AppName: "Slack",
            Sender: "김철수 팀장",
            Title: "[긴급] 서버 배포 오류",
            Body: "지금 102번 서버 에러 긴급 핫픽스 부탁드립니다.",
            ReceivedAt: now,
            IsPassed: true,
            UrgencyScore: 5,
            RelevanceScore: 4, // ⭐ 맥락 연관도 점수 검증
            Category: "긴급 업무",
            AiSummaryReason: "백엔드 코드 작성 중 발생한 서버 장애 알림",
            SessionId: "session_001");

        // Act
        await _notificationRepo.InsertAsync(record);
        var retrieved = await _notificationRepo.GetByIdAsync("noti_20260920_001");

        // Assert
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("noti_20260920_001", retrieved.Id);
        Assert.AreEqual("Slack", retrieved.AppName);
        Assert.AreEqual("김철수 팀장", retrieved.Sender);
        Assert.AreEqual("[긴급] 서버 배포 오류", retrieved.Title);
        Assert.AreEqual("지금 102번 서버 에러 긴급 핫픽스 부탁드립니다.", retrieved.Body);
        Assert.IsTrue(retrieved.IsPassed);
        Assert.AreEqual(5, retrieved.UrgencyScore);
        Assert.AreEqual(4, retrieved.RelevanceScore); // ⭐ 1:1 대조 통과 확인
        Assert.AreEqual("긴급 업무", retrieved.Category);
        Assert.AreEqual("백엔드 코드 작성 중 발생한 서버 장애 알림", retrieved.AiSummaryReason);
        Assert.AreEqual("session_001", retrieved.SessionId);
    }

    [TestMethod]
    public async Task NotificationRepository_DoesNotRequireActivityRecording()
    {
        await _notificationRepo.InsertAsync(new NotificationRecord(
            "focus-notification", "Slack", null, "집중모드 ON", "", DateTime.UtcNow,
            SessionId: "focus-session"));

        Assert.IsNotNull(await _notificationRepo.GetByIdAsync("focus-notification"));
    }

    [TestMethod]
    public async Task Initialize_RemovesLegacyActivityCaptureTrigger_WithoutDeletingNotifications()
    {
        using (var connection = _database.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER trg_notifications_focus_capture
                BEFORE INSERT ON notifications
                BEGIN SELECT RAISE(IGNORE); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        _database.Initialize();
        await _notificationRepo.InsertAsync(new NotificationRecord(
            "after-migration", "Slack", null, "알림", "", DateTime.UtcNow,
            SessionId: "focus-session"));

        Assert.IsNotNull(await _notificationRepo.GetByIdAsync("after-migration"));
    }

    [TestMethod]
    public async Task Initialize_PreservesExistingLegacyActivityTables_AndSharedRecords()
    {
        using (var connection = _database.CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE activity_sessions (activity_session_id TEXT PRIMARY KEY);
                CREATE TABLE activity_session_segments (
                    segment_id TEXT PRIMARY KEY,
                    activity_session_id TEXT REFERENCES activity_sessions(activity_session_id));
                CREATE TABLE activity_capture_policy (
                    activity_session_id TEXT PRIMARY KEY REFERENCES activity_sessions(activity_session_id));
                CREATE TABLE activity_capture_segments (
                    segment_id TEXT PRIMARY KEY,
                    activity_session_id TEXT REFERENCES activity_sessions(activity_session_id));
                INSERT INTO activity_sessions VALUES ('legacy-activity');
                INSERT INTO activity_session_segments VALUES ('segment', 'legacy-activity');
                INSERT INTO activity_capture_policy VALUES ('legacy-activity');
                INSERT INTO activity_capture_segments VALUES ('capture', 'legacy-activity');
                INSERT INTO window_sessions (process_name, window_title, started_at, ended_at,
                    duration_seconds, session_id)
                VALUES ('Editor.exe', '', '2026-09-30T10:00:00Z', '2026-09-30T10:01:00Z', 60,
                    'legacy-activity');
                """;
            await command.ExecuteNonQueryAsync();
        }
        await _notificationRepo.InsertAsync(new NotificationRecord(
            "shared-notification", "Slack", null, "title", "body", DateTime.UtcNow));

        _database.Initialize();

        using var verify = _database.CreateOpenConnection();
        using var count = verify.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                'activity_sessions', 'activity_session_segments',
                'activity_capture_policy', 'activity_capture_segments');
            """;
        Assert.AreEqual(4L, (long)count.ExecuteScalar()!);
        foreach (string table in new[] { "activity_sessions", "activity_session_segments", "activity_capture_policy", "activity_capture_segments" })
        {
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.AreEqual(1L, (long)count.ExecuteScalar()!);
        }
        count.CommandText = "SELECT COUNT(*) FROM window_sessions WHERE session_id = 'legacy-activity';";
        Assert.AreEqual(1L, (long)count.ExecuteScalar()!);
        Assert.IsNotNull(await _notificationRepo.GetByIdAsync("shared-notification"));

        // Repeated initialization must also preserve the dormant legacy data.
        _database.Initialize();
        count.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'activity_sessions';";
        Assert.AreEqual(1L, (long)count.ExecuteScalar()!);
    }

    [TestMethod]
    public void Initialize_DoesNotCreateLegacyActivityTables()
    {
        _database.Initialize();
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                'activity_sessions', 'activity_session_segments',
                'activity_capture_policy', 'activity_capture_segments');
            """;
        Assert.AreEqual(0L, (long)command.ExecuteScalar()!);
    }

    [TestMethod]
    public async Task NotificationRepository_UpdateFilterResult_UpdatesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var initial = new NotificationRecord(
            Id: "noti_20260920_002",
            AppName: "KakaoTalk",
            Sender: "친구",
            Title: "저녁 약속",
            Body: "삼겹살 먹으러 가자",
            ReceivedAt: now);

        await _notificationRepo.InsertAsync(initial);

        // Act: AI 실시간 필터 판단 결과 업데이트
        await _notificationRepo.UpdateFilterResultAsync(
            "noti_20260920_002",
            isPassed: false,
            urgencyScore: 1,
            relevanceScore: 1,
            category: "잡담",
            reason: "업무와 무관한 일상 대화로 무음 차단");

        var updated = await _notificationRepo.GetByIdAsync("noti_20260920_002");

        // Assert
        Assert.IsNotNull(updated);
        Assert.IsFalse(updated.IsPassed);
        Assert.AreEqual(1, updated.UrgencyScore);
        Assert.AreEqual(1, updated.RelevanceScore);
        Assert.AreEqual("잡담", updated.Category);
        Assert.AreEqual("업무와 무관한 일상 대화로 무음 차단", updated.AiSummaryReason);
    }

    [TestMethod]
    public async Task NotificationRepository_GetBlockedNotifications_ReturnsOnlyMutedOnes()
    {
        // Arrange
        var now = DateTime.UtcNow;
        await _notificationRepo.InsertAsync(new NotificationRecord("n1", "Slack", "A", "T1", "B1", now, IsPassed: true));
        await _notificationRepo.InsertAsync(new NotificationRecord("n2", "KakaoTalk", "B", "T2", "B2", now.AddMinutes(1), IsPassed: false, SessionId: "s1"));
        await _notificationRepo.InsertAsync(new NotificationRecord("n3", "YouTube", "C", "T3", "B3", now.AddMinutes(2), IsPassed: false, SessionId: "s1"));

        // Act
        var blockedInSession = await _notificationRepo.GetBlockedNotificationsAsync("s1");
        var allBlocked = await _notificationRepo.GetBlockedNotificationsAsync();

        // Assert
        Assert.AreEqual(2, blockedInSession.Count);
        Assert.AreEqual(2, allBlocked.Count);
        foreach (var n in blockedInSession)
        {
            Assert.IsFalse(n.IsPassed);
        }
    }

    [TestMethod]
    public async Task WindowSessionRepository_InsertAndAggregate_CalculatesDwellTimeCorrectly()
    {
        // Arrange: ActiveWindowTracker 세션 데이터 시뮬레이션
        var baseTime = DateTime.UtcNow.Date.AddHours(9); // 오늘 오전 9시

        var session1 = new WindowSessionRecord(0, "Code.exe", "Controller.cs", baseTime, baseTime.AddMinutes(30), 1800);
        var session2 = new WindowSessionRecord(0, "chrome.exe", "Stack Overflow", baseTime.AddMinutes(30), baseTime.AddMinutes(45), 900);
        var session3 = new WindowSessionRecord(0, "Code.exe", "Service.cs", baseTime.AddMinutes(45), baseTime.AddHours(2), 4500);

        // Act
        await _windowSessionRepo.InsertAsync(session1);
        await _windowSessionRepo.InsertAsync(session2);
        await _windowSessionRepo.InsertAsync(session3);

        var sessions = await _windowSessionRepo.GetSessionsByDateRangeAsync(baseTime, baseTime.AddHours(3));
        int totalSeconds = await _windowSessionRepo.GetTotalDwellTimeSecondsAsync(baseTime, baseTime.AddHours(3));
        var appSummary = await _windowSessionRepo.GetAppDwellTimeSummaryAsync(baseTime, baseTime.AddHours(3));

        // Assert (TIL 업무 일지 집계 쿼리 검증)
        Assert.AreEqual(3, sessions.Count);
        Assert.AreEqual(1800 + 900 + 4500, totalSeconds); // 총 7200초 = 2시간

        Assert.AreEqual(2, appSummary.Count);
        Assert.AreEqual(1800 + 4500, appSummary["Code.exe"]); // Code.exe 총 6300초
        Assert.AreEqual(900, appSummary["chrome.exe"]);        // chrome.exe 총 900초
    }

    [TestMethod]
    public async Task FocusSessionRepository_Lifecycle_ManagesSessionProperly()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var session = new FocusSessionRecord("session_123", now, TargetDurationMinutes: 25);

        // Act: 세션 시작
        await _focusSessionRepo.StartSessionAsync(session);
        var active = await _focusSessionRepo.GetActiveSessionAsync();
        Assert.IsNotNull(active);
        Assert.AreEqual("session_123", active.SessionId);
        Assert.AreEqual(0, active.BlockedCount);

        // Act: 방해 알림 차단 카운트 증가 2회
        await _focusSessionRepo.IncrementBlockedCountAsync("session_123");
        await _focusSessionRepo.IncrementBlockedCountAsync("session_123");

        // Act: 세션 정상 종료
        await _focusSessionRepo.EndSessionAsync("session_123", now.AddMinutes(25), isCompleted: true);

        var ended = await _focusSessionRepo.GetByIdAsync("session_123");
        var noActive = await _focusSessionRepo.GetActiveSessionAsync();

        // Assert
        Assert.IsNotNull(ended);
        Assert.IsTrue(ended.IsCompleted);
        Assert.AreEqual(2, ended.BlockedCount);
        Assert.IsNotNull(ended.EndedAt);
        Assert.IsNull(noActive); // 더 이상 진행 중인 세션 없음
    }
}

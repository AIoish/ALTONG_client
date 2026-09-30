using System.IO;
using Microsoft.Data.Sqlite;

namespace Altong.Client.Data;

/// <summary>
/// Microsoft.Data.Sqlite 기반의 로컬 SQLite 데이터베이스 구현체.
/// WAL 모드 활성화로 다중 프로세스(C# 및 Python AI) 동시 접근 시 락을 방지하며,
/// 로컬 AppData 디렉토리에 altong.db 파일을 자동 생성 및 관리합니다.
/// </summary>
public sealed class SqliteDatabase : IAltongDatabase
{
    private readonly string _connectionString;
    private SqliteConnection? _keepAliveConnection;

    public SqliteDatabase(string? customConnectionString = null)
    {
        if (!string.IsNullOrWhiteSpace(customConnectionString))
        {
            _connectionString = customConnectionString;
            if (_connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                _connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
            {
                _keepAliveConnection = new SqliteConnection(_connectionString);
                _keepAliveConnection.Open();
            }
            return;
        }

        string appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong");

        Directory.CreateDirectory(appDataPath);
        string dbPath = Path.Combine(appDataPath, "altong.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public static SqliteDatabase CreateInMemory()
    {
        return new SqliteDatabase($"Data Source=InMemoryDb_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    }

    public SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            -- 1. 알림 및 AI 실시간 필터 판별 결과 테이블
            CREATE TABLE IF NOT EXISTS notifications (
                id TEXT PRIMARY KEY,
                app_name TEXT NOT NULL,
                sender TEXT,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                received_at TEXT NOT NULL,
                is_passed INTEGER,
                urgency_score INTEGER,
                relevance_score INTEGER,
                category TEXT,
                ai_summary_reason TEXT,
                session_id TEXT,
                created_at TEXT DEFAULT (datetime('now', 'utc'))
            );

            CREATE INDEX IF NOT EXISTS idx_notifications_received_at ON notifications(received_at);
            CREATE INDEX IF NOT EXISTS idx_notifications_is_passed ON notifications(is_passed);

            -- 2. 활성 창 작업 세션 로그 테이블 (GitHub TIL 업무 일지 자동 생성 원천)
            CREATE TABLE IF NOT EXISTS window_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                process_name TEXT NOT NULL,
                window_title TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                duration_seconds INTEGER NOT NULL,
                session_id TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_window_sessions_started_at ON window_sessions(started_at);

            -- 3. 집중 모드 세션 메타데이터 테이블
            CREATE TABLE IF NOT EXISTS focus_sessions (
                session_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                target_duration_minutes INTEGER NOT NULL,
                blocked_count INTEGER DEFAULT 0,
                is_completed INTEGER DEFAULT 0
            );

            -- 4. 사용자 맞춤형 선별 피드백 루프 테이블
            CREATE TABLE IF NOT EXISTS user_feedbacks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                notification_id TEXT NOT NULL,
                feedback_type TEXT NOT NULL,
                created_at TEXT DEFAULT (datetime('now', 'utc')),
                FOREIGN KEY(notification_id) REFERENCES notifications(id) ON DELETE CASCADE
            );

            -- 5. 사용자가 명시적으로 시작하고 마친 활동 기록 범위
            CREATE TABLE IF NOT EXISTS activity_sessions (
                activity_session_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                status TEXT NOT NULL CHECK(status IN ('active', 'completed')),
                last_seen_at TEXT,
                created_at TEXT DEFAULT (datetime('now', 'utc'))
            );

            CREATE INDEX IF NOT EXISTS idx_activity_sessions_started_at
                ON activity_sessions(started_at);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_activity_sessions_single_active
                ON activity_sessions(status) WHERE status = 'active';

            -- 앱 종료, 절전, 재실행 사이 구간은 기록 시간에서 제외한다.
            CREATE TABLE IF NOT EXISTS activity_session_segments (
                segment_id TEXT PRIMARY KEY,
                activity_session_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                last_seen_at TEXT NOT NULL,
                FOREIGN KEY(activity_session_id) REFERENCES activity_sessions(activity_session_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_activity_session_segments_activity
                ON activity_session_segments(activity_session_id, started_at);

            -- 새 기록만 집중모드 ON 구간으로 집계한다. 이전 기록과 구분하는 표식이다.
            CREATE TABLE IF NOT EXISTS activity_capture_policy (
                activity_session_id TEXT PRIMARY KEY,
                FOREIGN KEY(activity_session_id) REFERENCES activity_sessions(activity_session_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS activity_capture_segments (
                segment_id TEXT PRIMARY KEY,
                activity_session_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                last_seen_at TEXT NOT NULL,
                FOREIGN KEY(activity_session_id) REFERENCES activity_sessions(activity_session_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_activity_capture_segments_activity
                ON activity_capture_segments(activity_session_id, started_at);

            -- 외부 알림 작성자도 같은 DB를 사용하므로 저장 경계는 DB에서 강제한다.
            -- 절전/비정상 종료 후 열린 구간은 최근 heartbeat가 없으면 인정하지 않는다.
            CREATE TRIGGER IF NOT EXISTS trg_notifications_focus_capture
            BEFORE INSERT ON notifications
            WHEN NOT EXISTS (
                SELECT 1 FROM activity_capture_segments capture
                JOIN activity_sessions activity
                  ON activity.activity_session_id = capture.activity_session_id
                WHERE activity.status = 'active'
                  AND capture.ended_at IS NULL
                  AND julianday(NEW.received_at) >= julianday(capture.started_at)
                  AND julianday('now') - julianday(capture.last_seen_at) <= 10.0 / 86400.0
            )
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """;

        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
    }
}

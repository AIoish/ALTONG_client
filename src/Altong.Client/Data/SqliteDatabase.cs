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
            """;

        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _keepAliveConnection?.Dispose();
        _keepAliveConnection = null;
    }
}

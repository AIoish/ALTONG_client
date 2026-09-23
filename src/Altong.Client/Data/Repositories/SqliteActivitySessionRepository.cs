using System.Globalization;
using System.IO;
using Altong.Client.Data.Models;

namespace Altong.Client.Data.Repositories;

/// <summary>기존 기록 테이블은 변경하지 않고 활동의 시작과 종료 경계만 저장한다.</summary>
public sealed class SqliteActivitySessionRepository(IAltongDatabase database)
{
    public async Task InsertAsync(ActivitySessionRecord session)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_sessions (
                activity_session_id, started_at, ended_at, status, last_seen_at
            ) VALUES (
                $id, $started_at, $ended_at, $status, $last_seen_at
            );
            """;
        command.Parameters.AddWithValue("$id", session.ActivitySessionId);
        command.Parameters.AddWithValue("$started_at", Format(session.StartedAt));
        command.Parameters.AddWithValue("$ended_at", session.EndedAt is { } end ? Format(end) : DBNull.Value);
        command.Parameters.AddWithValue("$status", ToDatabaseValue(session.Status));
        command.Parameters.AddWithValue("$last_seen_at", session.LastSeenAt is { } seen ? Format(seen) : DBNull.Value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<ActivitySessionRecord?> GetOpenAsync()
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT activity_session_id, started_at, ended_at, status, last_seen_at
            FROM activity_sessions
            WHERE status = 'active' AND ended_at IS NULL
            ORDER BY started_at DESC
            LIMIT 1;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<ActivitySessionRecord?> GetByIdAsync(string activitySessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT activity_session_id, started_at, ended_at, status, last_seen_at
            FROM activity_sessions
            WHERE activity_session_id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", activitySessionId);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task TouchAsync(string activitySessionId, DateTime lastSeenAt)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE activity_sessions
            SET last_seen_at = $last_seen_at
            WHERE activity_session_id = $id AND status = 'active';
            """;
        command.Parameters.AddWithValue("$id", activitySessionId);
        command.Parameters.AddWithValue("$last_seen_at", Format(lastSeenAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task CompleteAsync(string activitySessionId, DateTime endedAt)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE activity_sessions
            SET ended_at = $ended_at,
                status = 'completed',
                last_seen_at = $ended_at
            WHERE activity_session_id = $id AND status = 'active';
            """;
        command.Parameters.AddWithValue("$id", activitySessionId);
        command.Parameters.AddWithValue("$ended_at", Format(endedAt));
        int affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (affected != 1)
            throw new InvalidOperationException("진행 중인 활동 기록을 찾을 수 없습니다.");
    }

    private static ActivitySessionRecord Map(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Parse(reader.GetString(2)),
        ParseStatus(reader.GetString(3)),
        reader.IsDBNull(4) ? null : Parse(reader.GetString(4)));

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string ToDatabaseValue(ActivitySessionStatus status) => status switch
    {
        ActivitySessionStatus.Active => "active",
        ActivitySessionStatus.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
    private static ActivitySessionStatus ParseStatus(string value) => value switch
    {
        "active" => ActivitySessionStatus.Active,
        "completed" => ActivitySessionStatus.Completed,
        _ => throw new InvalidDataException($"알 수 없는 활동 기록 상태입니다: {value}"),
    };
}

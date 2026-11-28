using System.Globalization;
using Altong.Client.Data.Models;
using Microsoft.Data.Sqlite;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// Microsoft.Data.Sqlite 기반의 집중 모드 세션 저장소 구현체.
/// </summary>
public sealed class SqliteFocusSessionRepository : IFocusSessionRepository
{
    private readonly IAltongDatabase _database;

    public SqliteFocusSessionRepository(IAltongDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task StartSessionAsync(FocusSessionRecord session)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO focus_sessions (
                session_id, started_at, ended_at, target_duration_minutes, blocked_count, is_completed
            ) VALUES (
                $session_id, $started_at, $ended_at, $target_duration_minutes, $blocked_count, $is_completed
            );
            """;

        command.Parameters.AddWithValue("$session_id", session.SessionId);
        command.Parameters.AddWithValue("$started_at", session.StartedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ended_at", session.EndedAt.HasValue ? session.EndedAt.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$target_duration_minutes", session.TargetDurationMinutes);
        command.Parameters.AddWithValue("$blocked_count", session.BlockedCount);
        command.Parameters.AddWithValue("$is_completed", session.IsCompleted ? 1 : 0);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task EndSessionAsync(string sessionId, DateTime endedAt, bool isCompleted)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE focus_sessions
            SET ended_at = $ended_at,
                is_completed = $is_completed
            WHERE session_id = $session_id;
            """;

        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$ended_at", endedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$is_completed", isCompleted ? 1 : 0);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task IncrementBlockedCountAsync(string sessionId)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE focus_sessions
            SET blocked_count = blocked_count + 1
            WHERE session_id = $session_id;
            """;

        command.Parameters.AddWithValue("$session_id", sessionId);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<FocusSessionRecord?> GetByIdAsync(string sessionId)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT session_id, started_at, ended_at, target_duration_minutes, blocked_count, is_completed
            FROM focus_sessions
            WHERE session_id = $session_id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return MapFocusSession(reader);
        }

        return null;
    }

    public async Task<FocusSessionRecord?> GetActiveSessionAsync()
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT session_id, started_at, ended_at, target_duration_minutes, blocked_count, is_completed
            FROM focus_sessions
            WHERE ended_at IS NULL
            ORDER BY started_at DESC
            LIMIT 1;
            """;

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            return MapFocusSession(reader);
        }

        return null;
    }

    private static FocusSessionRecord MapFocusSession(SqliteDataReader reader)
    {
        string sessionId = reader.GetString(0);
        DateTime startedAt = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTime? endedAt = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        int targetMinutes = reader.GetInt32(3);
        int blockedCount = reader.GetInt32(4);
        bool isCompleted = reader.GetInt32(5) == 1;

        return new FocusSessionRecord(sessionId, startedAt, endedAt, targetMinutes, blockedCount, isCompleted);
    }
}

using System.Globalization;
using Altong.Client.Data.Models;
using Microsoft.Data.Sqlite;

namespace Altong.Client.Data.Repositories;

/// <summary>
/// Microsoft.Data.Sqlite 기반의 활성 창 작업 세션 저장소 구현체.
/// </summary>
public sealed class SqliteWindowSessionRepository : IWindowSessionRepository
{
    private readonly IAltongDatabase _database;

    public SqliteWindowSessionRepository(IAltongDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task InsertAsync(WindowSessionRecord session)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO window_sessions (
                process_name, window_title, started_at, ended_at, duration_seconds, session_id
            ) VALUES (
                $process_name, $window_title, $started_at, $ended_at, $duration_seconds, $session_id
            );
            """;

        command.Parameters.AddWithValue("$process_name", session.ProcessName);
        command.Parameters.AddWithValue("$window_title", session.WindowTitle);
        command.Parameters.AddWithValue("$started_at", session.StartedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ended_at", session.EndedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$duration_seconds", session.DurationSeconds);
        command.Parameters.AddWithValue("$session_id", (object?)session.SessionId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WindowSessionRecord>> GetSessionsByDateRangeAsync(DateTime fromUtc, DateTime toUtc)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, process_name, window_title, started_at, ended_at, duration_seconds, session_id
            FROM window_sessions
            WHERE started_at >= $from AND started_at <= $to
            ORDER BY started_at ASC;
            """;

        command.Parameters.AddWithValue("$from", fromUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toUtc.ToString("o", CultureInfo.InvariantCulture));

        var list = new List<WindowSessionRecord>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(new WindowSessionRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return list;
    }

    public async Task<int> GetTotalDwellTimeSecondsAsync(DateTime fromUtc, DateTime toUtc)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COALESCE(SUM(duration_seconds), 0)
            FROM window_sessions
            WHERE started_at >= $from AND started_at <= $to;
            """;

        command.Parameters.AddWithValue("$from", fromUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toUtc.ToString("o", CultureInfo.InvariantCulture));

        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetAppDwellTimeSummaryAsync(DateTime fromUtc, DateTime toUtc)
    {
        using var connection = _database.CreateOpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT process_name, SUM(duration_seconds) AS total_duration
            FROM window_sessions
            WHERE started_at >= $from AND started_at <= $to
            GROUP BY process_name
            ORDER BY total_duration DESC;
            """;

        command.Parameters.AddWithValue("$from", fromUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toUtc.ToString("o", CultureInfo.InvariantCulture));

        var summary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            summary[reader.GetString(0)] = reader.GetInt32(1);
        }

        return summary;
    }
}

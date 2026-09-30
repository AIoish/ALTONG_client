using System.Globalization;
using System.IO;
using Altong.Client.Data.Models;

namespace Altong.Client.Data.Repositories;

/// <summary>기존 기록 테이블은 변경하지 않고 활동의 시작과 종료 경계만 저장한다.</summary>
public sealed class SqliteActivitySessionRepository(IAltongDatabase database)
{
    public async Task EnableFocusCaptureAsync(string activitySessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO activity_capture_policy(activity_session_id) VALUES ($id);";
        command.Parameters.AddWithValue("$id", activitySessionId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<bool> UsesFocusCaptureAsync(string activitySessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM activity_capture_policy WHERE activity_session_id = $id);";
        command.Parameters.AddWithValue("$id", activitySessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    public async Task StartCaptureSegmentAsync(string activitySessionId, DateTime startedAt)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_capture_segments(segment_id, activity_session_id, started_at, last_seen_at)
            VALUES ($segment, $id, $started, $started);
            """;
        command.Parameters.AddWithValue("$segment", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$id", activitySessionId);
        command.Parameters.AddWithValue("$started", Format(startedAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task EndCaptureSegmentAsync(string activitySessionId, DateTime endedAt, bool useLastSeenAt = false)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = useLastSeenAt
            ? "UPDATE activity_capture_segments SET ended_at = last_seen_at WHERE activity_session_id = $id AND ended_at IS NULL;"
            : "UPDATE activity_capture_segments SET ended_at = $ended, last_seen_at = $ended WHERE activity_session_id = $id AND ended_at IS NULL;";
        command.Parameters.AddWithValue("$id", activitySessionId);
        if (!useLastSeenAt) command.Parameters.AddWithValue("$ended", Format(endedAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task TouchCaptureSegmentAsync(string activitySessionId, DateTime lastSeenAt)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE activity_capture_segments SET last_seen_at = $seen WHERE activity_session_id = $id AND ended_at IS NULL;";
        command.Parameters.AddWithValue("$id", activitySessionId);
        command.Parameters.AddWithValue("$seen", Format(lastSeenAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(DateTime Start, DateTime End)>> GetCaptureIntervalsAsync(string activitySessionId, DateTime upperBound)
    {
        upperBound = upperBound.ToUniversalTime();
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at, ended_at FROM activity_capture_segments
            WHERE activity_session_id = $id ORDER BY started_at;
            """;
        command.Parameters.AddWithValue("$id", activitySessionId);
        var intervals = new List<(DateTime Start, DateTime End)>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var start = Parse(reader.GetString(0));
            var end = reader.IsDBNull(1) ? upperBound : Parse(reader.GetString(1));
            if (end > upperBound) end = upperBound;
            if (end > start) intervals.Add((start, end));
        }
        return intervals;
    }

    public async Task<TimeSpan?> GetCapturedDurationAsync(string activitySessionId, DateTime upperBound)
    {
        if (!await UsesFocusCaptureAsync(activitySessionId).ConfigureAwait(false)) return null;
        var intervals = await GetCaptureIntervalsAsync(activitySessionId, upperBound).ConfigureAwait(false);
        return intervals.Aggregate(TimeSpan.Zero, (sum, interval) => sum + (interval.End - interval.Start));
    }

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

    public async Task<ActivitySessionRecord?> GetLatestCompletedAsync()
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT activity_session_id, started_at, ended_at, status, last_seen_at
            FROM activity_sessions
            WHERE status = 'completed' AND ended_at IS NOT NULL
            ORDER BY julianday(ended_at) DESC
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

    public async Task StartSegmentAsync(string activitySessionId, DateTime startedAt)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_session_segments (
                segment_id, activity_session_id, started_at, ended_at, last_seen_at
            ) VALUES ($segment_id, $activity_session_id, $started_at, NULL, $last_seen_at);
            """;
        command.Parameters.AddWithValue("$segment_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$activity_session_id", activitySessionId);
        command.Parameters.AddWithValue("$started_at", Format(startedAt));
        command.Parameters.AddWithValue("$last_seen_at", Format(startedAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task BackfillLegacySegmentAsync(string activitySessionId, DateTime startedAt, DateTime endedAt)
    {
        if (endedAt <= startedAt)
            return;

        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO activity_session_segments (
                segment_id, activity_session_id, started_at, ended_at, last_seen_at
            ) VALUES ($segment_id, $activity_session_id, $started_at, $ended_at, $ended_at);
            """;
        command.Parameters.AddWithValue("$segment_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$activity_session_id", activitySessionId);
        command.Parameters.AddWithValue("$started_at", Format(startedAt));
        command.Parameters.AddWithValue("$ended_at", Format(endedAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task TouchActiveSegmentAsync(string activitySessionId, DateTime lastSeenAt)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE activity_session_segments
            SET last_seen_at = $last_seen_at
            WHERE activity_session_id = $activity_session_id AND ended_at IS NULL;
            """;
        command.Parameters.AddWithValue("$activity_session_id", activitySessionId);
        command.Parameters.AddWithValue("$last_seen_at", Format(lastSeenAt));
        int changed = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException("진행 중인 활동 구간을 찾을 수 없습니다.");
    }

    public async Task EndActiveSegmentAsync(string activitySessionId, DateTime endedAt, bool useLastSeenAt = false)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = useLastSeenAt
            ? """
              UPDATE activity_session_segments
              SET ended_at = last_seen_at
              WHERE activity_session_id = $activity_session_id AND ended_at IS NULL;
              """
            : """
              UPDATE activity_session_segments
              SET ended_at = $ended_at, last_seen_at = $ended_at
              WHERE activity_session_id = $activity_session_id AND ended_at IS NULL;
              """;
        command.Parameters.AddWithValue("$activity_session_id", activitySessionId);
        if (!useLastSeenAt)
            command.Parameters.AddWithValue("$ended_at", Format(endedAt));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<TimeSpan?> GetRecordedDurationAsync(string activitySessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at, COALESCE(ended_at, last_seen_at)
            FROM activity_session_segments
            WHERE activity_session_id = $activity_session_id
            ORDER BY started_at;
            """;
        command.Parameters.AddWithValue("$activity_session_id", activitySessionId);
        TimeSpan duration = TimeSpan.Zero;
        bool hasSegments = false;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            hasSegments = true;
            DateTime startedAt = Parse(reader.GetString(0));
            DateTime endedAt = Parse(reader.GetString(1));
            if (endedAt > startedAt)
                duration += endedAt - startedAt;
        }
        return hasSegments ? duration : null;
    }

    public async Task CompleteAsync(string activitySessionId, DateTime endedAt)
    {
        using var connection = database.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        using var segmentCommand = connection.CreateCommand();
        segmentCommand.Transaction = transaction;
        segmentCommand.CommandText = """
            UPDATE activity_session_segments
            SET ended_at = $ended_at, last_seen_at = $ended_at
            WHERE activity_session_id = $id AND ended_at IS NULL;
            """;
        segmentCommand.Parameters.AddWithValue("$id", activitySessionId);
        segmentCommand.Parameters.AddWithValue("$ended_at", Format(endedAt));
        await segmentCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

        using var captureCommand = connection.CreateCommand();
        captureCommand.Transaction = transaction;
        captureCommand.CommandText = """
            UPDATE activity_capture_segments
            SET ended_at = $ended_at, last_seen_at = $ended_at
            WHERE activity_session_id = $id AND ended_at IS NULL;
            """;
        captureCommand.Parameters.AddWithValue("$id", activitySessionId);
        captureCommand.Parameters.AddWithValue("$ended_at", Format(endedAt));
        await captureCommand.ExecuteNonQueryAsync().ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        transaction.Commit();
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

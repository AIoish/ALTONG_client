using System.Globalization;
using System.IO;
using System.Text.Json;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using Altong.Client.Models;

namespace Altong.Client.Data.Repositories;

public sealed record ReportListEntry(string SessionId, DateTime StartedAt, DateTime EndedAt,
    int NotificationCount, int BlockedCount, int PassedCount, int ApplicationCount, string? MainApp, bool IsSaved)
{
    public string PeriodText => StartedAt.ToLocalTime().Date == EndedAt.ToLocalTime().Date
        ? $"{StartedAt.ToLocalTime():HH:mm} → {EndedAt.ToLocalTime():HH:mm}"
        : $"{StartedAt.ToLocalTime():M월 d일 HH:mm} → {EndedAt.ToLocalTime():M월 d일 HH:mm}";
    public string SummaryText
    {
        get
        {
            var duration = EndedAt - StartedAt;
            return duration.TotalMinutes < 1 ? "사용시간 · 1분 미만"
                : duration.TotalHours < 1 ? $"사용시간 · {(int)duration.TotalMinutes}분"
                : $"사용시간 · {(int)duration.TotalHours}시간 {duration.Minutes}분";
        }
    }
    public string AppsText => ApplicationCount == 0 ? "앱 사용 기록 없음" : $"주요 앱 {MainApp} · 사용 앱 {ApplicationCount}개";
    public string SourceText => IsSaved ? "저장된 리포트" : "과거 기록 · 조회 시 재구성";
}

// Store actual summary/draft values, not computed properties which could change with future releases.
internal sealed record ReportSnapshot(DateTime StartedAt, DateTime EndedAt,
    NotificationRecord[] Notifications, SessionAppUsage[] Apps, string NotificationSummary,
    string[] NotificationHighlights, QuickReplyDraft[] QuickReplies)
{
    public NotificationSummaryItem[] SummaryItems { get; init; } = [];
    public static ReportSnapshot Capture(SessionResult result) => new(result.StartedAt, result.EndedAt,
        result.Notifications.ToArray(), result.Apps.ToArray(), result.NotificationSummary,
        result.NotificationHighlights.ToArray(), result.QuickReplies.ToArray())
    {
        SummaryItems = result.SummaryItems.ToArray()
    };

    public SessionResult Restore(string sessionId) => new(StartedAt, EndedAt, Notifications, Apps, true)
    {
        SessionId = sessionId, ReportSource = "저장된 리포트",
        StoredNotificationSummary = NotificationSummary,
        StoredNotificationHighlights = NotificationHighlights, StoredQuickReplies = QuickReplies,
        SummaryItems = SummaryItems ?? []
    };
}

public sealed record DashboardSummarySession(string SessionId, DateTime EndedAt,
    IReadOnlyList<NotificationSummaryItem> Items);

public sealed class SqliteSessionReportRepository(IAltongDatabase database)
{
    public event EventHandler? Changed;
    public const int PageSize = 30;

    // Read existing sessions/snapshots; dashboard summaries do not change report data.
    public async Task<IReadOnlyList<DashboardSummarySession>> ReadSummarySessionsAsync()
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id, s.ended_at,
                   CASE WHEN r.schema_version = 1 THEN json_extract(r.snapshot_json, '$.SummaryItems') END
            FROM focus_sessions s
            LEFT JOIN focus_session_reports r ON r.session_id = s.session_id
            WHERE s.ended_at IS NOT NULL AND s.is_completed = 1
            ORDER BY julianday(s.ended_at) DESC, s.session_id;
            """;
        var rows = new List<DashboardSummarySession>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetString(0), Parse(reader.GetString(1)), reader.IsDBNull(2) ? []
                : JsonSerializer.Deserialize<NotificationSummaryItem[]>(reader.GetString(2)) ?? []));
        return rows;
    }

    public async Task SaveAsync(SessionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.SessionId) || !result.IsFocusSessionReport || result.EndedAt < result.StartedAt)
            throw new ArgumentException("저장 가능한 집중 리포트가 아닙니다.", nameof(result));
        var snapshot = JsonSerializer.Serialize(ReportSnapshot.Capture(result));
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO focus_session_reports (session_id, started_at, ended_at, saved_at, schema_version, snapshot_json)
            VALUES ($id, $start, $end, $saved, 1, $snapshot)
            ON CONFLICT(session_id) DO UPDATE SET snapshot_json = excluded.snapshot_json,
                saved_at = excluded.saved_at, schema_version = excluded.schema_version
            WHERE focus_session_reports.snapshot_json IS NULL;
            """;
        command.Parameters.AddWithValue("$id", result.SessionId);
        command.Parameters.AddWithValue("$start", Stamp(result.StartedAt));
        command.Parameters.AddWithValue("$end", Stamp(result.EndedAt));
        command.Parameters.AddWithValue("$saved", Stamp(DateTime.UtcNow));
        command.Parameters.AddWithValue("$snapshot", snapshot);
        await command.ExecuteNonQueryAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(string sessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        // Original sessions, notifications and window records remain intact.
        command.CommandText = "DELETE FROM focus_session_reports WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SessionResult?> GetSnapshotAsync(string sessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json, schema_version FROM focus_session_reports WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;
        if (reader.GetInt32(1) != 1) throw new InvalidDataException("지원하지 않는 리포트 저장 형식입니다.");
        var snapshot = JsonSerializer.Deserialize<ReportSnapshot>(reader.GetString(0))
            ?? throw new InvalidDataException("리포트 저장 내용을 읽을 수 없습니다.");
        return snapshot.Restore(sessionId);
    }

    public async Task<bool> IsLegacyReportAsync(string sessionId)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM focus_session_reports WHERE session_id = $id AND snapshot_json IS NULL);";
        command.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) != 0;
    }

    public static (DateTime FromUtc, DateTime ToUtc) LocalRange(DateTime firstDate, DateTime exclusiveLastDate,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        static DateTime Boundary(DateTime date, TimeZoneInfo zone)
        {
            var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
            // Some zones skip midnight when daylight saving starts.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            if (zone.IsAmbiguousTime(local))
                return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        return (Boundary(firstDate, zone), Boundary(exclusiveLastDate, zone));
    }

    public async Task<IReadOnlyDictionary<DateTime, int>> GetMonthCountsAsync(DateTime month,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var first = new DateTime(month.Year, month.Month, 1);
        var range = LocalRange(first, first.AddMonths(1), zone);
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ended_at FROM focus_session_reports
            WHERE julianday(ended_at) >= julianday($from) AND julianday(ended_at) < julianday($to);
            """;
        AddRange(command, range);
        var counts = new Dictionary<DateTime, int>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var day = TimeZoneInfo.ConvertTimeFromUtc(Parse(reader.GetString(0)).ToUniversalTime(), zone).Date;
            counts[day] = counts.GetValueOrDefault(day) + 1;
        }
        return counts;
    }

    public async Task<IReadOnlyList<ReportListEntry>> GetDayPageAsync(DateTime date, int offset = 0,
        TimeZoneInfo? timeZone = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var range = LocalRange(date, date.Date.AddDays(1), timeZone);
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        // Summary queries do not fetch snapshot blobs. JSON fields describe the saved values;
        // correlated queries are used only for pre-upgrade entries without a snapshot.
        command.CommandText = """
            SELECT r.session_id, r.started_at, r.ended_at, r.snapshot_json IS NOT NULL,
                COALESCE(json_array_length(r.snapshot_json, '$.Notifications'),
                    (SELECT COUNT(*) FROM notifications n WHERE n.session_id = r.session_id
                      AND julianday(n.received_at) >= julianday(r.started_at) AND julianday(n.received_at) < julianday(r.ended_at))),
                CASE WHEN r.snapshot_json IS NOT NULL THEN
                    (SELECT COUNT(*) FROM json_each(r.snapshot_json, '$.Notifications') WHERE json_extract(value, '$.IsPassed') = 0)
                    ELSE (SELECT COUNT(*) FROM notifications n WHERE n.session_id = r.session_id AND n.is_passed = 0
                      AND julianday(n.received_at) >= julianday(r.started_at) AND julianday(n.received_at) < julianday(r.ended_at)) END,
                CASE WHEN r.snapshot_json IS NOT NULL THEN
                    (SELECT COUNT(*) FROM json_each(r.snapshot_json, '$.Notifications') WHERE json_extract(value, '$.IsPassed') = 1)
                    ELSE (SELECT COUNT(*) FROM notifications n WHERE n.session_id = r.session_id AND n.is_passed = 1
                      AND julianday(n.received_at) >= julianday(r.started_at) AND julianday(n.received_at) < julianday(r.ended_at)) END,
                COALESCE(json_array_length(r.snapshot_json, '$.Apps'),
                    (SELECT COUNT(DISTINCT w.process_name COLLATE NOCASE) FROM window_sessions w WHERE w.session_id = r.session_id
                      AND julianday(w.ended_at) > julianday(r.started_at) AND julianday(w.started_at) < julianday(r.ended_at))),
                COALESCE(json_extract(r.snapshot_json, '$.Apps[0].AppName'),
                    (SELECT w.process_name FROM window_sessions w WHERE w.session_id = r.session_id
                      AND julianday(w.ended_at) > julianday(r.started_at) AND julianday(w.started_at) < julianday(r.ended_at)
                     GROUP BY w.process_name COLLATE NOCASE ORDER BY SUM(w.duration_seconds) DESC LIMIT 1))
            FROM focus_session_reports r
            WHERE julianday(r.ended_at) >= julianday($from) AND julianday(r.ended_at) < julianday($to)
            ORDER BY julianday(r.ended_at) DESC, r.session_id DESC LIMIT $limit OFFSET $offset;
            """;
        AddRange(command, range);
        command.Parameters.AddWithValue("$limit", PageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var rows = new List<ReportListEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetString(0), Parse(reader.GetString(1)), Parse(reader.GetString(2)),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(3)));
        return rows;
    }

    private static void AddRange(Microsoft.Data.Sqlite.SqliteCommand command, (DateTime FromUtc, DateTime ToUtc) range)
    {
        command.Parameters.AddWithValue("$from", Stamp(range.FromUtc));
        command.Parameters.AddWithValue("$to", Stamp(range.ToUtc));
    }

    private static string Stamp(DateTime date) => date.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

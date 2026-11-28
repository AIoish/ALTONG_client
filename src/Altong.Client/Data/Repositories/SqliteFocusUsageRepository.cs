using System.Globalization;
using Altong.Client.Data.Models;
using Altong.Client.Models;

namespace Altong.Client.Data.Repositories;

/// <summary>Read-only dashboard totals, independent of saved report snapshots.</summary>
public sealed class SqliteFocusUsageRepository(IAltongDatabase database)
{
    public async Task<FocusUsageDay> ReadDayAsync(DateTime localDate, DateTime nowUtc,
        FocusSessionRecord? activeSession = null, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var range = SqliteSessionReportRepository.LocalRange(localDate, localDate.Date.AddDays(1), zone);
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        // Interrupted sessions stop at their last persisted observation, never at restart time.
        // Only the current process's active session is extended to now, below.
        command.CommandText = """
            WITH observed_sessions AS (
                SELECT f.session_id, f.started_at,
                    COALESCE(f.ended_at, (
                        SELECT w.ended_at FROM window_sessions w
                        WHERE w.session_id = f.session_id
                        ORDER BY julianday(w.ended_at) DESC LIMIT 1
                    )) AS observed_end,
                    f.ended_at IS NULL AS recovered
                FROM focus_sessions f
                WHERE julianday(f.started_at) < julianday($to)
            )
            SELECT session_id, started_at, observed_end, recovered FROM observed_sessions
            WHERE julianday(observed_end) > julianday($from);
            """;
        command.Parameters.AddWithValue("$from", range.FromUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", range.ToUtc.ToString("o", CultureInfo.InvariantCulture));
        var records = new List<FocusSessionRecord>();
        bool hasRecoveredSession = false;
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new FocusSessionRecord(reader.GetString(0), Parse(reader.GetString(1)), Parse(reader.GetString(2))));
            hasRecoveredSession |= reader.GetInt32(3) == 1 && reader.GetString(0) != activeSession?.SessionId;
        }
        return Aggregate(localDate, nowUtc, records, activeSession, zone) with { HasRecoveredSession = hasRecoveredSession };
    }

    public static FocusUsageDay Aggregate(DateTime localDate, DateTime nowUtc,
        IEnumerable<FocusSessionRecord> records, FocusSessionRecord? activeSession = null,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var day = localDate.Date;
        var range = SqliteSessionReportRepository.LocalRange(day, day.AddDays(1), zone);
        var now = nowUtc.ToUniversalTime();
        var limit = now < range.ToUtc ? now : range.ToUtc;
        var intervals = new List<(DateTime Start, DateTime End)>();
        foreach (var record in activeSession is null ? records : records.Append(activeSession))
        {
            var end = record.EndedAt?.ToUniversalTime();
            if (end is null && record.SessionId == activeSession?.SessionId) end = now;
            if (end is null) continue;
            var start = record.StartedAt.ToUniversalTime();
            start = start < range.FromUtc ? range.FromUtc : start;
            var clippedEnd = end.Value > limit ? limit : end.Value;
            if (clippedEnd > start) intervals.Add((start, clippedEnd));
        }

        // Merge overlaps before bucketing, so duplicated/overlapping records cannot double-count time.
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var interval in intervals.OrderBy(item => item.Start))
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End) merged.Add(interval);
            else if (interval.End > merged[^1].End) merged[^1] = (merged[^1].Start, interval.End);
        }
        var ticks = new long[24];
        foreach (var interval in merged)
        {
            var cursor = interval.Start;
            while (cursor < interval.End)
            {
                var hour = TimeZoneInfo.ConvertTimeFromUtc(cursor, zone).Hour;
                // UTC minute slices preserve actual elapsed time when local clocks change.
                var boundary = new DateTime(cursor.Ticks - cursor.Ticks % TimeSpan.TicksPerMinute,
                    DateTimeKind.Utc).AddMinutes(1);
                var end = boundary < interval.End ? boundary : interval.End;
                ticks[hour] += (end - cursor).Ticks;
                cursor = end;
            }
        }
        var scale = Math.Max(60, Math.Ceiling(TimeSpan.FromTicks(ticks.Max()).TotalMinutes / 30) * 30);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var hours = Enumerable.Range(0, 24).Select(hour => new FocusUsageHour(hour,
            TimeSpan.FromTicks(ticks[hour]), scale, localNow.Date == day && localNow.Hour == hour)).ToArray();
        return new FocusUsageDay(day, hours, scale);
    }

    private static DateTime Parse(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind).ToUniversalTime();
}

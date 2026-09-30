using System.ComponentModel;
using System.Globalization;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;

namespace Altong.Client.Services;

public sealed record ActivityJournalEntry(string AppName, string WindowTitle, DateTime StartedAt, DateTime EndedAt)
{
    public string TimeText => StartedAt.ToLocalTime().ToString("HH:mm");
    public string DurationText => $"{(int)(EndedAt - StartedAt).TotalMinutes}분 {(EndedAt - StartedAt).Seconds}초";
}

public sealed record SessionAppUsage(string AppName, double Seconds)
{
    public string DisplayName => AppName;
    public string DurationText => $"{(int)Seconds / 60}분 {(int)Seconds % 60}초";
    public double Percentage { get; init; }
}

public sealed record SessionResult(
    DateTime StartedAt, DateTime EndedAt, IReadOnlyList<NotificationRecord> Notifications,
    IReadOnlyList<SessionAppUsage> Apps, TimeSpan? RecordedDuration = null, bool UsesFocusCapture = false)
{
    public string DurationLabel => UsesFocusCapture ? "집중모드 ON 기록 시간" : "기존 방식 · 활동 기록 시간";
    public int BlockedCount => Notifications.Count(n => n.IsPassed == false);
    public int PassedCount => Notifications.Count(n => n.IsPassed == true);
    public int NotificationCount => Notifications.Count;
    public int ApplicationCount => Apps.Count;
    public string PeriodText => $"{StartedAt.ToLocalTime():M월 d일 HH:mm} – {EndedAt.ToLocalTime():M월 d일 HH:mm}";
    public TimeSpan Duration => RecordedDuration ?? (EndedAt - StartedAt);
    public string DurationText => $"{(int)Duration.TotalMinutes}분 {Duration.Seconds}초";
}

/// <summary>종료된 세션의 저장 및 알림 분류가 완료된 경우에만 결과를 공개한다.</summary>
public sealed class SessionResultsService(IAltongDatabase database, IFocusSessionRepository sessions) : INotifyPropertyChanged
{
    private FocusSessionRecord? _session;
    private Task _saveStarted = Task.CompletedTask;
    private Task _windowWrites = Task.CompletedTask;
    private CurrentContext? _lastContext;
    private bool _refreshing;
    private int _generation;
    private int _activityGeneration;
    public SessionResult? Latest { get; private set; }
    public bool IsCollecting { get; private set; }
    public string Status { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Begin(DateTime startedAt, int targetMinutes)
    {
        _generation++;
        _session = new FocusSessionRecord(Guid.NewGuid().ToString("N"), startedAt, TargetDurationMinutes: targetMinutes);
        _saveStarted = sessions.StartSessionAsync(_session);
        // Observe early failures; RefreshAsync retries the idempotent start when ending.
        _ = _saveStarted.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        Latest = null;
        IsCollecting = false;
        Status = "";
        Changed();
    }

    public void End(
        DateTime endedAt,
        CurrentContext lastContext,
        Task? windowWrites = null,
        bool collectResult = true)
    {
        if (_session is null || _session.EndedAt is not null) return;
        _session = _session with { EndedAt = endedAt };
        _lastContext = lastContext;
        _windowWrites = windowWrites ?? Task.CompletedTask;
        // 집중 세션은 항상 저장하되, 화면 결과는 활동 기록을 마칠 때만 집계할 수 있다.
        _saveStarted = SaveEndAsync(_session, _saveStarted, isCompleted: !collectResult);
        _ = _saveStarted.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        IsCollecting = collectResult;
        Status = collectResult ? "집중 결과를 집계하고 있어요." : "";
        Changed();
    }

    private async Task SaveEndAsync(FocusSessionRecord session, Task started, bool isCompleted = false)
    {
        try { await started; }
        catch
        {
            if (await sessions.GetByIdAsync(session.SessionId) is null)
                await sessions.StartSessionAsync(session with { EndedAt = null });
        }
        await sessions.EndSessionAsync(session.SessionId, session.EndedAt!.Value, isCompleted);
    }

    public async Task RefreshAsync()
    {
        if (_refreshing || !IsCollecting || _session?.EndedAt is not { } end) return;
        _refreshing = true;
        int generation = _generation;
        var session = _session;
        var context = _lastContext;
        var writes = _windowWrites;
        var saved = _saveStarted;
        try
        {
            await SaveEndAsync(session, saved);
            await writes;
            var result = await Task.Run(async () =>
            {
                var notifications = await ReadNotificationsAsync(session.StartedAt, end);
                if (notifications.Any(n => n.IsPassed is null)) return null;
                var apps = await ReadAppsAsync(session.StartedAt, end, context);
                await sessions.EndSessionAsync(session.SessionId, end, true);
                return new SessionResult(session.StartedAt, end, notifications, apps);
            });
            if (generation != _generation) return;
            Latest = result;
            IsCollecting = result is null;
            Status = result is null ? "알림 분류가 끝나면 결과를 보여드릴게요." : "";
            Changed();
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            Status = "결과 집계가 지연되고 있어요. 자동으로 다시 시도합니다.";
            Console.WriteLine($"[SessionResults] {ex.Message}");
            Changed();
        }
        finally { _refreshing = false; }
    }

    public void ClearResult()
    {
        _generation++;
        _activityGeneration++;
        Latest = null;
        IsCollecting = false;
        Status = "";
        Changed();
    }

    public async Task<SessionResult> BuildActivityResultAsync(
        DateTime startedAt,
        DateTime endedAt,
        CurrentContext lastContext,
        Task? windowWrites = null,
        TimeSpan? recordedDuration = null,
        string? activitySessionId = null)
    {
        int generation = ++_activityGeneration;
        Status = "활동 결과를 집계하고 있어요.";
        Changed();
        try
        {
            if (windowWrites is not null)
                await windowWrites;
            var notificationsTask = ReadNotificationsAsync(startedAt, endedAt, activitySessionId);
            var appsTask = ReadAppsAsync(startedAt, endedAt,
                activitySessionId is null ? lastContext : null, activitySessionId);
            await Task.WhenAll(notificationsTask, appsTask);
            var result = new SessionResult(
                startedAt,
                endedAt,
                await notificationsTask,
                await appsTask,
                recordedDuration,
                activitySessionId is not null);
            if (generation != _activityGeneration) return result;
            Latest = result;
            Status = "";
            Changed();
            return result;
        }
        catch
        {
            if (generation == _activityGeneration)
            {
                Status = "활동 결과를 불러오지 못했어요. 다시 시도해 주세요.";
                Changed();
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<NotificationRecord>> ReadNotificationsAsync(DateTime from, DateTime to,
        string? activitySessionId = null)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, app_name, sender, title, body, received_at, is_passed
            FROM notifications
            WHERE julianday(received_at) >= julianday($from) AND julianday(received_at) < julianday($to)
              AND ($activity_id IS NULL OR EXISTS (
                  SELECT 1 FROM activity_capture_segments capture
                  WHERE capture.activity_session_id = $activity_id
                    AND julianday(received_at) >= julianday(capture.started_at)
                    AND julianday(received_at) < julianday(COALESCE(capture.ended_at, $to))))
            ORDER BY received_at ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activity_id", (object?)activitySessionId ?? DBNull.Value);
        var list = new List<NotificationRecord>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new NotificationRecord(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : reader.GetInt32(6) == 1));
        return list;
    }

    public Task<IReadOnlyList<SessionAppUsage>> ReadAppUsageAsync(
        DateTime from, DateTime to, CurrentContext? currentContext = null, string? activitySessionId = null) =>
        ReadAppsAsync(from, to, currentContext, activitySessionId);

    public async Task<IReadOnlyList<ActivityJournalEntry>> ReadActivityJournalAsync(DateTime from, DateTime to,
        string? activitySessionId = null)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, window_title, started_at, ended_at FROM window_sessions
            WHERE julianday(ended_at) > julianday($from) AND julianday(started_at) < julianday($to)
              AND ($activity_id IS NULL OR session_id = $activity_id)
            ORDER BY julianday(started_at) DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activity_id", (object?)activitySessionId ?? DBNull.Value);
        var entries = new List<ActivityJournalEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var start = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var end = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            entries.Add(new ActivityJournalEntry(reader.GetString(0), reader.GetString(1),
                start < from ? from : start, end > to ? to : end));
        }
        if (activitySessionId is not null)
        {
            var captures = await new SqliteActivitySessionRepository(database)
                .GetCaptureIntervalsAsync(activitySessionId, to);
            var clipped = (from entry in entries
                           from capture in captures
                           let start = entry.StartedAt > capture.Start ? entry.StartedAt : capture.Start
                           let end = entry.EndedAt < capture.End ? entry.EndedAt : capture.End
                           where end > start
                           orderby start
                           select entry with { StartedAt = start, EndedAt = end }).ToArray();
            var merged = new List<ActivityJournalEntry>();
            foreach (var entry in clipped)
            {
                if (merged.Count > 0 && merged[^1] is { } previous &&
                    previous.AppName == entry.AppName && previous.WindowTitle == entry.WindowTitle &&
                    entry.StartedAt <= previous.EndedAt)
                    merged[^1] = previous with { EndedAt = entry.EndedAt > previous.EndedAt ? entry.EndedAt : previous.EndedAt };
                else
                    merged.Add(entry);
            }
            merged.Reverse();
            return merged;
        }
        return entries;
    }

    public async Task<IReadOnlyList<string>> ReadFocusedAppNamesAsync(
        DateTime from, DateTime to, CurrentContext? currentContext = null)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ws.process_name
            FROM window_sessions ws
            JOIN focus_sessions fs
              ON julianday(ws.ended_at) > julianday(fs.started_at)
             AND julianday(ws.started_at) < julianday(COALESCE(fs.ended_at, $to))
            WHERE julianday(fs.started_at) < julianday($to)
              AND julianday(COALESCE(fs.ended_at, $to)) > julianday($from)
            ORDER BY ws.process_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        if (currentContext is not null && !string.IsNullOrWhiteSpace(currentContext.ActiveProcess))
            names.Add(currentContext.ActiveProcess);
        return names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<string>> ReadAppNamesAsync(
        DateTime from, DateTime to, CurrentContext? currentContext = null)
    {
        var usage = await ReadAppsAsync(from, to, currentContext);
        return usage.Select(item => item.AppName)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<SessionAppUsage>> ReadAppsAsync(DateTime from, DateTime to,
        CurrentContext? context, string? activitySessionId = null)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, started_at, ended_at FROM window_sessions
            WHERE julianday(ended_at) > julianday($from) AND julianday(started_at) < julianday($to)
              AND ($activity_id IS NULL OR session_id = $activity_id);
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activity_id", (object?)activitySessionId ?? DBNull.Value);
        var intervals = new List<(string App, DateTime Start, DateTime End)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            intervals.Add((reader.GetString(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        IReadOnlyList<(DateTime Start, DateTime End)>? captures = null;
        if (activitySessionId is not null)
        {
            captures = await new SqliteActivitySessionRepository(database)
                .GetCaptureIntervalsAsync(activitySessionId, to);
            intervals = (from window in intervals
                         from capture in captures
                         let start = window.Start > capture.Start ? window.Start : capture.Start
                         let end = window.End < capture.End ? window.End : capture.End
                         where end > start
                         select (window.App, Start: start, End: end)).ToList();
        }
        if (context is { DurationSeconds: > 0 } && !string.IsNullOrWhiteSpace(context.ActiveProcess))
        {
            var liveStart = context.LastUpdated.AddSeconds(-context.DurationSeconds);
            if (activitySessionId is null)
                intervals.Add((context.ActiveProcess, liveStart, context.LastUpdated));
            else
            {
                foreach (var capture in captures!)
                {
                    var start = liveStart > capture.Start ? liveStart : capture.Start;
                    var end = context.LastUpdated < capture.End ? context.LastUpdated : capture.End;
                    if (end > start) intervals.Add((context.ActiveProcess, start, end));
                }
            }
        }
        // Merge overlaps so a live snapshot and a concurrently persisted window are not counted twice.
        return intervals.GroupBy(x => x.App, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            double seconds = 0;
            DateTime cursor = from;
            foreach (var interval in group.OrderBy(x => x.Start))
            {
                var start = interval.Start > cursor ? interval.Start : cursor;
                var end = interval.End < to ? interval.End : to;
                if (end > start) seconds += (end - start).TotalSeconds;
                if (end > cursor) cursor = end;
            }
            return new SessionAppUsage(group.Key, seconds);
        }).Where(x => x.Seconds > 0).OrderByDescending(x => x.Seconds).ToArray();
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

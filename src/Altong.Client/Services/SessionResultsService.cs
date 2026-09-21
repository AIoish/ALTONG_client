using System.ComponentModel;
using System.Globalization;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;

namespace Altong.Client.Services;

public sealed record SessionAppUsage(string AppName, double Seconds)
{
    public string DisplayName => AppName;
    public string DurationText => $"{(int)Seconds / 60}분 {(int)Seconds % 60}초";
    public double Percentage { get; init; }
}

public sealed record SessionResult(
    DateTime StartedAt, DateTime EndedAt, IReadOnlyList<NotificationRecord> Notifications,
    IReadOnlyList<SessionAppUsage> Apps)
{
    public int BlockedCount => Notifications.Count(n => n.IsPassed == false);
    public int PassedCount => Notifications.Count(n => n.IsPassed == true);
    public int NotificationCount => Notifications.Count;
    public int ApplicationCount => Apps.Count;
    public string PeriodText => $"{StartedAt.ToLocalTime():M월 d일 HH:mm} – {EndedAt.ToLocalTime():M월 d일 HH:mm}";
    public string DurationText => $"{(int)(EndedAt - StartedAt).TotalMinutes}분 {(EndedAt - StartedAt).Seconds}초";
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

    public void End(DateTime endedAt, CurrentContext lastContext, Task? windowWrites = null)
    {
        if (_session is null || _session.EndedAt is not null) return;
        _session = _session with { EndedAt = endedAt };
        _lastContext = lastContext;
        _windowWrites = windowWrites ?? Task.CompletedTask;
        // Persist the end even if another focus session starts before aggregation finishes.
        _saveStarted = SaveEndAsync(_session, _saveStarted);
        _ = _saveStarted.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        IsCollecting = true;
        Status = "집중 결과를 집계하고 있어요.";
        Changed();
    }

    private async Task SaveEndAsync(FocusSessionRecord session, Task started)
    {
        try { await started; }
        catch
        {
            if (await sessions.GetByIdAsync(session.SessionId) is null)
                await sessions.StartSessionAsync(session with { EndedAt = null });
        }
        await sessions.EndSessionAsync(session.SessionId, session.EndedAt!.Value, false);
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

    public async Task<IReadOnlyList<NotificationRecord>> ReadNotificationsAsync(DateTime from, DateTime to)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, app_name, sender, title, body, received_at, is_passed
            FROM notifications
            WHERE julianday(received_at) >= julianday($from) AND julianday(received_at) < julianday($to)
            ORDER BY received_at ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
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
        DateTime from, DateTime to, CurrentContext? currentContext = null) =>
        ReadAppsAsync(from, to, currentContext);

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

    private async Task<IReadOnlyList<SessionAppUsage>> ReadAppsAsync(DateTime from, DateTime to, CurrentContext? context)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, started_at, ended_at FROM window_sessions
            WHERE julianday(ended_at) > julianday($from) AND julianday(started_at) < julianday($to);
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        var intervals = new List<(string App, DateTime Start, DateTime End)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            intervals.Add((reader.GetString(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        if (context is { DurationSeconds: > 0 } && !string.IsNullOrWhiteSpace(context.ActiveProcess))
            intervals.Add((context.ActiveProcess, context.LastUpdated.AddSeconds(-context.DurationSeconds), context.LastUpdated));
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

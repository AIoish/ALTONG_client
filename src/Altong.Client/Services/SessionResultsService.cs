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

public sealed record QuickReplyDraft(string AppName, string? Sender, string NotificationTitle, string Text)
{
    public string Label => string.IsNullOrWhiteSpace(Sender) ? AppName : $"{AppName} · {Sender}";
}

public sealed record SessionResult(
    DateTime StartedAt, DateTime EndedAt, IReadOnlyList<NotificationRecord> Notifications,
    IReadOnlyList<SessionAppUsage> Apps, bool IsFocusSessionReport = false)
{
    public string? SessionId { get; init; }
    public string ReportSource { get; init; } = "저장 전 미리보기";
    public string? StoredNotificationSummary { get; init; }
    public IReadOnlyList<string>? StoredNotificationHighlights { get; init; }
    public IReadOnlyList<QuickReplyDraft>? StoredQuickReplies { get; init; }
    public IReadOnlyList<NotificationSummaryItem> SummaryItems { get; init; } = [];
    public string DurationLabel => "집중모드 사용 시간";
    public int BlockedCount => Notifications.Count(n => n.IsPassed == false);
    public int PassedCount => Notifications.Count(n => n.IsPassed == true);
    public int NotificationCount => Notifications.Count;
    public int ApplicationCount => Apps.Count;
    public string PeriodText => $"{StartedAt.ToLocalTime():M월 d일 HH:mm} – {EndedAt.ToLocalTime():M월 d일 HH:mm}";
    public TimeSpan Duration => EndedAt - StartedAt;
    public string DurationText => $"{(int)Duration.TotalMinutes}분 {Duration.Seconds}초";
    public string NotificationSummary => StoredNotificationSummary ?? (NotificationCount == 0
        ? "이번 집중 세션에 저장된 알림이 없습니다."
        : $"알림 {NotificationCount}개 · 차단 {BlockedCount}개 · 통과 {PassedCount}개");
    public IReadOnlyList<string> NotificationHighlights => StoredNotificationHighlights ?? Notifications
        .Where(notification => notification.IsPassed == false)
        .Take(3)
        .Select(notification => $"{notification.AppName} · {notification.Title}")
        .ToArray();
    public IReadOnlyList<QuickReplyDraft> QuickReplies => StoredQuickReplies ?? Notifications
        .Where(notification => notification.IsPassed == false)
        .Take(3)
        .Select(notification => new QuickReplyDraft(notification.AppName, notification.Sender,
            notification.Title, "확인했습니다. 집중 시간이 끝나서 이제 확인했어요. 곧 답변드릴게요."))
        .ToArray();
}

/// <summary>종료된 세션의 저장 작업이 끝나면 저장된 기록과 처리 실패 안내로 결과를 공개한다.</summary>
public sealed class SessionResultsService(IAltongDatabase database, IFocusSessionRepository sessions) : INotifyPropertyChanged
{
    public SqliteSessionReportRepository Reports { get; } = new(database);
    public SqliteDashboardNotificationRepository DashboardNotifications { get; } = new(database);
    public SqliteFocusUsageRepository FocusUsage { get; } = new(database);
    private FocusSessionRecord? _session;
    private Task _saveStarted = Task.CompletedTask;
    private readonly List<Task> _completionWrites = new();
    private Task _windowWrites = Task.CompletedTask;
    private CurrentContext? _lastContext;
    private bool _refreshing;
    private int _generation;
    public SessionResult? Latest { get; private set; }
    public bool IsCollecting { get; private set; }
    public string Status { get; private set; } = "";
    public string? CurrentSessionId => _session is { EndedAt: null } ? _session.SessionId : null;
    public FocusSessionRecord? ActiveSession => _session is { EndedAt: null } ? _session : null;
    public Task PendingSave
    {
        get
        {
            // Only background storage/aggregation tasks; UI continuations must never be awaited on exit.
            lock (_completionWrites) return Task.WhenAll(_completionWrites.Append(_saveStarted));
        }
    }
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
        // 앱 종료 시에는 리포트 없이 세션 상태만 저장할 수 있다.
        _saveStarted = SaveEndAsync(_session, _saveStarted, isCompleted: !collectResult);
        _ = _saveStarted.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        IsCollecting = collectResult;
        Status = collectResult ? "집중 결과를 집계하고 있어요." : "";
        Changed();
    }

    public async Task<SessionResult?> CompleteFocusSessionAsync(DateTime endedAt, Task? windowWrites = null,
        Task? notificationWrites = null)
    {
        if (_session is not { EndedAt: null } active) return null;
        int generation = ++_generation;
        var completed = active with { EndedAt = endedAt };
        var started = _saveStarted;
        _session = completed;
        // 기존 타이머 기반 집계와 별도로 완료하여 빠른 ON/OFF에도 각 결과를 유지한다.
        IsCollecting = false;
        Status = "집중 결과를 집계하고 있어요.";
        var completion = Task.Run(async () =>
        {
            await SaveEndAsync(completed, started);
            bool notificationFailure = false;
            if (notificationWrites is not null)
            {
                try { await notificationWrites; }
                catch (Exception ex)
                {
                    notificationFailure = true;
                    Console.WriteLine($"[SessionResults] 일부 알림 처리 실패: {ex.GetBaseException().Message}");
                }
            }
            if (windowWrites is not null)
            {
                try { await windowWrites; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SessionResults] 일부 앱 사용 기록 저장 실패: {ex.GetBaseException().Message}");
                }
            }
            var notificationsTask = ReadNotificationsAsync(active.StartedAt, endedAt,
                focusSessionId: active.SessionId);
            var appsTask = ReadAppsAsync(active.StartedAt, endedAt, null,
                focusSessionId: active.SessionId);
            await Task.WhenAll(notificationsTask, appsTask);
            await sessions.EndSessionAsync(active.SessionId, endedAt, true);
            var result = new SessionResult(active.StartedAt, endedAt, await notificationsTask,
                await appsTask, IsFocusSessionReport: true) { SessionId = active.SessionId };
            return notificationFailure ? result with
            {
                StoredNotificationSummary = result.NotificationSummary + " · 일부 알림 처리가 실패했습니다. 저장된 알림 기준입니다."
            } : result;
        });
        _saveStarted = completion;
        lock (_completionWrites)
        {
            _completionWrites.RemoveAll(task => task.IsCompletedSuccessfully);
            _completionWrites.Add(completion);
        }
        Changed();
        try
        {
            var result = await completion;
            if (generation == _generation)
            {
                Latest = result;
                IsCollecting = false;
                Status = "";
                Changed();
            }
            return result;
        }
        catch
        {
            if (generation == _generation)
            {
                IsCollecting = false;
                Status = "집중 결과를 불러오지 못했어요.";
                Changed();
            }
            throw;
        }
    }

    private async Task SaveEndAsync(FocusSessionRecord session, Task started, bool isCompleted = false)
    {
        try { await started.ConfigureAwait(false); }
        catch
        {
            if (await sessions.GetByIdAsync(session.SessionId).ConfigureAwait(false) is null)
                await sessions.StartSessionAsync(session with { EndedAt = null }).ConfigureAwait(false);
        }
        await sessions.EndSessionAsync(session.SessionId, session.EndedAt!.Value, isCompleted).ConfigureAwait(false);
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
        Latest = null;
        IsCollecting = false;
        Status = "";
        Changed();
    }

    public async Task<SessionResult?> OpenReportAsync(string sessionId)
    {
        var stored = await Reports.GetSnapshotAsync(sessionId);
        if (stored is not null) return stored;
        // Only explicitly indexed legacy entries may be reconstructed, never every ended session.
        if (!await Reports.IsLegacyReportAsync(sessionId)) return null;
        var focus = await sessions.GetByIdAsync(sessionId);
        if (focus?.EndedAt is not { } end) return null;
        var notifications = await ReadNotificationsAsync(focus.StartedAt, end, sessionId);
        var apps = await ReadAppsAsync(focus.StartedAt, end, null, sessionId);
        return new SessionResult(focus.StartedAt, end, notifications, apps, true)
        {
            SessionId = sessionId,
            ReportSource = "과거 기록으로 재구성 · 종료 당시 저장본이 아닙니다"
        };
    }

    public async Task<IReadOnlyList<NotificationRecord>> ReadNotificationsAsync(DateTime from, DateTime to,
        string? focusSessionId = null)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, app_name, sender, title, body, received_at, is_passed
            FROM notifications
            WHERE julianday(received_at) >= julianday($from) AND julianday(received_at) < julianday($to)
              AND ($focus_id IS NULL OR session_id = $focus_id)
            ORDER BY received_at ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$focus_id", (object?)focusSessionId ?? DBNull.Value);
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
        DateTime from, DateTime to, CurrentContext? currentContext = null, bool focusSessionsOnly = false) =>
        ReadAppsAsync(from, to, currentContext, focusSessionsOnly: focusSessionsOnly);

    public async Task<IReadOnlyList<ActivityJournalEntry>> ReadActivityJournalAsync(DateTime from, DateTime to,
        bool focusSessionsOnly = false)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, window_title, started_at, ended_at FROM window_sessions
            WHERE julianday(ended_at) > julianday($from) AND julianday(started_at) < julianday($to)
              AND ($focus_only = 0 OR EXISTS (SELECT 1 FROM focus_sessions focus WHERE focus.session_id = window_sessions.session_id))
            ORDER BY julianday(started_at) DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$focus_only", focusSessionsOnly ? 1 : 0);
        var entries = new List<ActivityJournalEntry>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var start = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var end = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            entries.Add(new ActivityJournalEntry(reader.GetString(0), reader.GetString(1),
                start < from ? from : start, end > to ? to : end));
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
        CurrentContext? context, string? focusSessionId = null,
        bool focusSessionsOnly = false)
    {
        using var connection = database.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, started_at, ended_at FROM window_sessions
            WHERE julianday(ended_at) > julianday($from) AND julianday(started_at) < julianday($to)
              AND ($focus_id IS NULL OR session_id = $focus_id)
              AND ($focus_only = 0 OR EXISTS (SELECT 1 FROM focus_sessions focus WHERE focus.session_id = window_sessions.session_id));
            """;
        command.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$focus_id", (object?)focusSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$focus_only", focusSessionsOnly ? 1 : 0);
        var intervals = new List<(string App, DateTime Start, DateTime End)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            intervals.Add((reader.GetString(0),
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        if (context is { DurationSeconds: > 0 } && !string.IsNullOrWhiteSpace(context.ActiveProcess))
        {
            var liveStart = context.LastUpdated.AddSeconds(-context.DurationSeconds);
            intervals.Add((context.ActiveProcess, liveStart, context.LastUpdated));
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

using System.IO;
using System.Text.Json;

namespace Altong.Client.Services;

public sealed record QuickMemoEntry(string Id, string Text, DateTimeOffset CreatedAt, string? SessionId = null, bool IsDraft = false)
{
    public string WhenText => CreatedAt.ToLocalTime().ToString("M월 d일 HH:mm");
    public string ReportWhenText => WhenText;
}

/// <summary>Session-owned notes and drafts; saving a note clears only that session's draft atomically.</summary>
public sealed class QuickMemoStore
{
    private sealed record SessionDraft(string SessionId, string Text, DateTimeOffset UpdatedAt);
    private sealed record Document(int Version, string Draft, QuickMemoEntry[] Entries)
    {
        public string? DraftSessionId { get; init; }
        public DateTimeOffset? DraftUpdatedAt { get; init; }
        public SessionDraft[] SessionDrafts { get; init; } = [];
    }
    private readonly string? _path;
    private readonly object _sync = new();
    private volatile Document _document = new(1, "", []);
    public string Draft => _document.Draft;
    public string? DraftSessionId => _document.DraftSessionId;
    public IReadOnlyList<QuickMemoEntry> Entries => _document.Entries;
    public string? LoadError { get; private set; }

    // No path means an isolated in-memory store, also used by standalone panel hosts.
    public QuickMemoStore(string? path = null)
    {
        _path = path;
        Reload();
    }

    public static QuickMemoStore OpenDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altong", "quick-memos.json"));

    public void Reload()
    {
        lock (_sync)
        {
            try
            {
                if (_path is null || !File.Exists(_path)) { LoadError = null; return; }
                var loaded = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
                if (loaded is not { Version: 1, Draft: not null, Entries: not null, SessionDrafts: not null } ||
                    loaded.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Id) ||
                        string.IsNullOrWhiteSpace(entry.Text) || entry.CreatedAt == default) ||
                    loaded.Entries.Select(entry => entry.Id).Distinct().Count() != loaded.Entries.Length ||
                    loaded.SessionDrafts.Any(draft => draft is null || string.IsNullOrWhiteSpace(draft.SessionId) || draft.Text is null) ||
                    loaded.SessionDrafts.Select(draft => draft.SessionId).Distinct().Count() != loaded.SessionDrafts.Length)
                    throw new JsonException();
                // Preserve drafts from the previous file format under their original session.
                if (loaded.DraftSessionId is { Length: > 0 } owner && !string.IsNullOrWhiteSpace(loaded.Draft) &&
                    !loaded.SessionDrafts.Any(draft => draft.SessionId == owner))
                    loaded = loaded with { SessionDrafts = [.. loaded.SessionDrafts,
                        new(owner, loaded.Draft, loaded.DraftUpdatedAt ?? DateTimeOffset.UtcNow)] };
                _document = loaded;
                LoadError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoadError = "메모를 불러오지 못했어요. 다시 불러오기를 눌러 주세요.";
            }
        }
    }

    public void SaveDraft(string text, string? sessionId = null)
    {
        lock (_sync)
        {
            string? owner = string.IsNullOrWhiteSpace(text) ? null : sessionId;
            if (Draft != text || DraftSessionId != owner ||
                (sessionId is not null && GetDraftForSession(sessionId) != (string.IsNullOrWhiteSpace(text) ? "" : text)))
            {
                var now = DateTimeOffset.UtcNow;
                var drafts = _document.SessionDrafts.Where(draft => draft.SessionId != sessionId).ToList();
                if (owner is not null) drafts.Add(new(owner, text, now));
                Write(_document with { Draft = text, DraftSessionId = owner, DraftUpdatedAt = now, SessionDrafts = drafts.ToArray() });
            }
        }
    }

    public QuickMemoEntry? Save(string text, string? sessionId = null)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var entry = new QuickMemoEntry(Guid.NewGuid().ToString("N"), text.Trim(), DateTimeOffset.UtcNow, sessionId);
            bool clearCurrent = DraftSessionId == sessionId;
            Write(_document with
            {
                Draft = clearCurrent ? "" : Draft,
                DraftSessionId = clearCurrent ? null : DraftSessionId,
                DraftUpdatedAt = clearCurrent ? null : _document.DraftUpdatedAt,
                SessionDrafts = _document.SessionDrafts.Where(draft => draft.SessionId != sessionId).ToArray(),
                Entries = [entry, .. _document.Entries]
            });
            return entry;
        }
    }

    public void DeleteSession(string sessionId, IReadOnlyCollection<string>? memoIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_sync)
        {
            var ids = memoIds is null ? new HashSet<string>() : new HashSet<string>(memoIds, StringComparer.Ordinal);
            bool clearLast = _document.DraftSessionId == sessionId;
            Write(_document with
            {
                Entries = _document.Entries.Where(entry => entry.SessionId != sessionId && !ids.Contains(entry.Id)).ToArray(),
                SessionDrafts = _document.SessionDrafts.Where(draft => draft.SessionId != sessionId).ToArray(),
                Draft = clearLast ? "" : _document.Draft,
                DraftSessionId = clearLast ? null : _document.DraftSessionId,
                DraftUpdatedAt = clearLast ? null : _document.DraftUpdatedAt
            });
        }
    }

    public void ClearExceptSession(string? activeSessionId)
    {
        lock (_sync)
        {
            bool keepLast = activeSessionId is not null && _document.DraftSessionId == activeSessionId;
            Write(new(1, keepLast ? _document.Draft : "", activeSessionId is null ? []
                : _document.Entries.Where(entry => entry.SessionId == activeSessionId).ToArray())
            {
                DraftSessionId = keepLast ? activeSessionId : null,
                DraftUpdatedAt = keepLast ? _document.DraftUpdatedAt : null,
                SessionDrafts = activeSessionId is null ? []
                    : _document.SessionDrafts.Where(draft => draft.SessionId == activeSessionId).ToArray()
            });
        }
    }

    public string GetDraftForSession(string? sessionId) => sessionId is null ? ""
        : _document.SessionDrafts.FirstOrDefault(draft => draft.SessionId == sessionId)?.Text ?? "";

    public IReadOnlyList<QuickMemoEntry> GetSavedForSession(string? sessionId) => sessionId is null ? []
        : _document.Entries.Where(entry => entry.SessionId == sessionId).ToArray();

    public IReadOnlyList<QuickMemoEntry> GetForSession(string sessionId, DateTime startedAt, DateTime endedAt)
    {
        var snapshot = _document;
        // Untagged notes written by the first memo version can still be matched by their actual save time.
        var entries = snapshot.Entries.Where(entry => entry.SessionId == sessionId ||
            (entry.SessionId is null && entry.CreatedAt.UtcDateTime >= startedAt && entry.CreatedAt.UtcDateTime < endedAt)).ToList();
        if (snapshot.SessionDrafts.FirstOrDefault(draft => draft.SessionId == sessionId) is { } draft && !string.IsNullOrWhiteSpace(draft.Text))
            entries.Add(new("draft-" + sessionId, draft.Text, draft.UpdatedAt, sessionId, true));
        return entries.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    private void Write(Document updated)
    {
        if (LoadError is not null) throw new IOException(LoadError);
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temporary = _path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, updated);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        _document = updated;
    }
}

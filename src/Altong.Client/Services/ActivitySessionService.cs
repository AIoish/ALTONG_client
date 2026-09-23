using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;

namespace Altong.Client.Services;

/// <summary>현재 활동 기록의 생명주기와 재실행 복구 상태를 관리한다.</summary>
public sealed class ActivitySessionService(SqliteActivitySessionRepository repository)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastTouchAt = DateTime.MinValue;

    public ActivitySessionRecord? Current { get; private set; }
    public bool IsRecording => Current?.Status == ActivitySessionStatus.Active;
    public event EventHandler? StateChanged;

    public async Task RestoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Current = await repository.GetOpenAsync().ConfigureAwait(false);
            _lastTouchAt = Current?.LastSeenAt ?? DateTime.MinValue;
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ActivitySessionRecord> StartAsync(DateTime startedAt)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is { Status: ActivitySessionStatus.Active })
                return Current;

            var existing = await repository.GetOpenAsync().ConfigureAwait(false);
            if (existing is not null)
            {
                Current = existing;
                return existing;
            }

            var utc = startedAt.ToUniversalTime();
            var session = new ActivitySessionRecord(Guid.NewGuid().ToString("N"), utc, LastSeenAt: utc);
            await repository.InsertAsync(session).ConfigureAwait(false);
            Current = session;
            _lastTouchAt = utc;
            return session;
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<ActivitySessionRecord?> CompleteAsync(DateTime endedAt)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { Status: ActivitySessionStatus.Active } current)
                return null;

            var utc = endedAt.ToUniversalTime();
            await repository.CompleteAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            var completed = current with
            {
                EndedAt = utc,
                LastSeenAt = utc,
                Status = ActivitySessionStatus.Completed,
            };
            Current = null;
            return completed;
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task TouchIfDueAsync(DateTime now, TimeSpan? interval = null)
    {
        var utc = now.ToUniversalTime();
        if (!IsRecording || utc - _lastTouchAt < (interval ?? TimeSpan.FromSeconds(15)))
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { Status: ActivitySessionStatus.Active } current ||
                utc - _lastTouchAt < (interval ?? TimeSpan.FromSeconds(15)))
                return;
            await repository.TouchAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            Current = current with { LastSeenAt = utc };
            _lastTouchAt = utc;
        }
        finally { _gate.Release(); }
    }
}

using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;

namespace Altong.Client.Services;

/// <summary>현재 활동 기록의 생명주기와 재실행 복구 상태를 관리한다.</summary>
public sealed class ActivitySessionService(SqliteActivitySessionRepository repository)
{
    private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastTouchAt = DateTime.MinValue;
    private TimeSpan _recordedDuration;
    private DateTime? _activeSegmentStartedAt;
    private TimeSpan _capturedDuration;
    private DateTime? _captureStartedAt;

    public ActivitySessionRecord? Current { get; private set; }
    public ActivitySessionRecord? LastCompleted { get; private set; }
    public bool IsRecording => Current?.Status == ActivitySessionStatus.Active;
    public bool UsesFocusCapture { get; private set; }
    public event EventHandler? StateChanged;

    public async Task RestoreAsync(DateTime? resumedAt = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Current = await repository.GetOpenAsync().ConfigureAwait(false);
            LastCompleted = await repository.GetLatestCompletedAsync().ConfigureAwait(false);
            if (Current is { Status: ActivitySessionStatus.Active } current)
            {
                var now = (resumedAt ?? DateTime.UtcNow).ToUniversalTime();
                UsesFocusCapture = await repository.UsesFocusCaptureAsync(current.ActivitySessionId).ConfigureAwait(false);
                if (UsesFocusCapture)
                {
                    await repository.EndCaptureSegmentAsync(current.ActivitySessionId, now, useLastSeenAt: true).ConfigureAwait(false);
                    _capturedDuration = await repository.GetCapturedDurationAsync(current.ActivitySessionId, now).ConfigureAwait(false) ?? TimeSpan.Zero;
                }
                _captureStartedAt = null;
                await repository.EndActiveSegmentAsync(
                    current.ActivitySessionId,
                    current.LastSeenAt ?? current.StartedAt,
                    useLastSeenAt: true).ConfigureAwait(false);
                var previousDuration = await repository.GetRecordedDurationAsync(current.ActivitySessionId).ConfigureAwait(false);
                if (previousDuration is null && current.LastSeenAt is { } lastSeen && lastSeen > current.StartedAt)
                {
                    await repository.BackfillLegacySegmentAsync(
                        current.ActivitySessionId,
                        current.StartedAt,
                        lastSeen).ConfigureAwait(false);
                    previousDuration = lastSeen - current.StartedAt;
                }
                await repository.StartSegmentAsync(current.ActivitySessionId, now).ConfigureAwait(false);
                await repository.TouchAsync(current.ActivitySessionId, now).ConfigureAwait(false);
                _recordedDuration = previousDuration ?? NonNegativeDuration(
                    current.LastSeenAt ?? current.StartedAt,
                    current.StartedAt);
                _activeSegmentStartedAt = now;
                Current = current with { LastSeenAt = now };
                _lastTouchAt = now;
            }
            else
            {
                UsesFocusCapture = false;
                _capturedDuration = TimeSpan.Zero;
                _captureStartedAt = null;
                _lastTouchAt = DateTime.MinValue;
                _recordedDuration = TimeSpan.Zero;
                _activeSegmentStartedAt = null;
            }
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
                var resumedAtUtc = startedAt.ToUniversalTime();
                UsesFocusCapture = await repository.UsesFocusCaptureAsync(existing.ActivitySessionId).ConfigureAwait(false);
                if (UsesFocusCapture)
                {
                    await repository.EndCaptureSegmentAsync(existing.ActivitySessionId, resumedAtUtc, useLastSeenAt: true).ConfigureAwait(false);
                    _capturedDuration = await repository.GetCapturedDurationAsync(existing.ActivitySessionId, resumedAtUtc).ConfigureAwait(false) ?? TimeSpan.Zero;
                }
                _captureStartedAt = null;
                await repository.EndActiveSegmentAsync(
                    existing.ActivitySessionId,
                    existing.LastSeenAt ?? existing.StartedAt,
                    useLastSeenAt: true).ConfigureAwait(false);
                var previousDuration = await repository.GetRecordedDurationAsync(existing.ActivitySessionId).ConfigureAwait(false);
                if (previousDuration is null && existing.LastSeenAt is { } lastSeen && lastSeen > existing.StartedAt)
                {
                    await repository.BackfillLegacySegmentAsync(
                        existing.ActivitySessionId,
                        existing.StartedAt,
                        lastSeen).ConfigureAwait(false);
                    previousDuration = lastSeen - existing.StartedAt;
                }
                await repository.StartSegmentAsync(existing.ActivitySessionId, resumedAtUtc).ConfigureAwait(false);
                await repository.TouchAsync(existing.ActivitySessionId, resumedAtUtc).ConfigureAwait(false);
                _recordedDuration = previousDuration ?? NonNegativeDuration(
                    existing.LastSeenAt ?? existing.StartedAt,
                    existing.StartedAt);
                _activeSegmentStartedAt = resumedAtUtc;
                Current = existing with { LastSeenAt = resumedAtUtc };
                _lastTouchAt = resumedAtUtc;
                return Current;
            }

            var utc = startedAt.ToUniversalTime();
            var session = new ActivitySessionRecord(Guid.NewGuid().ToString("N"), utc, LastSeenAt: utc);
            await repository.InsertAsync(session).ConfigureAwait(false);
            await repository.EnableFocusCaptureAsync(session.ActivitySessionId).ConfigureAwait(false);
            await repository.StartSegmentAsync(session.ActivitySessionId, utc).ConfigureAwait(false);
            Current = session;
            UsesFocusCapture = true;
            _capturedDuration = TimeSpan.Zero;
            _captureStartedAt = null;
            _recordedDuration = TimeSpan.Zero;
            _activeSegmentStartedAt = utc;
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
        await TouchIfDueAsync(endedAt).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { Status: ActivitySessionStatus.Active } current)
                return null;

            var utc = endedAt.ToUniversalTime();
            await repository.CompleteAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            if (_captureStartedAt is { } captureStart && utc > captureStart)
                _capturedDuration += utc - captureStart;
            _captureStartedAt = null;
            AddActiveSegmentDuration(utc);
            var completed = current with
            {
                EndedAt = utc,
                LastSeenAt = utc,
                Status = ActivitySessionStatus.Completed,
            };
            LastCompleted = completed;
            Current = null;
            _activeSegmentStartedAt = null;
            return completed;
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task PauseForShutdownAsync(DateTime pausedAt)
    {
        await TouchIfDueAsync(pausedAt).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { Status: ActivitySessionStatus.Active } current)
                return;

            var utc = pausedAt.ToUniversalTime();
            if (_captureStartedAt is { } captureStart)
            {
                await repository.EndCaptureSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
                _capturedDuration += NonNegativeDuration(utc, captureStart);
                _captureStartedAt = null;
            }
            await repository.EndActiveSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            AddActiveSegmentDuration(utc);
            await repository.TouchAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            Current = current with { LastSeenAt = utc };
            _lastTouchAt = utc;
        }
        finally { _gate.Release(); }
    }

    public TimeSpan GetElapsedDuration(DateTime now)
    {
        if (!IsRecording || _activeSegmentStartedAt is not { } startedAt)
            return _recordedDuration;
        var currentSegment = now.ToUniversalTime() - startedAt;
        return _recordedDuration + (currentSegment > TimeSpan.Zero ? currentSegment : TimeSpan.Zero);
    }

    public async Task<TimeSpan?> GetRecordedDurationAsync(string activitySessionId)
    {
        var captured = await repository.GetCapturedDurationAsync(activitySessionId, DateTime.UtcNow).ConfigureAwait(false);
        return captured ?? await repository.GetRecordedDurationAsync(activitySessionId).ConfigureAwait(false);
    }

    public Task<bool> UsesFocusCaptureAsync(string activitySessionId) =>
        repository.UsesFocusCaptureAsync(activitySessionId);

    public Task<IReadOnlyList<(DateTime Start, DateTime End)>> GetCaptureIntervalsAsync(
        string activitySessionId, DateTime upperBound) =>
        repository.GetCaptureIntervalsAsync(activitySessionId, upperBound);

    public async Task SetFocusCaptureAsync(bool enabled, DateTime changedAt)
    {
        await TouchIfDueAsync(changedAt).ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!UsesFocusCapture || Current is not { Status: ActivitySessionStatus.Active } current)
                return;
            var utc = changedAt.ToUniversalTime();
            if (enabled && _captureStartedAt is null)
            {
                await repository.StartCaptureSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
                _captureStartedAt = utc;
            }
            else if (!enabled && _captureStartedAt is { } started)
            {
                await repository.EndCaptureSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
                _capturedDuration += NonNegativeDuration(utc, started);
                _captureStartedAt = null;
            }
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task TouchIfDueAsync(DateTime now, TimeSpan? interval = null)
    {
        var utc = now.ToUniversalTime();
        var elapsedSinceTouch = utc - _lastTouchAt;
        if (!IsRecording ||
            elapsedSinceTouch <= MaximumContinuousGap && elapsedSinceTouch < (interval ?? TimeSpan.FromSeconds(2)))
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Current is not { Status: ActivitySessionStatus.Active } current ||
                utc - _lastTouchAt <= MaximumContinuousGap &&
                utc - _lastTouchAt < (interval ?? TimeSpan.FromSeconds(2)))
                return;
            if (utc - _lastTouchAt > MaximumContinuousGap)
            {
                if (_captureStartedAt is { } capturedAt)
                {
                    var lastSeen = current.LastSeenAt ?? _lastTouchAt;
                    await repository.EndCaptureSegmentAsync(current.ActivitySessionId, lastSeen, useLastSeenAt: true).ConfigureAwait(false);
                    _capturedDuration += NonNegativeDuration(lastSeen, capturedAt);
                    await repository.StartCaptureSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
                    _captureStartedAt = utc;
                }
                // 긴 공백은 앱/PC가 실제로 관측되지 않은 구간으로 보고 합산하지 않는다.
                await repository.EndActiveSegmentAsync(
                    current.ActivitySessionId,
                    current.LastSeenAt ?? _lastTouchAt,
                    useLastSeenAt: true).ConfigureAwait(false);
                AddActiveSegmentDuration(current.LastSeenAt ?? _lastTouchAt);
                await repository.StartSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
                _activeSegmentStartedAt = utc;
            }
            else
            {
                await repository.TouchActiveSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
                if (_captureStartedAt is not null)
                    await repository.TouchCaptureSegmentAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            }
            await repository.TouchAsync(current.ActivitySessionId, utc).ConfigureAwait(false);
            Current = current with { LastSeenAt = utc };
            _lastTouchAt = utc;
        }
        finally { _gate.Release(); }
    }

    private void AddActiveSegmentDuration(DateTime endedAt)
    {
        if (_activeSegmentStartedAt is { } startedAt && endedAt > startedAt)
            _recordedDuration += endedAt - startedAt;
        _activeSegmentStartedAt = null;
    }

    private static TimeSpan NonNegativeDuration(DateTime end, DateTime start) =>
        end > start ? end - start : TimeSpan.Zero;
}

using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 알림 리스너, 활성 창 트래커, AI 필터 엔진, SQLite DB 저장소를 결합하여
/// 알림 인입 시 실시간 평가 및 자동 적재를 총괄하는 파이프라인 코디네이터.
/// </summary>
public sealed class NotificationPipelineCoordinator : IDisposable
{
    private readonly IWindowsNotificationListener _listener;
    private readonly IActiveWindowTracker _windowTracker;
    private readonly INotificationRepository _notificationRepository;
    private readonly IFilterEngine _filterEngine;
    private readonly Func<bool> _isFocusModeEnabled;
    private readonly Func<string?> _getCurrentSessionId;

    private readonly object _syncRoot = new();
    private bool _isDisposed;
    private readonly Dictionary<string, List<Task>> _sessionWrites = new();

    /// <summary>
    /// 알림이 평가되고 DB에 저장된 직후 발행되는 이벤트 (UI 표시 및 독 카운터 연동용).
    /// </summary>
    public event EventHandler<NotificationRecord>? NotificationProcessed;

    public NotificationPipelineCoordinator(
        IWindowsNotificationListener listener,
        IActiveWindowTracker windowTracker,
        INotificationRepository notificationRepository,
        IFilterEngine filterEngine,
        Func<bool> isFocusModeEnabled,
        Func<string?> getCurrentSessionId)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        _windowTracker = windowTracker ?? throw new ArgumentNullException(nameof(windowTracker));
        _notificationRepository = notificationRepository ?? throw new ArgumentNullException(nameof(notificationRepository));
        _filterEngine = filterEngine ?? throw new ArgumentNullException(nameof(filterEngine));
        _isFocusModeEnabled = isFocusModeEnabled ?? throw new ArgumentNullException(nameof(isFocusModeEnabled));
        _getCurrentSessionId = getCurrentSessionId ?? throw new ArgumentNullException(nameof(getCurrentSessionId));

        _listener.NotificationReceived += OnNotificationReceived;
    }

    public async Task<bool> StartAsync()
    {
        return await _listener.StartAsync().ConfigureAwait(false);
    }

    public void Stop()
    {
        _listener.Stop();
    }

    private async void OnNotificationReceived(object? sender, RawNotification notification)
    {
        TaskCompletionSource? completion = null;
        try
        {
            // 1. 현재 활성 창 맥락 스냅샷 획득
            CurrentContext context = _windowTracker.CurrentContext;

            // 2. 집중 모드 여부 및 세션 ID 확인
            bool isFocusMode;
            string? sessionId;
            lock (_syncRoot)
            {
                if (_isDisposed) return;
                isFocusMode = _isFocusModeEnabled();
                sessionId = isFocusMode ? _getCurrentSessionId() : null;
                if (sessionId is not null)
                {
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_sessionWrites.TryGetValue(sessionId, out var writes))
                        _sessionWrites[sessionId] = writes = new List<Task>();
                    writes.RemoveAll(task => task.IsCompletedSuccessfully);
                    writes.Add(completion.Task);
                    _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }

            // 3. AI 필터 엔진 평가
            FilterResult filterResult = await _filterEngine.EvaluateAsync(notification, context).ConfigureAwait(false);

            // 집중 모드가 켜져 있을 때만 차단/통과 플래그 확정 (평상시에는 일반 수신 null)
            bool? isPassed = isFocusMode ? filterResult.IsPassed : null;

            // 4. NotificationRecord 생성 및 DB 적재
            var record = new NotificationRecord(
                Id: notification.Id,
                AppName: notification.AppName,
                Sender: string.IsNullOrEmpty(notification.Sender) ? null : notification.Sender,
                Title: notification.Title,
                Body: notification.Body,
                ReceivedAt: notification.Timestamp,
                IsPassed: isPassed,
                UrgencyScore: filterResult.UrgencyScore,
                RelevanceScore: filterResult.RelevanceScore,
                Category: filterResult.Category,
                AiSummaryReason: filterResult.AiSummaryReason,
                SessionId: sessionId);

            // 4. SQLite DB 저장 (집중 모드 활성화 시에만 영속화)
            if (isFocusMode)
            {
                await _notificationRepository.InsertAsync(record).ConfigureAwait(false);
            }
            // The report depends on persistence, not logging or UI subscriber success.
            completion?.TrySetResult();

            // 5. 콘솔 실시간 로깅
            string senderPart = string.IsNullOrEmpty(notification.Sender) ? "" : $" ({notification.Sender})";
            string modeTag = isFocusMode
                ? (filterResult.IsPassed ? "[PASS]" : "[BLOCK]")
                : "[RECV]";
            string activeApp = string.IsNullOrEmpty(context.ActiveProcess) ? "None" : context.ActiveProcess;
            string dbStatus = isFocusMode ? "DB 저장 완료" : "일반 수신 (집중 모드 OFF, 미저장)";

            string logMsg = $"[NotificationPipeline] {modeTag,-7} {notification.AppName}{senderPart}: " +
                $"'{Truncate(notification.Title, 30)}' | 현재: {activeApp} ({context.DurationSeconds}s) -> {dbStatus}";
            AppLogger.Info(logMsg);

            // 6. UI 구독자에게 이벤트 발행
            NotificationProcessed?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            AppLogger.Error($"[NotificationPipeline] 알림 파이프라인 처리 오류: {ex.Message}", ex);
        }
    }

    /// <summary>Call after focus mode is disabled, before collecting that session's report.</summary>
    public Task DrainSessionAsync(string sessionId)
    {
        lock (_syncRoot)
        {
            if (!_sessionWrites.Remove(sessionId, out var writes)) return Task.CompletedTask;
            return Task.WhenAll(writes);
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _listener.NotificationReceived -= OnNotificationReceived;
        _listener.Dispose();
    }
}

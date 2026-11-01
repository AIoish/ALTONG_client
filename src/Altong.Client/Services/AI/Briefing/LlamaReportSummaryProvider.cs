using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services.Calendar;

namespace Altong.Client.Services.AI.Briefing;

/// <summary>
/// Qwen GGUF 온디바이스 SLM 엔진을 이용해 세션 차단 알림의 핵심 요약과 추천 일정을 제공하는 IReportSummaryProvider 구현체.
/// 결정론적 ID(Deterministic SHA256) 생성과 세션별 비동기 작업 캐싱을 통해 안정성을 보장합니다.
/// </summary>
public sealed class LlamaReportSummaryProvider : IReportSummaryProvider, IDisposable
{
    private readonly INotificationRepository _notifications;
    private readonly LlamaBriefingEngine _engine;
    private readonly Func<DateTime> _nowProvider;
    private readonly ConcurrentDictionary<string, Lazy<Task<ReportSummarySupply>>> _sessionTaskCache = new(StringComparer.Ordinal);

    public LlamaReportSummaryProvider(
        INotificationRepository notifications,
        string modelPath,
        Func<DateTime>? nowProvider = null)
    {
        _notifications = notifications;
        _engine = new LlamaBriefingEngine(modelPath);
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    public async Task<ReportSummarySupply> ReadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new ReportSummarySupply([]);
        if (!Regex.IsMatch(sessionId, @"\A[a-zA-Z0-9_-]{1,128}\z"))
            throw new InvalidDataException("잘못된 집중 세션 ID입니다.");

        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var lazyTask = _sessionTaskCache.GetOrAdd(
                sessionId,
                id => new Lazy<Task<ReportSummarySupply>>(
                    () => GenerateSummarySupplyAsync(id, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            Task<ReportSummarySupply> task;
            try
            {
                task = lazyTask.Value;
            }
            catch
            {
                _sessionTaskCache.TryRemove(KeyValuePair.Create(sessionId, lazyTask));
                throw;
            }

            if (task.IsFaulted || task.IsCanceled)
            {
                _sessionTaskCache.TryRemove(KeyValuePair.Create(sessionId, lazyTask));
                continue;
            }

            try
            {
                return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    _sessionTaskCache.TryRemove(KeyValuePair.Create(sessionId, lazyTask));
                }
                throw;
            }
        }
    }

    public static string NormalizeDisplayAppName(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return "Unknown";
        var trimmed = appName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].Trim();
        }
        return string.IsNullOrWhiteSpace(trimmed) ? "Unknown" : trimmed;
    }

    private async Task<ReportSummarySupply> GenerateSummarySupplyAsync(string sessionId, CancellationToken cancellationToken)
    {
        // 1. 해당 세션의 차단된 알림 목록 조회 (최대 50건)
        var blocked = await _notifications.GetBlockedNotificationsAsync(sessionId, 50).ConfigureAwait(false);
        if (blocked.Count == 0)
        {
            return new ReportSummarySupply([]);
        }

        // 2. 대화방/발신자/앱 단위로 알림 그룹화 (Sender가 있으면 Sender 우선, 없으면 Title 사용)
        var groups = blocked
            .GroupBy(n => (
                AppKey: BriefingPromptBuilder.NormalizeAppIdentity(n.AppName),
                GroupKey: (!string.IsNullOrWhiteSpace(n.Sender) ? n.Sender : (n.Title ?? "")).Trim()
            ))
            .ToList();

        var summaryItems = new List<NotificationSummaryItem>();
        var referenceNow = _nowProvider();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupNotifications = group.OrderBy(n => n.ReceivedAt).ToList();
            var appName = NormalizeDisplayAppName(groupNotifications[0].AppName);
            var sender = groupNotifications.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.Sender))?.Sender?.Trim() ?? "";

            string summaryText;
            string primaryCategory;

            // 3. AI 모델 호출 (가능한 경우) 또는 Fallback
            var aiOutput = await _engine.SummarizeNotificationGroupAsync(
                appName, sender, groupNotifications, maxSummaryLines: 3, cancellationToken).ConfigureAwait(false);

            if (aiOutput is not null && aiOutput.SummaryLines.Count > 0)
            {
                summaryText = string.Join(" ", aiOutput.SummaryLines);
                primaryCategory = aiOutput.PrimaryCategory;
            }
            else
            {
                // Fallback: 최근 알림 1~2개 텍스트 조합
                var latest = groupNotifications[^1];
                var senderPrefix = string.IsNullOrWhiteSpace(latest.Sender) ? "" : $"{latest.Sender}: ";
                summaryText = groupNotifications.Count == 1
                    ? $"{senderPrefix}{latest.Body}"
                    : (string.IsNullOrWhiteSpace(latest.Sender)
                        ? $"{latest.Body} 외 {groupNotifications.Count - 1}건"
                        : $"{latest.Sender} 외 {groupNotifications.Count - 1}건: {latest.Body}");
                primaryCategory = "일반 업무";
            }

            var notiIds = groupNotifications.Select(n => n.Id).ToList();
            var summaryId = ComputeDeterministicId("sum", sessionId, group.Key.GroupKey, notiIds);
            var eventId = ComputeDeterministicId("evt", sessionId, group.Key.GroupKey, notiIds);

            // 4. 일정 추출기 실행
            var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
                summaryText,
                primaryCategory,
                groupNotifications.Select(n => n.Body).ToList(),
                referenceNow,
                eventId: eventId);

            var item = new NotificationSummaryItem(
                SummaryId: summaryId,
                Text: summaryText,
                IsScheduleRelated: isScheduleRelated,
                Schedule: schedule)
            {
                AppName = appName
            };

            summaryItems.Add(item);
        }

        return new ReportSummarySupply(summaryItems, IsDemo: false);
    }

    public static string ComputeDeterministicId(string prefix, string sessionId, string groupKey, IEnumerable<string> notificationIds)
    {
        var raw = $"{sessionId}:{groupKey}:{string.Join(",", notificationIds.OrderBy(id => id, StringComparer.Ordinal))}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"{prefix}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    public void Dispose() => _engine.Dispose();
}

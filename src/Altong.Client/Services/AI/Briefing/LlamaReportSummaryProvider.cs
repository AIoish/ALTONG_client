using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services.Calendar;
using Altong.Client.Services.Notifications;

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
                    // 모델 로딩과 추론은 동기 네이티브 호출이 섞여 있어 UI 스레드가 멈추지 않도록 스레드 풀에서 실행한다.
                    () => Task.Run(() => GenerateSummarySupplyAsync(id, cancellationToken), cancellationToken),
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

        // 카카오톡 팝업에서 본문을 읽지 못한 알림은 요약할 내용이 없다. 본문 자리에 앱이 대신 넣은 문구
        // ("(내용을 읽지 못함)" 등)를 모델이 메시지로 읽고 요약하지 않도록 뺀다. 알림 자체는 목록에 그대로 남는다.
        blocked = blocked.Where(n => !KakaoNotificationText.IsUnreadBody(n.Body)).ToList();
        if (blocked.Count == 0)
        {
            return new ReportSummarySupply([]);
        }

        // 2. AI 레포와 같은 규칙(앱·발신자·카카오톡 방·30분·대화 연결)으로 묶고,
        //    모델 학습 범위(그룹당 7개)나 문맥을 넘는 그룹은 시간 순서대로 고르게 나눈다.
        using var session = await _engine.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        var chunks = BriefingGrouping.Group(blocked)
            .SelectMany(group => BriefingGrouping.SplitEvenly(group, BriefingGrouping.MaxNotificationsPerChunk,
                session is null ? null : chunk => session.Fits(
                    NormalizeDisplayAppName(chunk[0].AppName), BriefingGrouping.ModelSender(chunk[0]), chunk)))
            .ToList();

        var summaryItems = new List<NotificationSummaryItem>();
        var referenceNow = _nowProvider();

        foreach (var groupNotifications in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var first = groupNotifications[0];
            var appName = NormalizeDisplayAppName(first.AppName);
            var sender = NotificationSenderResolver.Resolve(first.AppName, first.Sender, first.Title) ?? "";
            var room = BriefingGrouping.KakaoRoom(first)?.Trim();
            var groupKey = $"{BriefingGrouping.AppIdentity(first.AppName)}|{BriefingGrouping.NormalizeIdentity(BriefingGrouping.ModelSender(first))}|{room}";

            string summaryText;
            string primaryCategory;

            // 3. AI 모델 호출 (가능한 경우) 또는 Fallback
            var aiOutput = session is null
                ? null
                : await session.SummarizeAsync(appName, BriefingGrouping.ModelSender(first), groupNotifications,
                    maxSummaryLines: 3, cancellationToken).ConfigureAwait(false);

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
            var summaryId = ComputeDeterministicId("sum", sessionId, groupKey, notiIds);
            var eventId = ComputeDeterministicId("evt", sessionId, groupKey, notiIds);

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
                AppName = appName,
                Sender = string.IsNullOrEmpty(sender) ? null : sender,
                Room = string.IsNullOrEmpty(room) ? null : room
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

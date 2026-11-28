using Altong.Client.Data.Models;
using Altong.Client.Models;

namespace Altong.Client.Services.AI.Briefing;

/// <summary>그룹 ID가 없던 저장 요약도 원본 알림과 결정론적 요약 ID가 일치할 때만 그룹을 복원한다.</summary>
public static class SummaryGroupResolver
{
    public static string GroupKey(NotificationRecord first) =>
        $"{BriefingGrouping.AppIdentity(first.AppName)}|{BriefingGrouping.NormalizeIdentity(BriefingGrouping.ModelSender(first))}|{BriefingGrouping.KakaoRoom(first)?.Trim()}";

    public static IReadOnlyList<NotificationSummaryItem> Restore(
        IReadOnlyList<NotificationSummaryItem> items, string? sessionId, IReadOnlyList<NotificationRecord> notifications)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || items.All(item => !string.IsNullOrWhiteSpace(item.GroupId))) return items;
        var missing = items.Where(item => string.IsNullOrWhiteSpace(item.GroupId))
            .Select(item => item.SummaryId).ToHashSet(StringComparer.Ordinal);
        var matches = new Dictionary<string, string>(StringComparer.Ordinal);
        // 생성 시 저장소가 선택한 최근 50개와 같은 범위·정렬로 복원해야 그룹 경계가 유지된다.
        var blocked = notifications.Where(notification => notification.IsPassed == false)
            .OrderByDescending(notification => notification.ReceivedAt)
            .ThenByDescending(notification => notification.Id, StringComparer.Ordinal)
            .Take(BriefingGrouping.MaxNotificationsPerSession).ToArray();
        foreach (var group in BriefingGrouping.Group(blocked))
        {
            var groupId = LlamaReportSummaryProvider.ComputeDeterministicId("grp", sessionId, GroupKey(group[0]),
                group.Select(notification => notification.Id));
            // 이전 분할 크기는 모델 입력 한도에도 좌우되므로, 상한 안의 연속 알림만 대조한다.
            for (var start = 0; start < group.Count; start++)
            {
                var first = group[start];
                var oldSender = string.IsNullOrWhiteSpace(first.Sender) ? first.AppName : first.Sender.Trim();
                var oldKey = $"{BriefingGrouping.AppIdentity(first.AppName)}|{BriefingGrouping.NormalizeIdentity(oldSender)}|{BriefingGrouping.KakaoRoom(first)?.Trim()}";
                foreach (var key in new[] { GroupKey(first), oldKey }.Distinct(StringComparer.Ordinal))
                {
                    for (var count = 1; count <= Math.Min(BriefingGrouping.MaxNotificationsPerChunk, group.Count - start); count++)
                    {
                        var id = LlamaReportSummaryProvider.ComputeDeterministicId("sum", sessionId, key,
                            group.Skip(start).Take(count).Select(notification => notification.Id));
                        if (missing.Contains(id)) matches[id] = groupId;
                    }
                }
            }
        }
        return items.Select(item => string.IsNullOrWhiteSpace(item.GroupId) && matches.TryGetValue(item.SummaryId, out var groupId)
            ? item with { GroupId = groupId } : item).ToArray();
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Altong.Client.Data.Models;

namespace Altong.Client.Services.AI.Briefing;

/// <summary>
/// 차단 알림을 요약 단위로 묶는다. ALTONG_ai의 src/briefing/clustering.py(group_items) 규칙을 그대로 옮겼다.
/// 같은 앱·발신자·카카오톡 방이고, 그룹 첫 알림부터 30분 안이며, 단어를 공유하거나
/// 대화형 앱에서 5분 안에 이어진 메시지만 같은 그룹이 된다.
/// </summary>
public static class BriefingGrouping
{
    /// <summary>요약 모델(v8)이 학습한 그룹 크기 상한. 학습 데이터는 그룹당 알림 1~7개다.</summary>
    public const int MaxNotificationsPerChunk = 7;

    private static readonly TimeSpan MaxGroupSpan = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxSequenceGap = TimeSpan.FromMinutes(5);
    private static readonly Regex TokenPattern = new("[0-9A-Za-z가-힣_]+", RegexOptions.CultureInvariant);
    private static readonly Regex SequenceIdPattern = new(@"\A(?<prefix>.*?)(?<number>[0-9]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex UuidPattern = new(
        @"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ConversationAppKeys = new(StringComparer.Ordinal)
    {
        "kakaotalk", "slack", "teams", "microsoft teams", "discord", "telegram", "whatsapp", "messages", "messenger",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "관련", "대한", "부탁", "확인", "주세요", "합니다", "해주세요", "지금", "오늘", "내일", "모레",
        "오전", "오후", "예정", "일정", "안내", "알림", "the", "and", "for", "with",
    };

    private static readonly HashSet<string> UnknownKakaoRooms = new(StringComparer.Ordinal) { "카카오톡", "kakaotalk" };

    /// <summary>Python normalize_identity: NFKC, 소문자, 공백 정리.</summary>
    public static string NormalizeIdentity(string? value) =>
        string.Join(' ', (value ?? "").Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Python app_identity: 정규화 후 .exe 제거.</summary>
    public static string AppIdentity(string? appName) => BriefingPromptBuilder.NormalizeAppIdentity(appName);

    /// <summary>발신자가 비어 있으면 앱 이름을 발신자로 쓴다 (Python sqlite_adapter 규칙).</summary>
    public static string ModelSender(NotificationRecord notification) =>
        string.IsNullOrWhiteSpace(notification.Sender) ? notification.AppName : notification.Sender.Trim();

    /// <summary>카카오톡 알림의 title은 방 이름이다. 다른 앱은 null.</summary>
    public static string? KakaoRoom(NotificationRecord notification) =>
        AppIdentity(notification.AppName) == "kakaotalk" ? notification.Title : null;

    public static IReadOnlyList<IReadOnlyList<NotificationRecord>> Group(IEnumerable<NotificationRecord> notifications)
    {
        var groups = new List<RuleGroup>();
        var ordered = notifications
            .OrderBy(n => n.ReceivedAt)
            .ThenBy(n => n.Id, StringComparer.Ordinal);

        foreach (var notification in ordered)
        {
            var tokens = TextTokens(notification);
            var matching = groups.FirstOrDefault(group => group.Accepts(notification, tokens));
            if (matching is null)
            {
                matching = new RuleGroup(
                    AppIdentity(notification.AppName),
                    NormalizeIdentity(ModelSender(notification)),
                    KakaoRoomKey(notification));
                groups.Add(matching);
            }
            matching.Add(notification, tokens);
        }

        return groups.Select(group => (IReadOnlyList<NotificationRecord>)group.Items).ToList();
    }

    /// <summary>
    /// 그룹이 학습 범위(<paramref name="maxPerChunk"/>개)를 넘거나 <paramref name="fits"/>를 만족하지 못하면
    /// 시간 순서를 유지한 채 가장 적은 수의 덩어리로 고르게 나눈다. 예: 12개 → 6 + 6.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> SplitEvenly<T>(
        IReadOnlyList<T> items, int maxPerChunk, Func<IReadOnlyList<T>, bool>? fits = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerChunk, 1);
        if (items.Count == 0) return [];

        for (int count = (items.Count + maxPerChunk - 1) / maxPerChunk; ; count++)
        {
            var chunks = Enumerable.Range(0, count).Select(index =>
            {
                int start = items.Count * index / count;
                int end = items.Count * (index + 1) / count;
                return (IReadOnlyList<T>)items.Skip(start).Take(end - start).ToList();
            }).ToList();
            // 알림 하나도 들어가지 않으면 더 나눌 수 없으므로 그대로 둔다 (요약 단계에서 대체 요약으로 처리).
            if (count >= items.Count || fits is null || chunks.All(fits)) return chunks;
        }
    }

    private static string? KakaoRoomKey(NotificationRecord notification) =>
        KakaoRoom(notification) is { } room ? NormalizeIdentity(room) : null;

    private static HashSet<string> TextTokens(NotificationRecord notification)
    {
        var text = (KakaoRoomKey(notification) is not null
                ? notification.Body
                : $"{notification.Title} {notification.Body}")
            .Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return TokenPattern.Matches(text)
            .Select(match => match.Value)
            .Where(token => token.Length > 1 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static (string Prefix, long Number)? SequenceId(string? id)
    {
        var normalized = (id ?? "").Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0 || UuidPattern.IsMatch(normalized)) return null;
        var match = SequenceIdPattern.Match(normalized);
        if (!match.Success || !long.TryParse(match.Groups["number"].Value, out var number)) return null;
        return (NormalizeIdentity(match.Groups["prefix"].Value), number);
    }

    private static bool HasAdjacentSequenceId(NotificationRecord previous, NotificationRecord current) =>
        SequenceId(previous.Id) is { } before && SequenceId(current.Id) is { } after
        && before.Prefix == after.Prefix && after.Number == before.Number + 1;

    private sealed class RuleGroup(string appKey, string senderKey, string? roomKey)
    {
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
        public List<NotificationRecord> Items { get; } = [];

        public bool Accepts(NotificationRecord notification, HashSet<string> tokens)
        {
            if (AppIdentity(notification.AppName) != appKey) return false;
            if (NormalizeIdentity(ModelSender(notification)) != senderKey) return false;
            if (KakaoRoomKey(notification) != roomKey) return false;
            if (Items.Count == 0) return false;
            if (notification.ReceivedAt - Items[0].ReceivedAt > MaxGroupSpan) return false;
            if (_tokens.Overlaps(tokens)) return true;

            // 발신자가 없어 앱 이름으로 대신한 경우, 같은 앱의 무관한 대화가 합쳐지지 않도록 연속 메시지 규칙을 쓰지 않는다.
            if (senderKey == appKey) return false;
            if (!ConversationAppKeys.Contains(appKey)) return false;

            var previous = Items[^1];
            var gap = notification.ReceivedAt - previous.ReceivedAt;
            bool knownRoom = roomKey is not null && !UnknownKakaoRooms.Contains(roomKey);
            return gap >= TimeSpan.Zero && gap <= MaxSequenceGap
                && (knownRoom || HasAdjacentSequenceId(previous, notification));
        }

        public void Add(NotificationRecord notification, HashSet<string> tokens)
        {
            Items.Add(notification);
            _tokens.UnionWith(tokens);
        }
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using Altong.Client.Models;

namespace Altong.Client.Services.AI.Briefing;

/// <summary>
/// 알림 그룹 본문 및 AI 요약문에서 캘린더 일정을 추출하는 규칙 기반 추출기.
/// ALTONG_ai의 RuleBasedActionItemProvider 로직을 순수 C#으로 이식했습니다.
/// </summary>
public static class BriefingScheduleExtractor
{
    private static readonly Regex DatePattern = new(
        @"(?:(?<year>\d{4})년\s*)?(?<month>\d{1,2})월\s*(?<day>\d{1,2})일",
        RegexOptions.Compiled);

    private static readonly Regex RelativeDatePattern = new(
        @"(?<rel>오늘|내일|모레|글피)",
        RegexOptions.Compiled);

    private static readonly Regex DayOfWeekPattern = new(
        @"(?:(?<prefix>이번\s*주|다음\s*주)\s*)?(?<dow>월|화|수|목|금|토|일)요일",
        RegexOptions.Compiled);

    private static readonly Regex TimePattern = new(
        @"(?:(?<ampm>오전|오후|낮|밤|새벽)\s*)?(?<hour>\d{1,2})시(?:\s*(?<minute>\d{1,2})분)?",
        RegexOptions.Compiled);

    private static readonly Regex ColonTimePattern = new(
        @"(?:(?<ampm>오전|오후)\s*)?(?<hour>\d{1,2}):(?<minute>\d{2})",
        RegexOptions.Compiled);

    private static readonly Regex TimeRangePattern = new(
        @"(?<startHour>\d{1,2})(?:시|:(?<startMin>\d{2}))\s*(?:~|-|부터)\s*(?<endHour>\d{1,2})(?:시|:(?<endMin>\d{2}))(?:까지)?",
        RegexOptions.Compiled);

    private static readonly Regex WherePattern = new(
        @"(?<where>[가-힣A-Za-z0-9\s]+?)(?:에서|로|룸|호실|회의실)",
        RegexOptions.Compiled);

    private static readonly string[] ScheduleKeywords =
    [
        "회의", "미팅", "미팅룸", "세미나", "스터디", "면담", "면접", "행사", "발표",
        "워크숍", "오리엔테이션", "점검", "진료", "예약", "약속", "일정", "식사"
    ];

    private static readonly string[] CancellationKeywords = ["취소", "연기", "취소되었습니다", "미뤄"];
    private static readonly string[] ChangedKeywords = ["변경", "정정", "바뀌었습니다", "시간 변경"];

    /// <summary>
    /// 알림 원문들과 AI 생성 요약문을 종합하여 일정 관련 여부 및 캘린더 일정을 추출합니다.
    /// </summary>
    public static (bool IsScheduleRelated, CalendarSchedule? Schedule) Extract(
        string groupSummary,
        string primaryCategory,
        IReadOnlyList<string> notificationTexts,
        DateTime referenceTime,
        string? eventId = null)
    {
        var combinedText = string.Join(" ", notificationTexts) + " " + groupSummary;

        bool hasCategoryCue = primaryCategory is "일정/회의" or "일정" or "회의";
        bool hasKeywordCue = ScheduleKeywords.Any(k => combinedText.Contains(k, StringComparison.OrdinalIgnoreCase));
        var (start, end) = ParseTimeRange(combinedText, referenceTime);

        bool isScheduleRelated = hasCategoryCue || (hasKeywordCue && start.HasValue);
        if (!isScheduleRelated)
        {
            return (false, null);
        }

        var title = ExtractTitle(groupSummary, combinedText);
        var location = ExtractLocation(combinedText);

        DateTimeOffset? startOffset = start.HasValue
            ? new DateTimeOffset(start.Value, TimeSpan.FromHours(9)) // KST
            : null;

        DateTimeOffset? endOffset = end.HasValue
            ? new DateTimeOffset(end.Value, TimeSpan.FromHours(9))
            : startOffset?.AddHours(1);

        var details = groupSummary;
        if (CancellationKeywords.Any(k => combinedText.Contains(k)))
        {
            details = $"[취소/연기] {details}";
        }
        else if (ChangedKeywords.Any(k => combinedText.Contains(k)))
        {
            details = $"[일정 변경] {details}";
        }

        if (string.IsNullOrWhiteSpace(eventId))
        {
            var raw = $"{title}|{location}|{startOffset}|{endOffset}|{details}";
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            eventId = "evt-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }

        var schedule = new CalendarSchedule(
            EventId: eventId,
            Title: title,
            Start: startOffset,
            End: endOffset,
            TimeZone: "Asia/Seoul",
            Location: location,
            Details: details);

        return (true, schedule);
    }

    private static (DateTime? Start, DateTime? End) ParseTimeRange(string text, DateTime referenceTime)
    {
        DateTime? baseDate = null;

        // 1. 상대적 날짜 (오늘, 내일, 모레)
        var relMatch = RelativeDatePattern.Match(text);
        if (relMatch.Success)
        {
            baseDate = relMatch.Groups["rel"].Value switch
            {
                "오늘" => referenceTime.Date,
                "내일" => referenceTime.Date.AddDays(1),
                "모레" => referenceTime.Date.AddDays(2),
                "글피" => referenceTime.Date.AddDays(3),
                _ => null
            };
        }

        // 2. 명시적 날짜 (M월 d일)
        if (baseDate is null)
        {
            var dateMatch = DatePattern.Match(text);
            if (dateMatch.Success)
            {
                int year = dateMatch.Groups["year"].Success ? int.Parse(dateMatch.Groups["year"].Value, CultureInfo.InvariantCulture) : referenceTime.Year;
                int month = int.Parse(dateMatch.Groups["month"].Value, CultureInfo.InvariantCulture);
                int day = int.Parse(dateMatch.Groups["day"].Value, CultureInfo.InvariantCulture);
                try { baseDate = new DateTime(year, month, day); } catch { /* invalid date */ }
            }
        }

        // 3. 요일 (이번 주 금요일, 다음 주 화요일 등)
        if (baseDate is null)
        {
            var dowMatch = DayOfWeekPattern.Match(text);
            if (dowMatch.Success)
            {
                string prefix = dowMatch.Groups["prefix"].Value.Replace(" ", "");
                DayOfWeek targetDow = dowMatch.Groups["dow"].Value switch
                {
                    "월" => DayOfWeek.Monday,
                    "화" => DayOfWeek.Tuesday,
                    "수" => DayOfWeek.Wednesday,
                    "목" => DayOfWeek.Thursday,
                    "금" => DayOfWeek.Friday,
                    "토" => DayOfWeek.Saturday,
                    "일" => DayOfWeek.Sunday,
                    _ => referenceTime.DayOfWeek
                };

                int daysOffset = ((int)targetDow - (int)referenceTime.DayOfWeek + 7) % 7;
                if (prefix == "다음주") daysOffset += 7;
                else if (daysOffset == 0) daysOffset = 7;

                baseDate = referenceTime.Date.AddDays(daysOffset);
            }
        }

        baseDate ??= referenceTime.Date;

        // 4. 시간 범위 파싱 (14시~16시 등)
        var rangeMatch = TimeRangePattern.Match(text);
        if (rangeMatch.Success)
        {
            int startH = int.Parse(rangeMatch.Groups["startHour"].Value, CultureInfo.InvariantCulture);
            int startM = rangeMatch.Groups["startMin"].Success ? int.Parse(rangeMatch.Groups["startMin"].Value, CultureInfo.InvariantCulture) : 0;
            int endH = int.Parse(rangeMatch.Groups["endHour"].Value, CultureInfo.InvariantCulture);
            int endM = rangeMatch.Groups["endMin"].Success ? int.Parse(rangeMatch.Groups["endMin"].Value, CultureInfo.InvariantCulture) : 0;

            var startDt = baseDate.Value.AddHours(startH).AddMinutes(startM);
            var endDt = baseDate.Value.AddHours(endH).AddMinutes(endM);
            return (startDt, endDt);
        }

        // 5. 단일 시간 파싱 (오후 2시, 15:30 등)
        var timeMatch = TimePattern.Match(text);
        if (!timeMatch.Success)
            timeMatch = ColonTimePattern.Match(text);

        if (timeMatch.Success)
        {
            int h = int.Parse(timeMatch.Groups["hour"].Value, CultureInfo.InvariantCulture);
            int m = timeMatch.Groups["minute"].Success && !string.IsNullOrEmpty(timeMatch.Groups["minute"].Value)
                ? int.Parse(timeMatch.Groups["minute"].Value, CultureInfo.InvariantCulture)
                : 0;

            string ampm = timeMatch.Groups["ampm"].Value;
            if (ampm is "오후" or "밤" && h < 12) h += 12;
            else if (ampm is "오전" or "새벽" && h == 12) h = 0;

            var startDt = baseDate.Value.AddHours(h).AddMinutes(m);
            return (startDt, startDt.AddHours(1));
        }

        return (null, null);
    }

    private static string ExtractTitle(string summary, string combinedText)
    {
        foreach (var keyword in ScheduleKeywords)
        {
            var match = Regex.Match(combinedText, $@"(?:[가-힣A-Za-z0-9]+\s+)?{Regex.Escape(keyword)}");
            if (match.Success && match.Value.Length >= 2 && match.Value.Length <= 25)
            {
                return match.Value.Trim();
            }
        }

        return summary.Length > 20 ? summary[..20].Trim() + "..." : summary;
    }

    private static string? ExtractLocation(string text)
    {
        if (text.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || text.Contains("온라인", StringComparison.OrdinalIgnoreCase))
            return "온라인 (Zoom)";

        var roomMatch = Regex.Match(text, @"회의실\s*[A-Za-z0-9]+(?:호)?");
        if (roomMatch.Success) return roomMatch.Value;

        var locMatch = Regex.Match(text, @"(?:회의실|세미나실|스터디룸|미팅룸|강의실|사무실|[A-Za-z0-9가-힣]+룸|[A-Za-z0-9가-힣]+호실)\s*[A-Za-z0-9]*");
        if (locMatch.Success && locMatch.Value.Trim().Length >= 2) return locMatch.Value.Trim();

        var whereMatch = Regex.Match(text, @"(?<where>[가-힣A-Za-z0-9]+)\s*에서");
        if (whereMatch.Success)
        {
            var val = whereMatch.Groups["where"].Value.Trim();
            if (!val.EndsWith("시") && !val.EndsWith("분") && !val.EndsWith("일") && val != "오늘" && val != "내일" && val.Length >= 2)
                return val;
        }

        return null;
    }
}

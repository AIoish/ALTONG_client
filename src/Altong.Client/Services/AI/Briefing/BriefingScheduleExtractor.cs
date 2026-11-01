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
        @"(?:(?<startAmpm>오전|오후|낮|밤|새벽)\s*)?(?<startHour>\d{1,2})(?:시(?:\s*(?<startMin>\d{1,2})분)?|:(?<startMin>\d{2}))\s*(?:~|-|부터)\s*(?:(?<endAmpm>오전|오후|낮|밤|새벽)\s*)?(?<endHour>\d{1,2})(?:시(?:\s*(?<endMin>\d{1,2})분)?|:(?<endMin>\d{2}))(?:까지)?",
        RegexOptions.Compiled);

    private static readonly Regex LeadingModifierTokenPattern = new(
        @"^(?:오늘|내일|모레|글피|이번주|다음주|[월화수목금토일]요일|오전|오후|낮|밤|새벽|\d{4}년|\d{1,2}월|\d{1,2}일|\d{1,2}시(?:에|까지|부터)?|\d{1,2}분(?:에|까지|부터)?|\d{1,2}:\d{2}(?:에|까지|부터)?|\d{1,4}호(?:실)?(?:에서|으로|로|에)?|\d{1,2}층(?:에서|에)?|에서|으로|로|변경|없이)$",
        RegexOptions.Compiled);

    private static readonly string[] ScheduleKeywords =
    [
        "회의", "미팅", "미팅룸", "세미나", "스터디", "면담", "면접", "행사", "발표",
        "워크숍", "오리엔테이션", "점검", "진료", "예약", "약속", "일정", "식사",
        "과제", "제출", "마감", "기한", "시험", "고사", "등록", "납부", "수강", "신청",
        "티켓팅", "체크인", "출발", "도착"
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
        // AI 요약문을 최우선 탐색하고, 요약문에 없는 정보만 최신 알림부터 역순으로 탐색
        var prioritizedSources = new List<string>(notificationTexts.Count + 1);
        if (!string.IsNullOrWhiteSpace(groupSummary))
        {
            prioritizedSources.Add(groupSummary);
        }
        for (int i = notificationTexts.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(notificationTexts[i]))
            {
                prioritizedSources.Add(notificationTexts[i]);
            }
        }

        var combinedText = string.Join(" ", notificationTexts) + " " + groupSummary;

        bool hasCategoryCue = primaryCategory is "일정/회의" or "일정" or "회의";
        bool hasKeywordCue = ScheduleKeywords.Any(k => HasScheduleKeyword(combinedText, k));
        var (start, end) = ParseTimeRangeFromSources(prioritizedSources, referenceTime);

        bool hasExplicitDateTime = (DatePattern.IsMatch(combinedText) || RelativeDatePattern.IsMatch(combinedText) || DayOfWeekPattern.IsMatch(combinedText))
            && (TimePattern.IsMatch(combinedText) || ColonTimePattern.IsMatch(combinedText) || TimeRangePattern.IsMatch(combinedText));

        bool isNonSpamCategory = primaryCategory is not "광고/홍보" and not "기타";

        bool isScheduleRelated = hasCategoryCue || (hasKeywordCue && start.HasValue) || (hasExplicitDateTime && isNonSpamCategory && start.HasValue);
        if (!isScheduleRelated)
        {
            return (false, null);
        }

        var title = ExtractTitle(groupSummary, prioritizedSources);
        var location = ExtractLocationFromSources(prioritizedSources);

        DateTimeOffset? startOffset = start.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(start.Value, DateTimeKind.Unspecified), TimeSpan.FromHours(9)) // KST
            : null;

        DateTimeOffset? endOffset = end.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(end.Value, DateTimeKind.Unspecified), TimeSpan.FromHours(9))
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

    private static bool HasScheduleKeyword(string text, string keyword)
    {
        if (keyword == "회의")
        {
            return Regex.IsMatch(text, @"회의(?!실|룸|장|록)");
        }
        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static (DateTime? Start, DateTime? End) ParseTimeRangeFromSources(
        IReadOnlyList<string> prioritizedSources,
        DateTime referenceTime)
    {
        var refLocalDate = DateTime.SpecifyKind(referenceTime.Date, DateTimeKind.Unspecified);

        DateTime? baseDate = null;
        foreach (var source in prioritizedSources)
        {
            baseDate = TryParseDate(source, refLocalDate);
            if (baseDate.HasValue)
            {
                break;
            }
        }

        var resolvedDate = baseDate ?? refLocalDate;

        for (int i = 0; i < prioritizedSources.Count; i++)
        {
            var (start, end, isExplicitRange) = TryParseTimeOnDate(prioritizedSources[i], resolvedDate);
            if (start.HasValue)
            {
                if (!isExplicitRange)
                {
                    // 요약문에 시작 시각("오후 2시")만 축약되어 있고 원문 알림에 범위("오후 2시~4시")가 있는 경우 보완
                    for (int j = i + 1; j < prioritizedSources.Count; j++)
                    {
                        var (rangeStart, rangeEnd, hasRange) = TryParseTimeOnDate(prioritizedSources[j], resolvedDate);
                        if (hasRange && rangeStart == start)
                        {
                            return (rangeStart, rangeEnd);
                        }
                    }
                }
                return (start, end);
            }
        }

        return (null, null);
    }

    private static DateTime? TryParseDate(string text, DateTime refLocalDate)
    {
        // 1. 명시적 날짜 (M월 d일) - 한 문장에 이전 날짜와 변경 날짜가 함께 있으면 마지막 매칭을 우선
        var dateMatches = DatePattern.Matches(text);
        if (dateMatches.Count > 0)
        {
            for (int i = dateMatches.Count - 1; i >= 0; i--)
            {
                var dateMatch = dateMatches[i];
                int year = dateMatch.Groups["year"].Success
                    ? int.Parse(dateMatch.Groups["year"].Value, CultureInfo.InvariantCulture)
                    : refLocalDate.Year;
                int month = int.Parse(dateMatch.Groups["month"].Value, CultureInfo.InvariantCulture);
                int day = int.Parse(dateMatch.Groups["day"].Value, CultureInfo.InvariantCulture);
                try { return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified); }
                catch { /* invalid date */ }
            }
        }

        // 2. 상대적 날짜 (오늘, 내일, 모레, 글피)
        var relMatches = RelativeDatePattern.Matches(text);
        if (relMatches.Count > 0)
        {
            var relMatch = relMatches[^1];
            return relMatch.Groups["rel"].Value switch
            {
                "오늘" => refLocalDate,
                "내일" => refLocalDate.AddDays(1),
                "모레" => refLocalDate.AddDays(2),
                "글피" => refLocalDate.AddDays(3),
                _ => null
            };
        }

        // 3. 요일 (이번 주 금요일, 다음 주 화요일 등)
        var dowMatches = DayOfWeekPattern.Matches(text);
        if (dowMatches.Count > 0)
        {
            var dowMatch = dowMatches[^1];
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
                _ => refLocalDate.DayOfWeek
            };

            int daysOffset = ((int)targetDow - (int)refLocalDate.DayOfWeek + 7) % 7;
            if (prefix == "다음주") daysOffset += 7;
            else if (daysOffset == 0) daysOffset = 7;

            return refLocalDate.AddDays(daysOffset);
        }

        return null;
    }

    private static (DateTime? Start, DateTime? End, bool IsExplicitRange) TryParseTimeOnDate(string text, DateTime baseDate)
    {
        // 4. 시간 범위 파싱 (오후 2시~4시, 오전 10:00~11:30, 14시~16시 등)
        var rangeMatches = TimeRangePattern.Matches(text);
        if (rangeMatches.Count > 0)
        {
            var rangeMatch = rangeMatches[^1];
            int startH = int.Parse(rangeMatch.Groups["startHour"].Value, CultureInfo.InvariantCulture);
            int startM = rangeMatch.Groups["startMin"].Success && !string.IsNullOrEmpty(rangeMatch.Groups["startMin"].Value)
                ? int.Parse(rangeMatch.Groups["startMin"].Value, CultureInfo.InvariantCulture)
                : 0;
            int endH = int.Parse(rangeMatch.Groups["endHour"].Value, CultureInfo.InvariantCulture);
            int endM = rangeMatch.Groups["endMin"].Success && !string.IsNullOrEmpty(rangeMatch.Groups["endMin"].Value)
                ? int.Parse(rangeMatch.Groups["endMin"].Value, CultureInfo.InvariantCulture)
                : 0;

            string startAmpm = rangeMatch.Groups["startAmpm"].Value;
            string endAmpm = rangeMatch.Groups["endAmpm"].Value;

            startH = AdjustHourForAmPm(startH, startAmpm);
            if (!string.IsNullOrEmpty(endAmpm))
            {
                endH = AdjustHourForAmPm(endH, endAmpm);
            }
            else if (startAmpm is "오후" or "밤" && endH < 12)
            {
                endH += 12;
            }
            else if (endH < startH && endH < 12)
            {
                endH += 12;
            }

            var startDt = DateTime.SpecifyKind(baseDate.AddHours(startH).AddMinutes(startM), DateTimeKind.Unspecified);
            var endDt = DateTime.SpecifyKind(baseDate.AddHours(endH).AddMinutes(endM), DateTimeKind.Unspecified);
            if (endDt <= startDt)
            {
                endDt = startDt.AddHours(1);
            }
            return (startDt, endDt, true);
        }

        // 5. 단일 시간 파싱 (오후 2시, 15:30 등) - "오후 2시에서 오후 4시로 변경"처럼 여러 시각이 있으면 마지막 매칭 우선
        var timeMatches = TimePattern.Matches(text);
        var colonMatches = ColonTimePattern.Matches(text);
        Match? selectedMatch = null;
        string fallbackAmpm = string.Empty;

        if (timeMatches.Count > 0)
        {
            selectedMatch = timeMatches[^1];
            for (int i = 0; i < timeMatches.Count - 1; i++)
            {
                if (!string.IsNullOrEmpty(timeMatches[i].Groups["ampm"].Value))
                    fallbackAmpm = timeMatches[i].Groups["ampm"].Value;
            }
        }

        if (colonMatches.Count > 0)
        {
            var lastColon = colonMatches[^1];
            if (selectedMatch is null || lastColon.Index > selectedMatch.Index)
            {
                selectedMatch = lastColon;
            }
        }

        if (selectedMatch is not null && selectedMatch.Success)
        {
            int h = int.Parse(selectedMatch.Groups["hour"].Value, CultureInfo.InvariantCulture);
            int m = selectedMatch.Groups["minute"].Success && !string.IsNullOrEmpty(selectedMatch.Groups["minute"].Value)
                ? int.Parse(selectedMatch.Groups["minute"].Value, CultureInfo.InvariantCulture)
                : 0;

            string ampm = selectedMatch.Groups["ampm"].Value;
            if (string.IsNullOrEmpty(ampm) && !string.IsNullOrEmpty(fallbackAmpm))
            {
                ampm = fallbackAmpm;
            }
            h = AdjustHourForAmPm(h, ampm);

            var startDt = DateTime.SpecifyKind(baseDate.AddHours(h).AddMinutes(m), DateTimeKind.Unspecified);
            return (startDt, startDt.AddHours(1), false);
        }

        return (null, null, false);
    }

    private static int AdjustHourForAmPm(int hour, string ampm)
    {
        if (ampm is "오후" or "밤" && hour < 12) return hour + 12;
        if (ampm is "오전" or "새벽" && hour == 12) return 0;
        return hour;
    }

    private static string ExtractTitle(string summary, IReadOnlyList<string> prioritizedSources)
    {
        foreach (var source in prioritizedSources)
        {
            foreach (var keyword in ScheduleKeywords)
            {
                // "회의실", "미팅룸", "세미나실" 등 장소 복합어는 제외하되,
                // "발표가", "과제를", "스터디를"처럼 조사가 붙은 자연어 표현은 정상 매칭
                string kwPattern = $@"{Regex.Escape(keyword)}(?!실|룸|장|관|동|록)";

                var matches = Regex.Matches(source, $@"(?:[가-힣A-Za-z0-9]+\s+){{0,2}}{kwPattern}");
                foreach (Match match in matches)
                {
                    var cleaned = CleanTitleCandidate(match.Value);
                    if (cleaned.Length >= 2 && cleaned.Length <= 25)
                    {
                        return cleaned;
                    }
                }
            }
        }

        return summary.Length > 20 ? summary[..20].Trim() + "..." : summary;
    }

    private static string CleanTitleCandidate(string candidate)
    {
        var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // "회의실 A에서 회의", "18시까지 과제"처럼 앞쪽 토큰이 부사격 조사(에서/까지/부터)로 끝나면 그 앞 절 전체 제거
        for (int i = tokens.Count - 2; i >= 0; i--)
        {
            if (tokens[i].EndsWith("에서", StringComparison.Ordinal) ||
                tokens[i].EndsWith("까지", StringComparison.Ordinal) ||
                tokens[i].EndsWith("부터", StringComparison.Ordinal))
            {
                tokens.RemoveRange(0, i + 1);
                break;
            }
        }

        while (tokens.Count > 1 && IsLeadingModifierToken(tokens[0]))
        {
            tokens.RemoveAt(0);
        }
        return string.Join(" ", tokens).Trim();
    }

    private static bool IsLeadingModifierToken(string token)
    {
        if (LeadingModifierTokenPattern.IsMatch(token)) return true;
        if (token.EndsWith("에서", StringComparison.Ordinal) ||
            token.EndsWith("으로", StringComparison.Ordinal) ||
            token.EndsWith("까지", StringComparison.Ordinal) ||
            token.EndsWith("부터", StringComparison.Ordinal) ||
            token.EndsWith("호실", StringComparison.Ordinal) ||
            token.EndsWith("회의실", StringComparison.Ordinal) ||
            token.EndsWith("강의동", StringComparison.Ordinal) ||
            token.EndsWith("공학관", StringComparison.Ordinal) ||
            token.EndsWith("회관", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static string? ExtractLocationFromSources(IReadOnlyList<string> prioritizedSources)
    {
        foreach (var source in prioritizedSources)
        {
            var loc = ExtractLocation(source);
            if (!string.IsNullOrWhiteSpace(loc))
            {
                return loc;
            }
        }
        return null;
    }

    private static string? ExtractLocation(string text)
    {
        if (text.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || text.Contains("온라인", StringComparison.OrdinalIgnoreCase))
            return "온라인 (Zoom)";

        var roomMatches = Regex.Matches(text, @"회의실\s*[A-Za-z0-9]+(?:호)?");
        if (roomMatches.Count > 0) return roomMatches[^1].Value.Trim();

        // 건물/동/관/층 접두어가 있는 호실("종합강의동 302호", "본관 3층 302호") 또는 단독 호실("302호", "302호실") 추출
        var buildingRoomMatches = Regex.Matches(
            text,
            @"(?:[가-힣A-Za-z0-9]*(?:동|관|층|센터|타워|빌딩|홀|캠퍼스|본부|연구소|랩|Lab)\s+)*\d{2,4}호(?:실)?");
        if (buildingRoomMatches.Count > 0)
        {
            var val = buildingRoomMatches[^1].Value.Trim();
            if (val.Length >= 2) return val;
        }

        var locMatches = Regex.Matches(text, @"(?:회의실|세미나실|스터디룸|미팅룸|강의실|사무실|[A-Za-z0-9가-힣]+룸)\s*[A-Za-z0-9]*");
        if (locMatches.Count > 0 && locMatches[^1].Value.Trim().Length >= 2) return locMatches[^1].Value.Trim();

        var whereMatches = Regex.Matches(text, @"(?<where>[가-힣A-Za-z0-9]+(?:\s+\d+호(?:실)?)?)\s*에서");
        for (int i = whereMatches.Count - 1; i >= 0; i--)
        {
            var val = whereMatches[i].Groups["where"].Value.Trim();
            if (!val.EndsWith("시") && !val.EndsWith("분") && !val.EndsWith("일") && val != "오늘" && val != "내일" && val.Length >= 2)
                return val;
        }

        return null;
    }
}

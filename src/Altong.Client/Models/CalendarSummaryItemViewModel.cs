using System.ComponentModel;
using System.Globalization;
using Altong.Client.Services;

namespace Altong.Client.Models;

public sealed class CalendarSummaryItemViewModel : INotifyPropertyChanged
{
    private bool _busy;
    private bool _editing;
    private bool _saved;
    private string _status = "";
    private string _title = "";
    private string _timeZone = DefaultTimeZone;
    private string _location = "";
    private string _details = "";
    public NotificationSummaryItem Summary { get; }
    public string? SessionId { get; }
    public string SourceText { get; }
    public CalendarSchedule Draft { get; private set; }
    public bool IsScheduleRelated => Summary.IsScheduleRelated;
    public string Title { get => _title; set { string v = value ?? ""; if (_title == v) return; _title = v; Changed(nameof(Title)); } }
    public const string DefaultTimeZone = "Asia/Seoul";
    /// <summary>시각 선택 목록. 00:00부터 23:30까지 30분 간격이다.</summary>
    public static IReadOnlyList<string> HalfHourSlots { get; } =
        Enumerable.Range(0, 48).Select(slot => $"{slot / 2:00}:{slot % 2 * 30:00}").ToArray();
    private string _startTime = "";
    private string _endTime = "";
    private DateTime? _startDate;
    private DateTime? _endDate;
    public DateTime? StartDate
    {
        get => _startDate;
        set
        {
            if (_startDate == value) return;
            _startDate = value;
            Changed(nameof(StartDate));
            // 종료 날짜가 없거나 시작보다 앞서면 시작 날짜를 따라간다. 시작만 골라도 저장할 수 있다.
            if (value is { } start && (EndDate is null || EndDate < start)) EndDate = start;
            FillEndTime();
        }
    }
    public DateTime? EndDate
    {
        get => _endDate;
        set { if (_endDate == value) return; _endDate = value; Changed(nameof(EndDate)); }
    }
    public string StartTime
    {
        get => _startTime;
        set
        {
            if (_startTime == value) return;
            bool listChanges = !IsSlot(_startTime) || !IsSlot(value);
            _startTime = value ?? "";
            Changed(nameof(StartTime));
            if (listChanges) Changed(nameof(StartTimeOptions));
            FillEndTime();
        }
    }

    /// <summary>같은 날 일정에서 종료가 비었거나 시작보다 이르면 종료를 시작 1시간 뒤로 맞춘다. 23시 이후는 사용자가 고른다.</summary>
    private void FillEndTime()
    {
        if (StartDate is { } start && (EndDate is null || EndDate == start) &&
            TryParseTime(_startTime, out var from) && (!TryParseTime(EndTime, out var to) || to <= from) &&
            from < TimeSpan.FromHours(23))
            EndTime = from.Add(TimeSpan.FromHours(1)).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
    public string EndTime
    {
        get => _endTime;
        set
        {
            if (_endTime == value) return;
            // 30분 단위가 아닌 시각이 들어오거나 빠지면 선택 목록도 바꿔야 빈칸으로 보이지 않는다.
            bool listChanges = !IsSlot(_endTime) || !IsSlot(value);
            _endTime = value ?? "";
            Changed(nameof(EndTime));
            if (listChanges) Changed(nameof(EndTimeOptions));
        }
    }
    public IReadOnlyList<string> StartTimeOptions => WithSlot(StartTime);
    public IReadOnlyList<string> EndTimeOptions => WithSlot(EndTime);
    public string TimeZone { get => _timeZone; set { string v = value ?? ""; if (_timeZone == v) return; _timeZone = v; Changed(nameof(TimeZone)); Changed(nameof(TimeZoneHeader)); } }
    public string TimeZoneHeader => TimeZone == DefaultTimeZone ? "시간대 · 한국 시간" : $"시간대 · {TimeZone}";
    public string Location { get => _location; set { string v = value ?? ""; if (_location == v) return; _location = v; Changed(nameof(Location)); } }
    public string Details { get => _details; set { string v = value ?? ""; if (_details == v) return; _details = v; Changed(nameof(Details)); } }
    public bool IsBusy { get => _busy; set { _busy = value; Changed(nameof(IsBusy)); Changed(nameof(CanAct)); Changed(nameof(CanEdit)); Changed(nameof(ButtonText)); } }
    public bool IsSaved { get => _saved; set { _saved = value; Changed(nameof(IsSaved)); Changed(nameof(CanAct)); Changed(nameof(ButtonText)); } }
    public bool CanAct => !IsBusy && !IsSaved;
    public bool CanEdit => !IsBusy;
    public bool IsEditing { get => _editing; set { _editing = value; Changed(nameof(IsEditing)); } }
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); Changed(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);
    public string ButtonText => IsBusy ? "저장 중…" : IsSaved ? "✓ 등록됨" : "일정 등록";
    public string CardLabel => IsScheduleRelated ? "일정" : "AI 요약";
    public string AppDisplayName => AppNameDisplayConverter.Format(Summary.AppName);
    public string? SourceMetadata => SourceText == Summary.AppName?.Trim() ? null : SourceText;
    public string ScheduleTitle => string.IsNullOrWhiteSpace(Draft.Title) ? "일정 제목 확인 필요" : Draft.Title;
    public string ScheduleWhen => Draft.Start is { } start && Draft.End is { } end
        ? (start.Date == end.Date ? $"{start:yyyy년 M월 d일} · {start:HH:mm}–{end:HH:mm}"
            : $"{start:yyyy년 M월 d일 HH:mm} → {end:M월 d일 HH:mm}")
        : "날짜·시각을 확인해 주세요";
    public string ScheduleCompactMeta
    {
        get
        {
            string when = Draft.Start is { } start && Draft.End is { } end
                ? (start.Date == end.Date
                    ? $"{start:M월 d일} {start:HH:mm}–{end:HH:mm}"
                    : $"{start:M월 d일 HH:mm} → {end:M월 d일 HH:mm}")
                : "날짜·시각 확인 필요";
            return string.IsNullOrWhiteSpace(Draft.Location) ? when : $"{when} · {Draft.Location.Trim()}";
        }
    }
    public string ScheduleZone => Draft.TimeZone == "Asia/Seoul" ? "한국 시간" : Draft.TimeZone ?? "시간대 확인 필요";
    public string ScheduleLocation => string.IsNullOrWhiteSpace(Draft.Location) ? "지정된 장소 없음" : Draft.Location;
    public string ScheduleDetails => string.IsNullOrWhiteSpace(Draft.Details) ? "추가 세부사항 없음" : Draft.Details;
    public string ScheduleText => Draft.Start is { } start && Draft.End is { } end
        ? $"{Draft.Title} · {start:M월 d일 HH:mm} → {end:M월 d일 HH:mm} · {Draft.TimeZone}"
        : $"{Draft.Title ?? "일정"} · 날짜·시간 확인 필요";

    public CalendarSummaryItemViewModel(NotificationSummaryItem summary, string? sessionId = null,
        DateTime? receivedAt = null, CalendarSchedule? correction = null)
    {
        Summary = summary;
        SessionId = sessionId;
        SourceText = receivedAt is { } at ? $"{at.ToLocalTime():M월 d일 HH:mm} 집중 종료 후 요약" : summary.AppName?.Trim() ?? "";
        Draft = InScheduleTimeZone(correction ?? summary.Schedule ?? new("", null, null, null, null, null, null));
        LoadEditableFieldsFromDraft(notify: false);
    }

    private void LoadEditableFieldsFromDraft(bool notify)
    {
        _title = Draft.Title ?? "";
        _startDate = Draft.Start?.Date;
        _endDate = Draft.End?.Date;
        _startTime = Draft.Start?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _endTime = Draft.End?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _timeZone = string.IsNullOrWhiteSpace(Draft.TimeZone) ? DefaultTimeZone : Draft.TimeZone;
        _location = Draft.Location ?? "";
        _details = Draft.Details ?? "";
        if (!notify) return;
        Changed(nameof(Title));
        Changed(nameof(StartDate));
        Changed(nameof(EndDate));
        Changed(nameof(StartTime));
        Changed(nameof(StartTimeOptions));
        Changed(nameof(EndTime));
        Changed(nameof(EndTimeOptions));
        Changed(nameof(TimeZone));
        Changed(nameof(TimeZoneHeader));
        Changed(nameof(Location));
        Changed(nameof(Details));
    }

    public void ResetEdits()
    {
        LoadEditableFieldsFromDraft(notify: true);
        IsEditing = false;
        if (!IsSaved && !string.IsNullOrWhiteSpace(Status))
            Status = "";
    }

    private static CalendarSchedule InScheduleTimeZone(CalendarSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.TimeZone)) return schedule;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
            return schedule with
            {
                Start = schedule.Start is { } start ? TimeZoneInfo.ConvertTime(start, zone) : null,
                End = schedule.End is { } end ? TimeZoneInfo.ConvertTime(end, zone) : null
            };
        }
        catch (TimeZoneNotFoundException) { return schedule; }
        catch (InvalidTimeZoneException) { return schedule; }
    }

    public bool ApplyEdits()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TimeZone)) TimeZone = DefaultTimeZone;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone.Trim());
            var updated = Draft with
            {
                Title = Title.Trim(), Start = ReadTime(StartDate, StartTime, zone, Draft.Start),
                End = ReadTime(EndDate, EndTime, zone, Draft.End), TimeZone = TimeZone.Trim(),
                Location = Location.Trim(), Details = Details.Trim()
            };
            if (updated.Validate() is { } error) { Status = error; return false; }
            Draft = updated;
            // Corrections are stored separately from the immutable report snapshot.
            IsSaved = false;
            IsEditing = false;
            Status = "수정 완료 · 캘린더에 저장하세요.";
            Changed(nameof(ScheduleText));
            Changed(nameof(ScheduleTitle));
            Changed(nameof(ScheduleWhen));
            Changed(nameof(ScheduleCompactMeta));
            Changed(nameof(ScheduleZone));
            Changed(nameof(ScheduleLocation));
            Changed(nameof(ScheduleDetails));
            return true;
        }
        catch (ArgumentException ex) { Status = ex.Message; }
        catch (TimeZoneNotFoundException) { Status = "알 수 없는 시간대입니다. 예: Asia/Seoul"; }
        catch (InvalidTimeZoneException) { Status = "사용할 수 없는 시간대입니다."; }
        return false;
    }

    private static DateTimeOffset ReadTime(DateTime? date, string time, TimeZoneInfo zone, DateTimeOffset? original)
    {
        if (date is null || !DateTime.TryParseExact(time, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)) throw new ArgumentException("시작·종료 날짜와 시각을 선택해 주세요.");
        var wall = DateTime.SpecifyKind(date.Value.Date.Add(parsed.TimeOfDay), DateTimeKind.Unspecified);
        // The minute-only editor must preserve seconds and the known DST offset when left unchanged.
        if (original is { } unchanged && unchanged.Date == wall.Date && unchanged.Hour == wall.Hour &&
            unchanged.Minute == wall.Minute && zone.GetUtcOffset(unchanged.UtcDateTime) == unchanged.Offset)
            return unchanged;
        if (zone.IsInvalidTime(wall)) throw new ArgumentException("시간대 변경으로 존재하지 않는 시각입니다. 다른 시각을 입력해 주세요.");
        if (zone.IsAmbiguousTime(wall))
        {
            if (original is { } known && known.DateTime == wall && zone.GetAmbiguousTimeOffsets(wall).Contains(known.Offset)) return known;
            throw new ArgumentException("이 시각은 시간대 변경으로 두 번 발생합니다. UTC 오프셋이 확정된 일정 정보가 필요합니다.");
        }
        return new(wall, zone.GetUtcOffset(wall));
    }

    private static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (!DateTime.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        time = parsed.TimeOfDay;
        return true;
    }

    private static bool IsSlot(string? time) => string.IsNullOrWhiteSpace(time) || HalfHourSlots.Contains(time);

    // AI가 뽑은 시각이 30분 단위가 아니어도 목록에서 그대로 고를 수 있게 끼워 넣는다.
    private static IReadOnlyList<string> WithSlot(string time) =>
        string.IsNullOrWhiteSpace(time) || HalfHourSlots.Contains(time) ? HalfHourSlots
            : HalfHourSlots.Append(time).Order(StringComparer.Ordinal).ToArray();

    public NotificationSummaryItem Capture() => Summary with { Schedule = Summary.IsScheduleRelated ? Draft : Summary.Schedule };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

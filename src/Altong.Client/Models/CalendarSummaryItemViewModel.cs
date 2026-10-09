using System.ComponentModel;
using System.Globalization;

namespace Altong.Client.Models;

public sealed class CalendarSummaryItemViewModel : INotifyPropertyChanged
{
    private bool _busy;
    private bool _editing;
    private bool _saved;
    private string _status = "";
    public NotificationSummaryItem Summary { get; }
    public string? SessionId { get; }
    public string SourceText { get; }
    public CalendarSchedule Draft { get; private set; }
    public bool IsScheduleRelated => Summary.IsScheduleRelated;
    public string Title { get; set; }
    public DateTime? StartDate { get; set; }
    public string StartTime { get; set; }
    public DateTime? EndDate { get; set; }
    public string EndTime { get; set; }
    public string TimeZone { get; set; }
    public string Location { get; set; }
    public string Details { get; set; }
    public bool IsBusy { get => _busy; set { _busy = value; Changed(nameof(IsBusy)); Changed(nameof(CanAct)); Changed(nameof(CanEdit)); Changed(nameof(ButtonText)); } }
    public bool IsSaved { get => _saved; set { _saved = value; Changed(nameof(IsSaved)); Changed(nameof(CanAct)); Changed(nameof(ButtonText)); } }
    public bool CanAct => !IsBusy && !IsSaved;
    public bool CanEdit => !IsBusy;
    public bool IsEditing { get => _editing; set { _editing = value; Changed(nameof(IsEditing)); } }
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public string ButtonText => IsBusy ? "저장 중…" : IsSaved ? "캘린더에 저장됨" : "캘린더에 저장";
    public string CardLabel => IsScheduleRelated ? "일정이 포함된 알림" : "알림 요약";
    public string ScheduleTitle => string.IsNullOrWhiteSpace(Draft.Title) ? "일정 제목 확인 필요" : Draft.Title;
    public string ScheduleWhen => Draft.Start is { } start && Draft.End is { } end
        ? (start.Date == end.Date ? $"{start:yyyy년 M월 d일} · {start:HH:mm}–{end:HH:mm}"
            : $"{start:yyyy년 M월 d일 HH:mm} → {end:M월 d일 HH:mm}")
        : "날짜·시각을 확인해 주세요";
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
        SourceText = receivedAt is { } at ? $"{at.ToLocalTime():M월 d일 HH:mm} 집중 종료 후 요약" : "";
        Draft = InScheduleTimeZone(correction ?? summary.Schedule ?? new("", null, null, null, null, null, null));
        Title = Draft.Title ?? "";
        StartDate = Draft.Start?.Date;
        EndDate = Draft.End?.Date;
        StartTime = Draft.Start?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        EndTime = Draft.End?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        TimeZone = Draft.TimeZone ?? "";
        Location = Draft.Location ?? "";
        Details = Draft.Details ?? "";
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
            if (string.IsNullOrWhiteSpace(TimeZone)) throw new ArgumentException("시간대를 입력해 주세요. 예: Asia/Seoul");
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
            Status = "일정 정보를 확인했습니다. 캘린더에 저장 버튼을 눌러 주세요.";
            Changed(nameof(ScheduleText));
            Changed(nameof(ScheduleTitle));
            Changed(nameof(ScheduleWhen));
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
            DateTimeStyles.None, out var parsed)) throw new ArgumentException("시작·종료 날짜와 시각(HH:mm)을 입력해 주세요.");
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

    public NotificationSummaryItem Capture() => Summary with { Schedule = Summary.IsScheduleRelated ? Draft : Summary.Schedule };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

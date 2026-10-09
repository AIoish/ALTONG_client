using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Altong.Client.Models;

namespace Altong.Client.Services.Calendar;

// Independent snapshots: removing an AI summary or report must not remove a saved appointment.
public sealed record DashboardCalendarEntry(string SessionId, string SummaryId, string? AppName, CalendarSchedule Schedule)
{
    [JsonIgnore] public DateTime LocalDate => Schedule.Start!.Value.ToLocalTime().Date;
    [JsonIgnore] public string Title => Schedule.Title!;
    [JsonIgnore] public string WhenText
    {
        get
        {
            var start = Schedule.Start!.Value.ToLocalTime();
            var end = Schedule.End!.Value.ToLocalTime();
            return start.Date == end.Date ? $"{start:HH:mm}–{end:HH:mm}" : $"{start:M월 d일 HH:mm} → {end:M월 d일 HH:mm}";
        }
    }
    [JsonIgnore] public string LocationText => string.IsNullOrWhiteSpace(Schedule.Location) ? "지정된 장소 없음" : Schedule.Location;
    [JsonIgnore] public string DetailsText => string.IsNullOrWhiteSpace(Schedule.Details) ? "추가 세부사항 없음" : Schedule.Details;
    [JsonIgnore] public string SourceText => string.IsNullOrWhiteSpace(AppName) ? "알림에서 저장한 일정" : $"{AppName} · 알림에서 저장";
}

public sealed class DashboardCalendarStore
{
    private readonly string _path;
    private Dictionary<(string Session, string Summary), DashboardCalendarEntry> _entries = [];
    public string? LoadWarning { get; private set; }
    public event EventHandler? Changed;

    public DashboardCalendarStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "dashboard-calendar-schedules.json");
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<DashboardCalendarEntry[]>(File.ReadAllText(_path))
                ?? throw new JsonException();
            foreach (var entry in loaded)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.SessionId) ||
                    string.IsNullOrWhiteSpace(entry.SummaryId) || entry.Schedule is null || entry.Schedule.Validate() is not null)
                    throw new JsonException();
            }
            foreach (var entry in loaded) _entries[(entry.SessionId, entry.SummaryId)] = entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "저장된 일정을 불러오지 못했습니다. 일정 파일을 확인한 뒤 앱을 다시 실행해 주세요.";
        }
    }

    public DashboardCalendarEntry? Get(string sessionId, string summaryId) => _entries.GetValueOrDefault((sessionId, summaryId));
    public IReadOnlyList<DashboardCalendarEntry> GetDay(DateTime date) => _entries.Values
        .Where(entry => entry.LocalDate == date.Date).OrderBy(entry => entry.Schedule.Start)
        .ThenBy(entry => entry.Title).ToArray();
    public IReadOnlyDictionary<DateTime, int> GetMonthCounts(DateTime month) => _entries.Values
        .Where(entry => entry.LocalDate.Year == month.Year && entry.LocalDate.Month == month.Month)
        .GroupBy(entry => entry.LocalDate).ToDictionary(group => group.Key, group => group.Count());

    public void Save(DashboardCalendarEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SessionId) || string.IsNullOrWhiteSpace(entry.SummaryId))
            throw new ArgumentException("일정을 식별할 정보가 없습니다.");
        if (entry.Schedule.Validate() is { } error) throw new ArgumentException(error);
        if (Get(entry.SessionId, entry.SummaryId) == entry) return;
        var updated = new Dictionary<(string, string), DashboardCalendarEntry>(_entries)
        {
            [(entry.SessionId, entry.SummaryId)] = entry
        };
        Write(updated);
    }

    public void Delete(string sessionId, string summaryId)
    {
        var updated = new Dictionary<(string, string), DashboardCalendarEntry>(_entries);
        if (updated.Remove((sessionId, summaryId))) Write(updated);
    }

    private void Write(Dictionary<(string Session, string Summary), DashboardCalendarEntry> updated)
    {
        // Preserve an unreadable file and publish the change only after the atomic replacement succeeds.
        if (LoadWarning is not null) throw new IOException(LoadWarning);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated.Values.ToArray()));
        File.Move(temporary, _path, overwrite: true);
        _entries = updated;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

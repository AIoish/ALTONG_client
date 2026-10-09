using System.ComponentModel;

namespace Altong.Client.Models;

// Presentation grouping uses the stored app identifier, never sender/title guesses.
public sealed class NotificationAppGroup(string appName) : INotifyPropertyChanged
{
    public string AppName { get; } = appName;
    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? "알 수 없는 앱" : AppName;
    public NotificationHistoryItem[] Rows { get; private set; } = [];
    private bool _expanded;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Changed(nameof(IsExpanded));
            Changed(nameof(ExpandText));
        }
    }
    public string ExpandText => IsExpanded ? "알림 접기 ∧" : "알림 펼치기 ∨";
    private bool _summaryOpen;
    public bool IsSummaryOpen
    {
        get => _summaryOpen;
        set
        {
            if (_summaryOpen == value) return;
            _summaryOpen = value;
            Changed(nameof(IsSummaryOpen));
            Changed(nameof(SummaryButtonText));
        }
    }
    public string SummaryButtonText => IsSummaryOpen ? "요약 닫기" : "AI 요약";
    public string LatestTitle => Rows.FirstOrDefault()?.Title ?? "받은 요약과 일정을 확인하세요.";
    public int SummaryCount { get; private set; }
    public string CountText => (Rows.Length == 0 ? $"요약 {SummaryCount}개" : $"알림 {Rows.Length}개")
        + (Rows.Length > 0 && SummaryCount > 0 ? $" · 요약 {SummaryCount}개" : "") + (Rows.Any(row => row.IsSelected)
        ? $" · 선택 {Rows.Count(row => row.IsSelected)}개" : "");
    public string LatestTimeText => Rows.FirstOrDefault() is { } row
        ? "최근 알림 · " + (row.Record.ReceivedAt.ToLocalTime().Date == DateTime.Today
            ? row.Record.ReceivedAt.ToLocalTime().ToString("오늘 HH:mm")
            : row.Record.ReceivedAt.ToLocalTime().ToString("M월 d일 HH:mm")) : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool HasNotifications => Rows.Length > 0;

    public void SetSummaryCount(int count)
    {
        if (SummaryCount == count) return;
        SummaryCount = count;
        Changed(nameof(CountText));
    }

    public void SetRows(NotificationHistoryItem[] rows)
    {
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows = rows;
        foreach (var row in Rows) row.PropertyChanged += RowChanged;
        Changed(nameof(Rows)); Changed(nameof(CountText)); Changed(nameof(LatestTitle)); Changed(nameof(LatestTimeText)); Changed(nameof(HasNotifications));
    }

    public static NotificationAppGroup[] Group(NotificationHistoryItem[] rows, IEnumerable<NotificationAppGroup> previous)
    {
        var existing = previous.ToDictionary(group => group.AppName, StringComparer.OrdinalIgnoreCase);
        var groups = rows.GroupBy(row => row.AppName.Trim(), StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            if (!existing.Remove(group.Key, out var item)) item = new(group.Key);
            item.SetRows(group.ToArray());
            return item;
        }).OrderByDescending(group => AsUtc(group.Rows[0].Record.ReceivedAt)).ToArray();
        foreach (var removed in existing.Values) removed.SetRows([]);
        return groups;
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationHistoryItem.IsSelected)) Changed(nameof(CountText));
    }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    private static DateTime AsUtc(DateTime at) => at.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime();
}

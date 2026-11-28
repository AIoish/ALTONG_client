namespace Altong.Client.Models;

/// <summary>여러 모델 요약을 한 그룹으로 표시하면서 각 일정의 편집·저장 상태는 독립적으로 유지한다.</summary>
public sealed class ReportSummaryGroupViewModel
{
    public string AppDisplayName { get; }
    public string Text { get; }
    public CalendarSummaryItemViewModel[] Schedules { get; }

    public ReportSummaryGroupViewModel(IEnumerable<CalendarSummaryItemViewModel> items)
    {
        var rows = items.ToArray();
        AppDisplayName = rows[0].AppDisplayName;
        Text = string.Join("\n\n", rows.Select(row => row.Summary.Text.Trim()).Distinct(StringComparer.Ordinal));
        Schedules = rows.Where(row => row.IsScheduleRelated).ToArray();
    }
}

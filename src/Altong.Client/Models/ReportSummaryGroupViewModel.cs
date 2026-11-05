namespace Altong.Client.Models;

/// <summary>여러 모델 요약을 한 그룹으로 표시하면서 각 일정의 편집·저장 상태는 독립적으로 유지한다.</summary>
public sealed class ReportSummaryGroupViewModel
{
    public string? AppName { get; }
    public string AppDisplayName { get; }
    public string? SourceLabel { get; }
    public string? SourceMetadata { get; }
    public string Text { get; }
    public CalendarSummaryItemViewModel[] Schedules { get; }
    public bool IsScheduleRelated => Schedules.Length > 0;
    public string CardLabel => IsScheduleRelated ? "일정 포함" : "AI 요약";

    public ReportSummaryGroupViewModel(IEnumerable<CalendarSummaryItemViewModel> items)
    {
        var rows = items.ToArray();
        AppName = rows[0].Summary.AppName;
        AppDisplayName = rows[0].AppDisplayName;
        SourceMetadata = rows[0].SourceMetadata;
        var sources = rows.Select(row => row.Summary.SourceLabel).Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal).ToArray();
        SourceLabel = sources.Length == 0 ? null : string.Join(" / ", sources);
        Text = string.Join("\n\n", rows.Select(row => row.Summary.Text.Trim()).Distinct(StringComparer.Ordinal));
        Schedules = rows.Where(row => row.IsScheduleRelated).ToArray();
    }
}

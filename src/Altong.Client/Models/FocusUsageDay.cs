namespace Altong.Client.Models;

public sealed record FocusUsageHour(int Hour, TimeSpan Duration, double ScaleMinutes, bool IsCurrentHour)
{
    public double BarHeight => Duration.TotalMinutes / ScaleMinutes * 88;
    public string AxisLabel => Hour % 6 == 0 || Hour == 23 ? $"{Hour:00}" : "";
    /// <summary>"오늘의 흐름" 띠의 시각 표시. 3시간마다 하나.</summary>
    public string AxisText => Hour % 3 == 0 ? $"{Hour}시" : "";
    /// <summary>그 시간에 집중한 정도(0~1). 띠의 호박색 칸이 이 비율만큼 진해진다. 기록이 없으면 비운다.</summary>
    public double Intensity => Duration <= TimeSpan.Zero ? 0 : Math.Clamp(0.35 + 0.65 * Duration.TotalMinutes / 60, 0.35, 1);
    public string TooltipText => $"{Hour:00}시–{Hour + 1:00}시 · {FormatDuration(Duration)}";

    internal static string FormatDuration(TimeSpan value) => value.Ticks == 0 ? "0분"
        : value.TotalMinutes < 1 ? "1분 미만"
        : value.TotalHours < 1 ? $"{(int)value.TotalMinutes}분"
        : value.Minutes == 0 ? $"{(int)value.TotalHours}시간" : $"{(int)value.TotalHours}시간 {value.Minutes}분";
}

public sealed record FocusUsageDay(DateTime Date, IReadOnlyList<FocusUsageHour> Hours, double ScaleMinutes)
{
    public bool HasRecoveredSession { get; init; }
    public TimeSpan Total => TimeSpan.FromTicks(Hours.Sum(hour => hour.Duration.Ticks));
    public string TotalText => $"총 {FocusUsageHour.FormatDuration(Total)}";
    public string ScaleText => $"{ScaleMinutes:0}분";
    public string MidScaleText => $"{ScaleMinutes / 2:0}분";
}

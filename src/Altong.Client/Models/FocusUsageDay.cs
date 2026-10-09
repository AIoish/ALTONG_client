namespace Altong.Client.Models;

public sealed record FocusUsageHour(int Hour, TimeSpan Duration, double ScaleMinutes, bool IsCurrentHour)
{
    public double BarHeight => Duration.TotalMinutes / ScaleMinutes * 88;
    public string AxisLabel => Hour % 6 == 0 || Hour == 23 ? $"{Hour:00}" : "";
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

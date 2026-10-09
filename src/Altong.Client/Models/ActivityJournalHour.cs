using Altong.Client.Services;

namespace Altong.Client.Models;

/// <summary>Dashboard presentation only; the stored window records remain unchanged.</summary>
public sealed record ActivityJournalHour(DateTime LocalHour, IReadOnlyList<ActivityJournalHourApp> Apps)
{
    public string HourText => $"{LocalHour:HH}시";
    public TimeSpan Duration => TimeSpan.FromTicks(Apps.Sum(app => app.Duration.Ticks));
    public string SummaryText => $"앱 {Apps.Count}개 · 총 {FormatDuration(Duration)}";

    public static IReadOnlyList<ActivityJournalHour> Group(
        IEnumerable<ActivityJournalEntry> entries, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var hours = new SortedDictionary<DateTime, Dictionary<string, AppAccumulator>>();
        var countedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(entry => entry.StartedAt.ToUniversalTime()))
        {
            var cursor = entry.StartedAt.ToUniversalTime();
            var end = entry.EndedAt.ToUniversalTime();
            countedUntil.TryGetValue(entry.AppName, out var previousEnd);
            if (end > cursor && end > previousEnd) countedUntil[entry.AppName] = end;
            while (cursor < end)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(cursor, timeZone);
                var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
                if (!hours.TryGetValue(hour, out var apps))
                    hours[hour] = apps = new Dictionary<string, AppAccumulator>(StringComparer.OrdinalIgnoreCase);
                if (!apps.TryGetValue(entry.AppName, out var app))
                    apps[entry.AppName] = app = new AppAccumulator(entry.AppName);

                // UTC minute boundaries preserve elapsed time across local daylight-saving transitions.
                // Daily journal reads bound this loop to the day's recorded activity.
                long nextMinuteTicks = cursor.Ticks - cursor.Ticks % TimeSpan.TicksPerMinute;
                var boundary = nextMinuteTicks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerMinute
                    ? end : new DateTime(nextMinuteTicks + TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                var segmentEnd = boundary < end ? boundary : end;
                // Checkpoints and live snapshots may describe the same app interval.
                // Keep window titles, but count the overlapping time only once.
                var countedStart = cursor > previousEnd ? cursor : previousEnd;
                if (segmentEnd > countedStart) app.Ticks += (segmentEnd - countedStart).Ticks;
                if (!string.IsNullOrWhiteSpace(entry.WindowTitle)) app.Titles.Add(entry.WindowTitle);
                cursor = segmentEnd;
            }
        }

        return hours.Select(pair => new ActivityJournalHour(pair.Key,
            pair.Value.Values.OrderByDescending(app => app.Ticks)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(app => new ActivityJournalHourApp(app.Name, TimeSpan.FromTicks(app.Ticks),
                    app.Titles.Count == 0 ? "" : app.Titles.Count == 1 ? app.Titles.First()
                        : $"{app.Titles.First()} · 외 {app.Titles.Count - 1}개 창"))
                .ToArray())).ToArray();
    }

    public static IReadOnlyList<SessionAppUsage> SummarizeApps(IEnumerable<ActivityJournalHour> hours) => hours
        .SelectMany(hour => hour.Apps)
        .GroupBy(app => app.AppName, StringComparer.OrdinalIgnoreCase)
        .Select(group => new SessionAppUsage(group.Key,
            TimeSpan.FromTicks(group.Sum(app => app.Duration.Ticks)).TotalSeconds))
        .OrderByDescending(app => app.Seconds).ToArray();

    internal static string FormatDuration(TimeSpan duration)
    {
        long minutes = duration.Ticks / TimeSpan.TicksPerMinute;
        return minutes == 0 ? "1분 미만" : minutes < 60 ? $"{minutes}분"
            : minutes % 60 == 0 ? $"{minutes / 60}시간" : $"{minutes / 60}시간 {minutes % 60}분";
    }

    private sealed class AppAccumulator(string name)
    {
        public string Name { get; } = name;
        public long Ticks { get; set; }
        public HashSet<string> Titles { get; } = new(StringComparer.Ordinal);
    }
}

public sealed record ActivityJournalHourApp(string AppName, TimeSpan Duration, string WindowSummary)
{
    public string DurationText => ActivityJournalHour.FormatDuration(Duration);
}

using Altong.Client.Services;

namespace Altong.Client.Models;

/// <summary>
/// 활동 일지(도안 "앱 순위 + 언제 무엇을 썼나")의 데이터. 고른 기간 동안 집중 중 쓴 앱을 오래 쓴 순서로 세우고,
/// 하루 중 몇 시에 얼마나 썼는지 시각별로 더한다(앱별 + 그 시각 전체). 주·달 단위면 같은 시각끼리 합친다.
/// </summary>
public sealed record JournalHourlyUsage(
    IReadOnlyList<SessionAppUsage> Ranking,
    int FirstHour,
    int LastHour,
    IReadOnlyDictionary<(string AppName, int Hour), double> Seconds,
    IReadOnlyDictionary<int, double> HourTotals,
    int? BusiestHour,
    IReadOnlyList<string> BusiestApps)
{
    public const int DefaultFirstHour = 9;
    public const int DefaultLastHour = 22;

    public double TotalSeconds => Ranking.Sum(app => app.Seconds);
    public double MaxHourSeconds => HourTotals.Count == 0 ? 0 : HourTotals.Values.Max();
    public double HourSeconds(int hour) => HourTotals.GetValueOrDefault(hour);
    public double CellSeconds(string appName, int hour) => Seconds.GetValueOrDefault((appName, hour));

    /// <summary>"가장 오래 집중한 때는 10시~11시예요. 이때는 VS Code만 썼어요."의 가운데(굵게)와 뒷부분.</summary>
    public string? BusiestRangeText => BusiestHour is { } hour ? $"{hour}시~{hour + 1}시" : null;
    public string BusiestTailText => BusiestApps.Count switch
    {
        0 => "예요.",
        1 => $"예요. 이때는 {AppNameDisplayConverter.Format(BusiestApps[0])}만 썼어요.",
        _ => $"예요. 이때는 {AppNameDisplayConverter.Format(BusiestApps[0])} 사용이 가장 길었어요."
    };

    public static JournalHourlyUsage Build(IReadOnlyList<ActivityJournalHour> hours, int top = 5)
    {
        var ranking = ActivityJournalHour.SummarizeApps(hours).Take(top).ToArray();
        var names = ranking.Select(app => app.AppName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seconds = new Dictionary<(string, int), double>();
        var perHour = new Dictionary<int, Dictionary<string, double>>();
        foreach (var hour in hours)
        {
            foreach (var app in hour.Apps)
            {
                double value = app.Duration.TotalSeconds;
                if (!perHour.TryGetValue(hour.LocalHour.Hour, out var apps))
                    perHour[hour.LocalHour.Hour] = apps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                apps[app.AppName] = apps.GetValueOrDefault(app.AppName) + value;
                // 순위 이름과 대소문자가 달라도 같은 앱으로 합친다.
                string? name = ranking.FirstOrDefault(item => string.Equals(item.AppName, app.AppName, StringComparison.OrdinalIgnoreCase))?.AppName;
                if (name is not null && names.Contains(name))
                    seconds[(name, hour.LocalHour.Hour)] = seconds.GetValueOrDefault((name, hour.LocalHour.Hour)) + value;
            }
        }
        int first = Math.Min(DefaultFirstHour, perHour.Count == 0 ? DefaultFirstHour : perHour.Keys.Min());
        int last = Math.Max(DefaultLastHour, perHour.Count == 0 ? DefaultLastHour : perHour.Keys.Max());
        int? busiest = perHour.Count == 0 ? null
            : perHour.OrderByDescending(pair => pair.Value.Values.Sum()).ThenBy(pair => pair.Key).First().Key;
        var busiestApps = busiest is { } at
            ? perHour[at].Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToArray()
            : [];
        var totals = perHour.ToDictionary(pair => pair.Key, pair => pair.Value.Values.Sum());
        return new JournalHourlyUsage(ranking, first, last, seconds, totals, busiest, busiestApps);
    }
}

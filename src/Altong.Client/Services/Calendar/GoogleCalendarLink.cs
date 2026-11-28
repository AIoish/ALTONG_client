using System.Diagnostics;
using System.Globalization;
using Altong.Client.Models;

namespace Altong.Client.Services.Calendar;

public static class GoogleCalendarLink
{
    public static string Build(CalendarSchedule schedule)
    {
        if (schedule.Validate() is { } error) throw new ArgumentException(error);
        var values = new Dictionary<string, string>
        {
            ["action"] = "TEMPLATE",
            ["text"] = schedule.Title!.Trim(),
            ["dates"] = UtcTime(schedule.Start!.Value) + "/" + UtcTime(schedule.End!.Value),
            ["ctz"] = schedule.TimeZone!.Trim(),
            ["location"] = schedule.Location ?? "",
            ["details"] = schedule.Details ?? ""
        };
        return "https://calendar.google.com/calendar/render?" + string.Join("&",
            values.Select(value => value.Key + "=" + Uri.EscapeDataString(value.Value)));
    }

    public static void Open(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "calendar.google.com" || uri.AbsolutePath != "/calendar/render")
            throw new ArgumentException("올바른 Google Calendar 작성 링크가 아닙니다.");
        Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
    }

    private static string UtcTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}

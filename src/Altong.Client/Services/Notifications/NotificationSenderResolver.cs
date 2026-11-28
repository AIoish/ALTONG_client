namespace Altong.Client.Services.Notifications;

/// <summary>원본 알림에서 발신자를 구한다. Discord는 토스트 제목에 발신자와 채널을 담는다.</summary>
public static class NotificationSenderResolver
{
    public static string? Resolve(string? appName, string? sender, string? title)
    {
        var app = appName?.Trim();
        if (app?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true) app = app[..^4];
        var value = sender?.Trim();
        if (!string.Equals(app, "Discord", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(value) ? null : value;
        if (!string.IsNullOrEmpty(value) && !string.Equals(value, "Discord", StringComparison.OrdinalIgnoreCase))
            return value;
        var sourceTitle = title?.Trim();
        return !string.IsNullOrEmpty(sourceTitle) && !sourceTitle.StartsWith('#')
            && !string.Equals(sourceTitle, "Discord", StringComparison.OrdinalIgnoreCase)
                ? sourceTitle : null;
    }
}

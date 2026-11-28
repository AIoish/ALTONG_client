namespace Altong.Client.Services;

/// <summary>긴급도 표현을 한곳에서 조정한다. 색 이외에도 개수와 텍스트로 상태를 구분한다.</summary>
public sealed record DockNotificationAppearance(string Color, double GlowOpacity, double BlurRadius)
{
    public const string BaseGreen = "#72B5A6";
    public const string Background = "#000000";
    public const string EngagedBackground = "#191420";

    public static DockNotificationAppearance For(int highestUnreadUrgency) => highestUnreadUrgency switch
    {
        >= 5 => new("#EEAA92", .8, 14),
        4 => new("#DFC28E", .65, 12),
        > 0 => new(BaseGreen, .6, 11),
        _ => new(BaseGreen, 0, 0),
    };
}

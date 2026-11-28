using System.Windows;

namespace Altong.Client.Services;

/// <summary>
/// 화면 위 가운데의 알림 자리. 집중 제안 카드와 AI 요약 완료 말풍선이 같은 자리에 뜬다.
/// 둘이 동시에 보이면 나중에 뜬 카드를 먼저 뜬 카드 바로 아래에 놓아 겹치지 않게 한다.
/// </summary>
public static class TopBanner
{
    /// <summary>작업 영역 위쪽에서 카드까지의 거리.</summary>
    public const double TopMargin = 10;
    /// <summary>카드 아래 그림자 자리(창 안 여백). 쌓을 때 카드끼리의 간격 계산에서 뺀다.</summary>
    public const double ShadowRoom = 18;
    private const double Gap = 8;

    // 창은 만든 UI 스레드에서만 다룬다.
    [ThreadStatic] private static List<Window>? t_banners;

    public static void Place(Window window)
    {
        var banners = t_banners ??= [];
        if (!banners.Contains(window))
        {
            banners.Add(window);
            window.Closed += (_, _) => banners.Remove(window);
        }
        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        window.Left = Math.Round((SystemParameters.PrimaryScreenWidth - width) / 2);
        double top = Math.Max(0, SystemParameters.WorkArea.Top) + TopMargin;
        foreach (var other in banners.OrderBy(banner => banner.Top))
        {
            if (ReferenceEquals(other, window) || !other.IsVisible || other.ActualHeight <= 0) continue;
            if (top + window.ActualHeight - ShadowRoom + Gap <= other.Top + 0.5) break;
            top = Math.Max(top, other.Top + other.ActualHeight - ShadowRoom + Gap);
        }
        window.Top = Math.Round(top);
    }
}

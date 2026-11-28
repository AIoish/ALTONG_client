using System.Runtime.InteropServices;
using System.Windows.Media.Animation;

namespace Altong.Client.Services;

/// <summary>
/// 상단 노치 애니메이션을 모니터 주사율에 맞춰 그린다. WPF 애니메이션은 기본 60fps로 그려져
/// 120Hz 이상 노트북 화면에서는 Windows 기본 앱보다 끊겨 보인다.
/// </summary>
internal static class DisplayRefresh
{
    private const int VRefresh = 116;
    private static int? _hz;

    /// <summary>주 모니터 주사율(Hz). 읽지 못하거나 이상한 값이면 60으로 본다. 60~240 사이로 제한한다.</summary>
    public static int PrimaryHz => _hz ??= Read();

    /// <summary>애니메이션을 주 모니터 주사율로 그리도록 지정하고 그대로 돌려준다.</summary>
    public static T AtDisplayRate<T>(this T timeline) where T : Timeline
    {
        Timeline.SetDesiredFrameRate(timeline, PrimaryHz);
        return timeline;
    }

    /// <summary>모니터 설정이 바뀌면 다음 애니메이션부터 다시 읽는다.</summary>
    public static void Invalidate() => _hz = null;

    private static int Read()
    {
        nint dc = GetDC(0);
        if (dc == 0) return 60;
        try
        {
            int hz = GetDeviceCaps(dc, VRefresh);
            return hz <= 1 ? 60 : Math.Clamp(hz, 60, 240);
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);
}

namespace Altong.Client.Services;

/// <summary>미니바의 위치를 주 모니터 작업 영역 안으로 제한한다.</summary>
public static class DockPositioning
{
    public static double ClampTop(double top, double workAreaTop, double workAreaHeight, double dockHeight)
    {
        var maxTop = workAreaTop + Math.Max(0, workAreaHeight - dockHeight);
        return Math.Clamp(top, workAreaTop, maxTop);
    }

    public static double FromRatio(double ratio, double workAreaTop, double workAreaHeight, double dockHeight)
    {
        var travel = Math.Max(0, workAreaHeight - dockHeight);
        return workAreaTop + Math.Clamp(ratio, 0, 1) * travel;
    }

    public static double ToRatio(double top, double workAreaTop, double workAreaHeight, double dockHeight)
    {
        var travel = Math.Max(0, workAreaHeight - dockHeight);
        return travel == 0 ? 0 : (ClampTop(top, workAreaTop, workAreaHeight, dockHeight) - workAreaTop) / travel;
    }

    public static bool IsDrag(double horizontalDelta, double verticalDelta, double horizontalThreshold, double verticalThreshold) =>
        Math.Abs(horizontalDelta) >= horizontalThreshold || Math.Abs(verticalDelta) >= verticalThreshold;
}

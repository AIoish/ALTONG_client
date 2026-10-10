using System.IO;
using System.Text.Json;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace Altong.Client.Services;

/// <summary>
/// 은신형 미니바의 기준점(접힌 동그라미 중심) 계산. 좌표는 모두 해당 모니터 작업 영역의 DIP다.
/// 동그라미는 작업 영역 끝까지 붙일 수 있고, 펼친 캡슐이 끝을 넘으면 안쪽으로 밀어 넣는다.
/// 가장자리가 아니면 펼친 캡슐의 상태 점이 동그라미와 같은 자리에 온다.
/// </summary>
public static class StealthDockPositioning
{
    /// <summary>캡슐 왼쪽 끝에서 상태 점 중심까지. 그립 영역을 포함한다.</summary>
    public const double DotCenterX = 29.5;
    /// <summary>상태 점 중심에서 가장 넓은 캡슐(개수 9+)의 오른쪽 끝까지.</summary>
    public const double RightReach = 40;
    public const double HalfHeight = 14;
    /// <summary>가장 큰 접힌 동그라미(미확인, 지름 14)의 반지름. 이만큼만 남기고 끝까지 붙일 수 있다.</summary>
    public const double CollapsedRadius = 7;
    public const double SnapDistance = 16;
    public const double DefaultRightInset = 24;
    /// <summary>처음 위치에서 작업표시줄과 띄우는 간격. 펼쳐도 캡슐이 밀리지 않는다.</summary>
    public const double DefaultBottomGap = 4;

    /// <summary>접힌 동그라미가 작업 영역 밖으로 나가지 않는 기준점의 범위.</summary>
    public static Rect AnchorBounds(Rect workArea)
    {
        double left = workArea.Left + CollapsedRadius;
        double top = workArea.Top + CollapsedRadius;
        double right = Math.Max(left, workArea.Right - CollapsedRadius);
        double bottom = Math.Max(top, workArea.Bottom - CollapsedRadius);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>펼친 캡슐의 왼쪽 위. 기준점에 상태 점을 맞추되 작업 영역을 넘으면 안쪽으로 민다.</summary>
    public static Point CapsuleOrigin(Point anchor, double capsuleWidth, Rect workArea)
    {
        double height = HalfHeight * 2;
        return new Point(
            Math.Clamp(anchor.X - DotCenterX, workArea.Left, Math.Max(workArea.Left, workArea.Right - capsuleWidth)),
            Math.Clamp(anchor.Y - HalfHeight, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height)));
    }

    public static Point Clamp(Point anchor, Rect workArea)
    {
        var bounds = AnchorBounds(workArea);
        return new Point(
            Math.Clamp(anchor.X, bounds.Left, bounds.Right),
            Math.Clamp(anchor.Y, bounds.Top, bounds.Bottom));
    }

    /// <summary>가장자리 가까이 놓으면 붙인다. 화면 가운데 애매한 위치에 남는 일을 줄인다.</summary>
    public static Point Snap(Point anchor, Rect workArea)
    {
        var bounds = AnchorBounds(workArea);
        var clamped = Clamp(anchor, workArea);
        double x = clamped.X - bounds.Left <= SnapDistance ? bounds.Left
            : bounds.Right - clamped.X <= SnapDistance ? bounds.Right : clamped.X;
        double y = clamped.Y - bounds.Top <= SnapDistance ? bounds.Top
            : bounds.Bottom - clamped.Y <= SnapDistance ? bounds.Bottom : clamped.Y;
        return new Point(x, y);
    }

    /// <summary>처음에는 기존 가로 미니바와 같은 오른쪽 아래, 작업표시줄 바로 위에 둔다.</summary>
    public static Point DefaultAnchor(Rect workArea) =>
        Clamp(new Point(workArea.Right - DefaultRightInset - RightReach,
            workArea.Bottom - HalfHeight - DefaultBottomGap), workArea);

    /// <summary>해상도·배율·작업표시줄이 바뀌어도 같은 상대 위치를 유지하도록 0~1 비율로 바꾼다.</summary>
    public static Point ToRatio(Point anchor, Rect workArea)
    {
        var bounds = AnchorBounds(workArea);
        var clamped = Clamp(anchor, workArea);
        return new Point(
            bounds.Width == 0 ? 0 : (clamped.X - bounds.Left) / bounds.Width,
            bounds.Height == 0 ? 0 : (clamped.Y - bounds.Top) / bounds.Height);
    }

    public static Point FromRatio(Point ratio, Rect workArea)
    {
        var bounds = AnchorBounds(workArea);
        return new Point(
            bounds.Left + Math.Clamp(ratio.X, 0, 1) * bounds.Width,
            bounds.Top + Math.Clamp(ratio.Y, 0, 1) * bounds.Height);
    }

    /// <summary>패널·안내가 펼쳐질 방향. 기준점이 아래쪽 절반이면 위로, 오른쪽 절반이면 오른쪽 끝을 맞춘다.</summary>
    public static (bool OpenAbove, bool AlignRight) Direction(Point anchor, Rect workArea) => (
        anchor.Y >= workArea.Top + workArea.Height / 2,
        anchor.X >= workArea.Left + workArea.Width / 2);
}

/// <param name="DeviceName">모니터 장치 이름. 해당 모니터가 없으면 주 모니터를 사용한다.</param>
public sealed record StealthDockPositionSettings(string? DeviceName, double RatioX, double RatioY)
{
    public bool IsValid => double.IsFinite(RatioX) && RatioX is >= 0 and <= 1
        && double.IsFinite(RatioY) && RatioY is >= 0 and <= 1;
}

/// <summary>은신형 미니바의 위치를 모니터와 작업 영역 비율로 저장한다.</summary>
public sealed class StealthDockPositionStore
{
    private readonly string _path;

    public StealthDockPositionSettings? Settings { get; private set; }

    public StealthDockPositionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "stealth-dock-position.json");

        try
        {
            var settings = JsonSerializer.Deserialize<StealthDockPositionSettings>(File.ReadAllText(_path));
            if (settings is not { IsValid: true })
                throw new JsonException("Invalid stealth dock position.");
            Settings = settings;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 첫 실행은 기본 위치를 사용한다.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 잘못된 설정 파일은 보존하고 기본 위치를 사용한다.
        }
    }

    public void Save(StealthDockPositionSettings settings)
    {
        if (!settings.IsValid)
            throw new ArgumentOutOfRangeException(nameof(settings));

        string path = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, path, overwrite: true);
            Settings = settings;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

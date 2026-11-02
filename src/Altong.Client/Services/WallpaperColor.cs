using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Altong.Client.Services;

/// <summary>
/// 바탕화면 그림 위쪽의 평균색을 읽는다. 상단 띠를 배경색에 맞출 때 쓴다.
/// 바탕화면 그림이 없으면 단색 배경색을 쓰고, 둘 다 읽지 못하면 null을 돌려준다(띠는 검정으로 둔다).
/// 그림의 맞춤 방식(채우기·맞춤·바둑판 등)은 따지지 않고 원본 그림의 위쪽을 기준으로 한다.
/// </summary>
internal static class WallpaperColor
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const int MaxPath = 260;

    public static string? ReadTopColor()
    {
        try
        {
            var path = new StringBuilder(MaxPath * 2);
            if (SystemParametersInfo(SpiGetDeskWallpaper, (uint)path.Capacity, path, 0)
                && path.Length > 0 && File.Exists(path.ToString()))
                return FromImage(path.ToString());
            return SolidBackground();
        }
        catch (Exception ex)
        {
            // 그림 형식·권한 문제로 읽지 못해도 띠는 검정으로 계속 동작한다.
            Trace.TraceWarning($"바탕화면 색을 읽지 못했습니다: {ex.GetType().Name}");
            return null;
        }
    }

    private static string FromImage(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 96;
        image.StreamSource = stream;
        image.EndInit();
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = pixels.PixelWidth * 4;
        var buffer = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(buffer, stride, 0);
        return TopNotchLayout.AverageTopColor(buffer, pixels.PixelWidth, pixels.PixelHeight, stride);
    }

    /// <summary>
    /// 바탕화면이 바뀌었는지 비교하기 위한 값. 그림 경로·수정 시각·크기와 단색 배경값을 합친다.
    /// Windows는 같은 경로(TranscodedWallpaper)에 새 그림을 덮어쓰기도 하므로 경로만으로는 비교하지 않는다.
    /// </summary>
    public static string Signature()
    {
        try
        {
            var path = new StringBuilder(MaxPath * 2);
            string file = SystemParametersInfo(SpiGetDeskWallpaper, (uint)path.Capacity, path, 0) ? path.ToString() : "";
            var info = file.Length > 0 ? new FileInfo(file) : null;
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            return string.Join('|', file,
                info is { Exists: true } ? info.LastWriteTimeUtc.Ticks : 0,
                info is { Exists: true } ? info.Length : 0,
                key?.GetValue("Background") as string ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }

    /// <summary>단색 배경. 레지스트리에 "R G B" 형식으로 저장된다.</summary>
    private static string? SolidBackground()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
        if (key?.GetValue("Background") is not string value) return null;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !byte.TryParse(parts[0], out var r) || !byte.TryParse(parts[1], out var g)
            || !byte.TryParse(parts[2], out var b)) return null;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
}

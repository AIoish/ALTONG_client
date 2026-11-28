using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Altong.Client.Services;

/// <summary>
/// 알림 앱의 Windows 앱 ID(AUMID, 또는 실행 파일 경로)로 아이콘을 찾는다.
/// Windows가 알림 설정에 쓰는 ID는 시작 메뉴의 "모든 앱" 항목 ID와 같아서, 이름이 달라도
/// (예: "com.squirrel.slack.slack", "{6D809377-…}\Kakao\KakaoTalk\KakaoTalk.exe") 앱 아이콘을 얻을 수 있다.
/// 찾지 못하면 null을 돌려주고, 호출하는 쪽이 이름으로 찾는 <see cref="AppIconConverter"/>로 넘어간다.
/// </summary>
public static class AppIdIconResolver
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Resolve(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        lock (Cache)
            if (Cache.TryGetValue(appId, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            image = File.Exists(appId) ? FromFile(appId) : FromAppsFolder(appId);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or ExternalException) { }
        lock (Cache) Cache[appId] = image;
        return image;
    }

    private static ImageSource? FromFile(string path)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        return icon is null ? null : Freeze(Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32)));
    }

    /// <summary>shell:AppsFolder\{ID} 항목의 아이콘. 설치 앱(UWP)과 일반 프로그램 모두 같은 방법으로 얻는다.</summary>
    private static ImageSource? FromAppsFolder(string appId)
    {
        if (SHParseDisplayName(@"shell:AppsFolder\" + appId, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
        try
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                return Freeze(Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32)));
            }
            finally { DestroyIcon(info.hIcon); }
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    private static ImageSource Freeze(BitmapSource image)
    {
        image.Freeze();
        return image;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_PIDL = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint fileAttributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);
}

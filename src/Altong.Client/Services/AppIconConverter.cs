using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Altong.Client.Services;

/// <summary>알림 앱의 실행 파일/시작 메뉴 아이콘을 사용하고 없으면 공용 앱 아이콘을 표시한다.</summary>
public sealed class AppIconConverter : IValueConverter
{
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string name = value as string ?? "";
        if (_cache.TryGetValue(name, out var cached)) return cached;
        string key = Normalize(name);
        string? path = null;
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var processKey = Normalize(process.ProcessName);
                        if (key.Length > 0 && (processKey == key ||
                            Normalize(process.MainWindowTitle) == key))
                            path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
            if (path is null && key.Length > 0)
            {
                foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
                {
                    string root = Environment.GetFolderPath(folder);
                    if (!Directory.Exists(root)) continue;
                    path = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .FirstOrDefault(file => Normalize(Path.GetFileNameWithoutExtension(file)) == key);
                    if (path is not null) break;
                }
            }
            if (path is not null)
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    image.Freeze();
                    return _cache[name] = image;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        var fallback = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Application.Handle,
            Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
        fallback.Freeze();
        return _cache[name] = fallback;
    }

    private static string Normalize(string value)
    {
        string key = Path.GetFileNameWithoutExtension(value).Replace(" ", "").ToLowerInvariant();
        return key switch { "googlechrome" => "chrome", "microsoftedge" => "msedge", "microsoftteams" => "ms-teams", "카카오톡" => "kakaotalk", _ => key };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

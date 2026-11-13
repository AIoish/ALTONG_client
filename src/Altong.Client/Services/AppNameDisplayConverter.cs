using System.Globalization;
using System.Windows.Data;

namespace Altong.Client.Services;

/// <summary>표시용 실행파일 이름만 정리한다. 추적·검색·저장에 쓰는 원본 이름은 보존한다.</summary>
public sealed class AppNameDisplayConverter : IValueConverter
{
    public static string Format(string? appName)
    {
        var name = appName?.Trim();
        if (string.IsNullOrEmpty(name)) return "알 수 없는 앱";
        return name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Format(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

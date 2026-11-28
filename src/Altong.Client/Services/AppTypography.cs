using FontFamily = System.Windows.Media.FontFamily;

namespace Altong.Client.Services;

public static class AppTypography
{
    // Resolve the bundled font even when a report is hosted by another executable.
    public static FontFamily BodyFont { get; } = new(
        new Uri("pack://application:,,,/Altong.Client;component/"), "./Fonts/#Pretendard");
}

/// <summary>코드에서 만드는 글자의 크기 역할. Themes/BlackTheme.xaml의 Type* 값과 같다.</summary>
public static class TypeScale
{
    public const double Caption = 11, Label = 12, Control = 13, Body = 14, Section = 16, Title = 20, Page = 24, Metric = 32, Display = 72;
}

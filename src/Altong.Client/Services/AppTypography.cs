using FontFamily = System.Windows.Media.FontFamily;

namespace Altong.Client.Services;

public static class AppTypography
{
    // Resolve the bundled font even when a report is hosted by another executable.
    public static FontFamily BodyFont { get; } = new(
        new Uri("pack://application:,,,/Altong.Client;component/"), "./Fonts/#Pretendard");
}

using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class SessionReportWindow : Window
{
    public SessionReportWindow(SessionResult result)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => FitToWorkArea();
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        };
        var maximumSeconds = result.Apps.Count == 0 ? 0 : result.Apps.Max(app => app.Seconds);
        DataContext = result with
        {
            Apps = result.Apps.Select(app => app with
            {
                Percentage = maximumSeconds > 0 ? app.Seconds / maximumSeconds * 100 : 0
            }).ToArray()
        };
    }

    private void FitToWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var available = transform.Transform(new Vector(area.Width, area.Height));
        double width = Math.Max(1, available.X - 24);
        double height = Math.Max(1, available.Y - 24);
        MinWidth = Math.Min(520, width);
        MinHeight = Math.Min(420, height);
        Width = Math.Min(760, width);
        Height = Math.Min(780, height);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
}

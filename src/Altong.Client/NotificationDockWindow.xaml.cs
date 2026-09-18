using System.Windows;
using System.Windows.Input;

namespace Altong.Client;

public partial class NotificationDockWindow : Window
{
    private const double RightMargin = 16;
    private const double VerticalPositionRatio = 0.6;

    public NotificationDockWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenRequested;

    public void PositionOnPrimaryWorkArea()
    {
        var workArea = SystemParameters.WorkArea;

        Left = workArea.Right - Width - RightMargin;
        Top = workArea.Top + (workArea.Height * VerticalPositionRatio) - (Height / 2);
    }

    private void DockSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }
}

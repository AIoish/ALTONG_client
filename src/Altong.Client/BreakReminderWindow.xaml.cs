using System.Media;
using System.Windows;

namespace Altong.Client;

public partial class BreakReminderWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _dismissTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };

    public BreakReminderWindow()
    {
        InitializeComponent();
        _dismissTimer.Tick += (_, _) => HideReminder();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    public void Present(string remainingText)
    {
        RemainingText.Text = remainingText;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;
        if (!IsVisible) Show();
        Topmost = false;
        Topmost = true;
        _dismissTimer.Stop();
        _dismissTimer.Start();
        SystemSounds.Asterisk.Play();
    }

    public void CloseForShutdown()
    {
        _dismissTimer.Stop();
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => HideReminder();

    private void HideReminder()
    {
        _dismissTimer.Stop();
        Hide();
    }
}

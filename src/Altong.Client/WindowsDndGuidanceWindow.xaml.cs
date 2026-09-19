using System.ComponentModel;
using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class WindowsDndGuidanceWindow : Window
{
    private bool _allowClose;

    public WindowsDndGuidanceWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? CancelRequested;

    public event EventHandler? OpenSettingsRequested;

    public void Present(FocusModeGuidance guidance)
    {
        GuidanceMessageText.Text = guidance.Message;
        OpenSettingsButton.Visibility = guidance.CanOpenSettings
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    public void Dismiss()
    {
        Hide();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}

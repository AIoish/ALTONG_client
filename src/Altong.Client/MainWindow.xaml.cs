using System.ComponentModel;
using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class MainWindow : Window
{
    private readonly FocusModeService _focusModeService;

    public MainWindow()
    {
        InitializeComponent();

        _focusModeService = ((App)System.Windows.Application.Current).FocusModeService;
        _focusModeService.StateChanged += FocusModeService_StateChanged;
        UpdateFocusModeView();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (System.Windows.Application.Current is App { IsShuttingDown: false })
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _focusModeService.StateChanged -= FocusModeService_StateChanged;
        base.OnClosed(e);
    }

    private void FocusModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _focusModeService.Toggle();
    }

    private void FocusModeService_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateFocusModeView);
            return;
        }

        UpdateFocusModeView();
    }

    private void UpdateFocusModeView()
    {
        FocusModeStatusText.Text = _focusModeService.IsEnabled
            ? "집중 모드 켜짐"
            : "집중 모드 꺼짐";

        FocusModeToggleButton.Content = _focusModeService.IsEnabled
            ? "집중 모드 끄기"
            : "집중 모드 켜기";
    }
}

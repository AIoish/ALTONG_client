using System.Windows;
using System.Windows.Input;
using Altong.Client.Models;
using Altong.Client.Services;

namespace Altong.Client;

public partial class ScheduleRegistrationWindow : Window
{
    private readonly CalendarSummaryItemViewModel _row;
    private readonly Func<CalendarSummaryItemViewModel, bool> _confirmHandler;
    private bool _confirmed;

    public ScheduleRegistrationWindow(
        CalendarSummaryItemViewModel row,
        Func<CalendarSummaryItemViewModel, bool> confirmHandler)
    {
        _row = row;
        _confirmHandler = confirmHandler;
        InitializeComponent();
        DataContext = row;
        Loaded += (_, _) => TitleTextBox.Focus();
        Closed += (_, _) =>
        {
            if (!_confirmed)
                _row.ResetEdits();
        };
    }

    private void ConfirmSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmHandler(_row))
        {
            MotionBehavior.Acknowledge(DialogStatusText);
            return;
        }
        _confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}

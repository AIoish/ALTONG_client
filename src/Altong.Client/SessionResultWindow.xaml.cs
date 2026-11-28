using System.Windows;
using Altong.Client.Services;

namespace Altong.Client;

public partial class SessionResultWindow : Window
{
    public SessionResultWindow(SessionResult result)
    {
        InitializeComponent();
        DataContext = result;
        NotificationRows.ItemsSource = result.Notifications.Select(n => new NotificationDisplayItem(n)).ToArray();
    }
}

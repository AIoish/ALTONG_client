using System.Windows;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;

namespace Altong.Client;

public partial class ConfirmDialogWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialogWindow(
        string title,
        string message,
        string confirmText = "확인",
        string? cancelText = "취소",
        bool isDestructive = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (isDestructive)
        {
            HeaderIcon.Text = "\uE7BA";
            HeaderIcon.Foreground = (Brush)FindResource("BlackError");
            ConfirmButton.Style = (Style)FindResource("DestructiveDialogButton");
        }
        else
        {
            HeaderIcon.Text = "\uE946";
            HeaderIcon.Foreground = (Brush)FindResource("BlackAction");
            ConfirmButton.Style = (Style)FindResource("PrimaryDialogButton");
        }

        if (string.IsNullOrWhiteSpace(cancelText))
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.IsDefault = true;
            ConfirmButton.IsCancel = true;
        }
        else
        {
            CancelButton.Content = cancelText;
            // 파괴적 동작은 실수로 Enter를 눌러도 취소 쪽이 기본 선택되도록 유지한다.
            CancelButton.IsDefault = isDestructive;
            ConfirmButton.IsDefault = !isDestructive;
        }
    }

    public static bool ShowConfirm(
        Window? owner,
        string title,
        string message,
        string confirmText = "확인",
        string? cancelText = "취소",
        bool isDestructive = false)
    {
        var dialog = new ConfirmDialogWindow(title, message, confirmText, cancelText, isDestructive);
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        return dialog.ShowDialog() == true || dialog.Confirmed;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        CloseWithResult(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        CloseWithResult(false);
    }

    private void CloseWithResult(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}

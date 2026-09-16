using System.Windows;
using System.Windows.Controls;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Altong.MockGenerator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LoadPresetButtons();
    }

    /// <summary>
    /// 프리셋 버튼들을 동적으로 생성하여 패널에 추가합니다.
    /// </summary>
    private void LoadPresetButtons()
    {
        foreach (var preset in NotificationPresets.All)
        {
            var button = new Button
            {
                Content = preset.Label,
                Style = (Style)FindResource("PresetButtonStyle"),
                Tag = preset,
            };
            button.Click += PresetButton_Click;
            PresetPanel.Children.Add(button);
        }
    }

    /// <summary>
    /// 프리셋 버튼 클릭 시 입력 필드를 자동으로 채웁니다.
    /// </summary>
    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NotificationPreset preset })
        {
            AppNameComboBox.Text = preset.AppName;
            SenderTextBox.Text = preset.Sender;
            TitleTextBox.Text = preset.Title;
            BodyTextBox.Text = preset.Body;
        }
    }

    /// <summary>
    /// 실제 Windows 토스트 알림을 시스템에 전송합니다.
    /// </summary>
    private void SendNotification_Click(object sender, RoutedEventArgs e)
    {
        var appName = AppNameComboBox.Text.Trim();
        var senderName = SenderTextBox.Text.Trim();
        var title = TitleTextBox.Text.Trim();
        var body = BodyTextBox.Text.Trim();

        if (string.IsNullOrEmpty(title))
        {
            ShowStatus("⚠️ 제목을 입력해주세요.", isError: true);
            return;
        }

        try
        {
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(body);

            if (!string.IsNullOrEmpty(appName) || !string.IsNullOrEmpty(senderName))
            {
                var attribution = string.Join(" · ",
                    new[] { appName, senderName }.Where(s => !string.IsNullOrEmpty(s)));
                builder.AddAttributionText(attribution);
            }

            builder.Show();

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var historyEntry = $"{timestamp}  {appName} / {senderName} / {title}";
            HistoryListView.Items.Insert(0, historyEntry);

            ShowStatus($"✅ 알림 전송 완료 ({timestamp})");
        }
        catch (Exception ex)
        {
            ShowStatus($"❌ 전송 실패: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 상태 메시지를 표시합니다.
    /// </summary>
    private void ShowStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8)  // Red
                : System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1)); // Green
        StatusText.Visibility = Visibility.Visible;
    }
}

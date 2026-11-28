using System.Windows;
using System.Windows.Controls;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Altong.MockGenerator;

public partial class MainWindow : Window
{
    private readonly List<Button> _scenarioButtons = [];

    public MainWindow()
    {
        InitializeComponent();
        LoadPresetButtons();
    }

    /// <summary>
    /// 단일 알림 프리셋 및 시나리오 시퀀스 버튼들을 생성하여 패널에 추가합니다.
    /// </summary>
    private void LoadPresetButtons()
    {
        // 1. 단일 알림 프리셋
        foreach (var preset in NotificationPresets.SinglePresets)
        {
            var button = new Button
            {
                Content = preset.Label,
                Style = (Style)FindResource("PresetButtonStyle"),
                Tag = preset,
                ToolTip = $"[{preset.ExpectedCategory}] {preset.AppName} - {preset.Sender}"
            };
            button.Click += PresetButton_Click;
            PresetPanel.Children.Add(button);
        }

        // 2. 시나리오 시퀀스 프리셋
        foreach (var scenario in NotificationPresets.Scenarios)
        {
            var button = new Button
            {
                Content = scenario.Label,
                Style = (Style)FindResource("ScenarioButtonStyle"),
                Tag = scenario,
                ToolTip = $"{scenario.Description} (알림 {scenario.Items.Length}건 연속 발송)"
            };
            button.Click += ScenarioButton_Click;
            _scenarioButtons.Add(button);
            ScenarioPanel.Children.Add(button);
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
            ShowStatus($"📋 '{preset.Label}' 프리셋 로드 완료 (예상 분류: {preset.ExpectedCategory})");
        }
    }

    /// <summary>
    /// 시나리오 시퀀스를 1-Click으로 연속 발송합니다.
    /// </summary>
    private async void ScenarioButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScenarioPreset scenario })
        {
            return;
        }

        SetControlsEnabled(false);
        ShowStatus($"🎬 '{scenario.Label}' 발송 시작 (총 {scenario.Items.Length}건)...");

        try
        {
            for (int i = 0; i < scenario.Items.Length; i++)
            {
                var item = scenario.Items[i];
                ExecuteSendItem(item.AppName, item.Sender, item.Title, item.Body, isScenario: true);

                if (i < scenario.Items.Length - 1)
                {
                    // 알림 간 500ms 간격을 두어 순차 수신 보장
                    await Task.Delay(500);
                }
            }

            ShowStatus($"✅ 시나리오 전송 완료! ({scenario.Items.Length}건 전송됨 - Altong에서 요약을 확인하세요)");
        }
        catch (Exception ex)
        {
            ShowStatus($"❌ 시나리오 전송 실패: {ex.Message}", isError: true);
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    /// <summary>
    /// 즉시 알림을 전송합니다.
    /// </summary>
    private void SendNotification_Click(object sender, RoutedEventArgs e)
    {
        ExecuteSend();
    }

    /// <summary>
    /// 3초 카운트다운 후 알림을 전송하여 사용자가 작업 창을 전환할 수 있도록 합니다.
    /// </summary>
    private async void DelaySendNotification_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleTextBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            ShowStatus("⚠️ 제목을 입력해주세요.", isError: true);
            return;
        }

        SetControlsEnabled(false);

        try
        {
            for (int i = 3; i > 0; i--)
            {
                ShowStatus($"⏳ {i}초 뒤 발송됩니다... 테스트할 작업 창(VS Code 등)으로 전환하세요!");
                await Task.Delay(1000);
            }

            ExecuteSend();
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        SendButton.IsEnabled = isEnabled;
        DelaySendButton.IsEnabled = isEnabled;
        foreach (var btn in _scenarioButtons)
        {
            btn.IsEnabled = isEnabled;
        }
    }

    /// <summary>
    /// 현재 입력 필드의 내용으로 알림을 전송합니다.
    /// </summary>
    private void ExecuteSend()
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

        ExecuteSendItem(appName, senderName, title, body, isScenario: false);
    }

    /// <summary>
    /// 실제 Windows 토스트 알림 및 로컬 파이프로 전송합니다.
    /// </summary>
    private void ExecuteSendItem(string appName, string senderName, string title, string body, bool isScenario)
    {
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
                // WinRT UserNotificationListener(Windows 11)에서 확실히 텍스트 라인으로 수신할 수 있도록 보조 텍스트 추가
                builder.AddText(attribution);
            }

            builder.Show();

            // Altong.Client 개발/시연 로컬 파이프로도 전달
            TrySendToClientPipe(appName, senderName, title, body);

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var historyEntry = $"{timestamp}  {(isScenario ? "🎬 " : "")}{appName} / {senderName} / {title}";
            HistoryListView.Items.Insert(0, historyEntry);

            if (!isScenario)
            {
                ShowStatus($"✅ 알림 전송 완료 ({timestamp})");
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"❌ 전송 실패: {ex.Message}", isError: true);
        }
    }

    private static void TrySendToClientPipe(string appName, string sender, string title, string body)
    {
        Task.Run(() =>
        {
            try
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(".", "Altong_Notification_Pipe", System.IO.Pipes.PipeDirection.Out);
                client.Connect(300);
                using var writer = new System.IO.StreamWriter(client, System.Text.Encoding.UTF8);
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = $"mock_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}"[..32],
                    app_name = string.IsNullOrEmpty(appName) ? "Altong.MockGenerator" : appName,
                    sender = sender,
                    title = title,
                    body = body,
                    timestamp = DateTime.UtcNow
                });
                writer.WriteLine(json);
                writer.Flush();
            }
            catch
            {
                // Altong.Client가 실행 중이지 않을 때는 무시
            }
        });
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

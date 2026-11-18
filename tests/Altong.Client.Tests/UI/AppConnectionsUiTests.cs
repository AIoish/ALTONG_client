using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Focus;
using Altong.Client.Services.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class AppConnectionsUiTests
{
    [TestMethod]
    public void Settings_RequireExplicitDisconnect_AndNeverPretendUnavailableOAuthSucceeded()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                using var db = SqliteDatabase.CreateInMemory();
                db.Initialize();
                var focus = new FocusModeService();
                var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
                var connection = new Connection();
                var results = new SessionResultsService(db, new SqliteFocusSessionRepository(db));

                DashboardWindow CreateWindow()
                {
                    var window = new DashboardWindow(settings, new FocusRoutineService(focus, settings),
                        results, new Tracker(), focus,
                        autoDndSettings: new AutoDndSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")),
                        slackConnection: connection);
                    ((TabControl)window.FindName("DashboardTabs")).SelectedIndex = 3;
                    ((Expander)window.FindName("AppConnectionsExpander")).IsExpanded = true;
                    return window;
                }

                var disconnected = CreateWindow();
                try
                {
                    SavePreview(disconnected, "slack-disconnected.png");
                    ((Button)disconnected.FindName("SlackConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual("미연결", ((TextBlock)disconnected.FindName("SlackConnectionStatusText")).Text);
                    Assert.AreEqual(Visibility.Visible, ((TextBlock)disconnected.FindName("SlackConnectionFeedbackText")).Visibility);
                    Assert.IsFalse(((TextBlock)disconnected.FindName("SlackConnectionFeedbackText")).Text.Contains("토큰"));
                    Assert.AreEqual(0, connection.DisconnectCalls);

                    var refresh = typeof(DashboardWindow).GetMethod("UpdateClock",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    var feedback = (TextBlock)disconnected.FindName("SlackConnectionFeedbackText");
                    connection.UsesOAuth = true;
                    connection.Current = new(SlackConnectionPhase.NeedsAttention);
                    connection.Feedback = "Slack 연결을 완료하지 못했어요. 다시 시도해 주세요.";
                    refresh.Invoke(disconnected, null);
                    Assert.AreEqual(Visibility.Visible, feedback.Visibility);
                    Assert.AreEqual(connection.Feedback, feedback.Text);

                    connection.Current = new(SlackConnectionPhase.Connected, "가상 사용자", "캡스톤 개발팀");
                    connection.Feedback = null;
                    refresh.Invoke(disconnected, null);
                    Assert.AreEqual("연결됨", ((TextBlock)disconnected.FindName("SlackConnectionStatusText")).Text);
                    Assert.AreEqual(Visibility.Collapsed, feedback.Visibility);
                    Assert.AreEqual("", feedback.Text);
                }
                finally { disconnected.Close(); }

                connection.UsesOAuth = false;
                connection.Current = new(SlackConnectionPhase.Connected, "가상 사용자", "캡스톤 개발팀");
                var connected = CreateWindow();
                try
                {
                    Assert.AreEqual("가상 사용자", ((TextBlock)connected.FindName("SlackAccountText")).Text);
                    Assert.AreEqual("캡스톤 개발팀", ((TextBlock)connected.FindName("SlackWorkspaceText")).Text);
                    Assert.AreEqual(Visibility.Collapsed, ((Button)connected.FindName("SlackConnectButton")).Visibility);
                    Assert.AreEqual(Visibility.Visible, ((Button)connected.FindName("SlackDisconnectButton")).Visibility);
                    Assert.IsNull(connected.FindName("SlackAddWorkspaceButton"));
                    Assert.IsNull(connected.FindName("SlackWorkspaceSelector"));
                    SavePreview(connected, "slack-connected.png");
                    ((Button)connected.FindName("SlackDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(0, connection.DisconnectCalls, "Opening confirmation must not stop receipt or remove credentials.");
                    SavePreview(connected, "slack-disconnect-confirmation.png");
                    ((Button)connected.FindName("SlackConfirmDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(1, connection.DisconnectCalls);
                    Assert.AreEqual("수신 중지", ((TextBlock)connected.FindName("SlackConnectionStatusText")).Text);
                    Assert.AreEqual(Visibility.Collapsed, ((Border)connected.FindName("SlackAccountPanel")).Visibility);
                    Assert.AreEqual("다시 연결", ((Button)connected.FindName("SlackConnectButton")).Content);
                    Assert.AreEqual(Visibility.Collapsed, ((Button)connected.FindName("SlackDisconnectButton")).Visibility);
                    SavePreview(connected, "slack-paused.png");
                    ((Button)connected.FindName("SlackConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual("연결 중", ((TextBlock)connected.FindName("SlackConnectionStatusText")).Text);
                    Assert.AreEqual(Visibility.Collapsed, ((TextBlock)connected.FindName("SlackConnectionFeedbackText")).Visibility);
                }
                finally { connected.Close(); }

                connection.Current = new(SlackConnectionPhase.NeedsAttention, "가상 사용자", "캡스톤 개발팀");
                var expired = CreateWindow();
                try
                {
                    Assert.AreEqual("다시 연결", ((Button)expired.FindName("SlackConnectButton")).Content);
                    SavePreview(expired, "slack-reconnect.png");
                    expired.Width = 640;
                    expired.Height = 620;
                    SavePreview(expired, "slack-compact.png");
                }
                finally { expired.Close(); }

                connection.UsesOAuth = true;
                connection.Current = new(SlackConnectionPhase.AwaitingApproval);
                var approving = CreateWindow();
                try
                {
                    Assert.AreEqual("승인 대기", ((TextBlock)approving.FindName("SlackConnectionStatusText")).Text);
                    Assert.IsFalse(((Button)approving.FindName("SlackConnectButton")).IsEnabled);
                    Assert.AreEqual("연결 취소", ((Button)approving.FindName("SlackDisconnectButton")).Content);
                    ((Button)approving.FindName("SlackDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual("승인 요청 취소", ((Button)approving.FindName("SlackConfirmDisconnectButton")).Content);
                    SavePreview(approving, "slack-oauth-approval.png");
                    ((Button)approving.FindName("SlackConfirmDisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual("미연결", ((TextBlock)approving.FindName("SlackConnectionStatusText")).Text);
                    Assert.AreEqual("Slack 연결", ((Button)approving.FindName("SlackConnectButton")).Content);
                }
                finally { approving.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Settings UI did not complete.");
        if (failure is not null) throw new AssertFailedException(failure.ToString(), failure);
    }

    private static void SavePreview(DashboardWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable("ALTONG_UI_PREVIEW_DIR") is not { Length: > 0 } directory) return;
        // Render the actual WPF view without opening another app window or touching the live app.
        var content = (FrameworkElement)window.Content;
        var size = new System.Windows.Size(window.Width, window.Height);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    private sealed class Connection : ISlackConnectionController
    {
        public bool UsesOAuth { get; set; }
        public string? Feedback { get; set; }
        public SlackConnectionView Current { get; set; } = new(SlackConnectionPhase.NotConnected);
        internal int DisconnectCalls;
        public Task<bool> ConnectAsync()
        {
            if (Current.Phase != SlackConnectionPhase.Paused) return Task.FromResult(false);
            Current = new(SlackConnectionPhase.Connecting);
            return Task.FromResult(true);
        }
        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            Current = new(UsesOAuth ? SlackConnectionPhase.NotConnected : SlackConnectionPhase.Paused);
            return Task.CompletedTask;
        }
    }

    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

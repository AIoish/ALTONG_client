using Altong.Client.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// Windows 10/11 WinRT UserNotificationListener API를 연동하여
/// OS 알림 센터의 모든 알림을 실시간 감지하고 RawNotification으로 변환하는 리스너 구현체.
/// </summary>
public sealed class WinRtNotificationListener : IWindowsNotificationListener
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(2);
    private const int DedupCapacity = 50;

    private readonly UserNotificationListener _listener;
    private readonly (uint Id, DateTime AddedAt)[] _recentNotifications = new (uint, DateTime)[DedupCapacity];
    private int _recentIndex;
    private readonly object _syncRoot = new();
    private LocalPipeNotificationListener? _fallbackPipeListener;

    private bool _isRunning;
    private bool _isDisposed;

    public event EventHandler<RawNotification>? NotificationReceived;

    public bool IsRunning => _isRunning;

    public WinRtNotificationListener()
        : this(UserNotificationListener.Current)
    {
    }

    public WinRtNotificationListener(UserNotificationListener listener)
    {
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    }

    public async Task<bool> StartAsync()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isRunning)
            {
                return true;
            }
        }

        try
        {
            try
            {
                var pkgId = Windows.ApplicationModel.Package.Current?.Id?.FullName;
                AppLogger.Info($"[WinRtNotificationListener] Package Identity 확인됨: {pkgId}");
            }
            catch
            {
                AppLogger.Warn("[WinRtNotificationListener] Package Identity 없음 (Unpackaged Win32)");
            }

            // 1. 이미 허용되어 있는지 현재 권한 상태 먼저 확인 (unpackaged Win32에서 불필요한 UWP 다이얼로그 호출 방지)
            var accessStatus = _listener.GetAccessStatus();
            AppLogger.Info($"[WinRtNotificationListener] 현재 알림 권한 상태: {accessStatus}");

            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                try
                {
                    accessStatus = await _listener.RequestAccessAsync();
                    AppLogger.Info($"[WinRtNotificationListener] RequestAccessAsync 결과: {accessStatus}");
                }
                catch (Exception reqEx)
                {
                    AppLogger.Warn($"[WinRtNotificationListener] RequestAccessAsync 호출 생략/예외: {reqEx.GetType().Name} (0x{reqEx.HResult:X8})");
                }
            }

            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                AppLogger.Warn($"[WinRtNotificationListener] 알림 접근 권한 미허용: {accessStatus}");
                return false;
            }

            _listener.NotificationChanged += Listener_NotificationChanged;

            lock (_syncRoot)
            {
                _isRunning = true;
            }

            AppLogger.Info("[WinRtNotificationListener] Windows 알림 리스너 시작 완료 (수신 대기 중)");
            return true;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            AppLogger.Warn("[WinRtNotificationListener] ⚠️ Win32 언패키징 환경 감지 (0x80070490: Package Identity 부재)");
            AppLogger.Info("[WinRtNotificationListener] ➡️ 개발/시연을 위해 'Altong.MockGenerator' 전용 로컬 파이프 리스너로 자동 전환합니다.");

            _fallbackPipeListener = new LocalPipeNotificationListener();
            _fallbackPipeListener.NotificationReceived += (s, raw) => NotificationReceived?.Invoke(this, raw);
            await _fallbackPipeListener.StartAsync().ConfigureAwait(false);

            lock (_syncRoot)
            {
                _isRunning = true;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[WinRtNotificationListener] 리스너 시작 실패: {ex.GetType().Name} - {ex.Message} (0x{ex.HResult:X8})", ex);
            return false;
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _fallbackPipeListener?.Stop();
            try
            {
                _listener.NotificationChanged -= Listener_NotificationChanged;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WinRtNotificationListener] 이벤트 해제 중 예외: {ex.Message}");
            }
        }

        Console.WriteLine("[WinRtNotificationListener] Windows 알림 리스너 중지됨");
    }

    private void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
        {
            return;
        }

        uint notificationId = args.UserNotificationId;
        DateTime now = DateTime.UtcNow;

        lock (_syncRoot)
        {
            if (!_isRunning || IsDuplicateLocked(notificationId, now))
            {
                return;
            }
        }

        try
        {
            UserNotification? userNotification = sender.GetNotification(notificationId);
            if (userNotification is null)
            {
                return;
            }

            RawNotification raw = ExtractRawNotification(userNotification);
            AppLogger.Info($"[WinRtNotificationListener] 🔔 WinRT 알림 수신: {raw.AppName} - '{raw.Title}': '{raw.Body}'");
            NotificationReceived?.Invoke(this, raw);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[WinRtNotificationListener] 알림(Id: {notificationId}) 수신 처리 중 오류: {ex.Message}", ex);
        }
    }

    private bool IsDuplicateLocked(uint id, DateTime now)
    {
        for (int i = 0; i < _recentNotifications.Length; i++)
        {
            if (_recentNotifications[i].Id == id && (now - _recentNotifications[i].AddedAt) < DedupWindow)
            {
                return true;
            }
        }

        _recentNotifications[_recentIndex] = (id, now);
        _recentIndex = (_recentIndex + 1) % _recentNotifications.Length;
        return false;
    }

    private static RawNotification ExtractRawNotification(UserNotification userNotification)
    {
        string rawAppName = userNotification.AppInfo?.DisplayInfo?.DisplayName ?? "알 수 없는 앱";
        var textLines = new List<string>();

        NotificationBinding? binding = userNotification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is not null)
        {
            var elements = binding.GetTextElements();
            if (elements is not null)
            {
                foreach (var el in elements)
                {
                    if (!string.IsNullOrEmpty(el.Text))
                    {
                        textLines.Add(el.Text);
                    }
                }
            }
        }

        return ParseNotification(userNotification.Id, rawAppName, textLines, userNotification.CreationTime);
    }

    /// <summary>
    /// 토스트 텍스트 라인들을 파싱하여 AppName, Sender, Title, Body를 분리합니다.
    /// Altong.MockGenerator(AttributionText) 포맷도 자동 감지하여 지원합니다.
    /// 단위 테스트에서 직접 호출할 수 있도록 public static으로 제공합니다.
    /// </summary>
    public static RawNotification ParseNotification(
        uint id,
        string rawAppName,
        IReadOnlyList<string> textLines,
        DateTimeOffset creationTime)
    {
        string appName = rawAppName;
        string sender = string.Empty;
        string title = string.Empty;
        string body = string.Empty;

        // 1. 기본 텍스트 추출 (Line 0: Title, Line 1: Body)
        if (textLines.Count > 0)
        {
            title = textLines[0].Trim();
        }

        if (textLines.Count > 1)
        {
            body = textLines[1].Trim();
        }

        // 2. AttributionText 또는 추가 텍스트 라인 검사
        // Altong.MockGenerator의 경우 3번째 라인 등에 "{앱이름} · {발신자}" 형태로 전달됨
        for (int i = 0; i < textLines.Count; i++)
        {
            string line = textLines[i].Trim();
            if (line.Contains('·'))
            {
                var parts = line.Split('·', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[0]))
                {
                    appName = parts[0];
                    sender = parts[1];
                    // 만약 이 라인이 본문(Line 1)으로 잘못 들어갔다면 본문 초기화
                    if (i == 1 && textLines.Count == 2)
                    {
                        body = string.Empty;
                    }
                    break;
                }
            }
        }

        // 3. 만약 일반 앱에서 3줄 이상 전달된 경우 (Line 2가 발신자/보조정보인 경우)
        if (string.IsNullOrEmpty(sender) && textLines.Count > 2)
        {
            sender = textLines[2].Trim();
        }

        // 발신 앱이 MockGenerator이고 아직 appName이 안 바뀌었으면 교체
        if (appName.Equals("Altong.MockGenerator", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sender))
        {
            // Attribution에서 이미 처리됨
        }

        string notiId = $"win_{id}_{creationTime.ToUnixTimeMilliseconds()}";
        DateTime timestamp = creationTime.UtcDateTime;

        return new RawNotification(notiId, appName, sender, title, body, timestamp);
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        Stop();
        _fallbackPipeListener?.Dispose();
    }
}

using System.ComponentModel;
using Altong.Client.Data.Models;

namespace Altong.Client.Models;

/// <summary>현재 집중 세션에서만 사용하는 읽음 상태. 원본 DB 기록은 변경하지 않는다.</summary>
public sealed class DockNotificationItem(NotificationRecord notification) : INotifyPropertyChanged
{
    public NotificationRecord Notification { get; } = notification;
    public string Id => Notification.Id;
    public string AppName => Notification.AppName;
    public string AppDisplayName => AppName.Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase) ||
        AppName.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase) ? "카카오톡" : AppName;
    public string Sender => string.IsNullOrWhiteSpace(Notification.Sender) ? Notification.AppName : Notification.Sender;
    public string Title => Notification.Title;
    // 수집 결과는 보존하고, 1:1 대화처럼 이름과 제목이 같은 경우 표시만 생략한다.
    public bool HasDistinctTitle => !string.IsNullOrWhiteSpace(Title) &&
        !string.Equals(Title.Trim(), Sender.Trim(), StringComparison.OrdinalIgnoreCase);
    public string Body => Notification.Body;
    public string TimeText => Notification.ReceivedAt.ToLocalTime().ToString("HH:mm");
    public string ReceivedText => Notification.ReceivedAt.ToLocalTime().ToString("M월 d일 HH:mm");
    public int Urgency => Math.Clamp(Notification.UrgencyScore ?? 1, 1, 5);
    public string UrgencyText => Urgency >= 5 ? "긴급" : Urgency >= 4 ? "중요" : "알림";
    public bool IsRead { get; private set; }
    public string ReadText => IsRead ? "읽음" : "미확인";

    // 상세를 읽는 동안에는 원래 위치를 유지하고, 상세를 닫거나 전환할 때 확정한다.
    internal bool SortAsUnread { get; set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void MarkRead()
    {
        if (IsRead) return;
        IsRead = true;
        PropertyChanged?.Invoke(this, new(nameof(IsRead)));
        PropertyChanged?.Invoke(this, new(nameof(ReadText)));
    }
}

using System.ComponentModel;
using Altong.Client.Data.Models;
using Altong.Client.Services;

namespace Altong.Client.Models;

public sealed class NotificationHistoryItem(NotificationRecord record) : INotifyPropertyChanged
{
    public NotificationRecord Record { get; } = record;
    public string AppName => Record.AppName;
    public string DisplayName => AppNameDisplayConverter.Format(AppName);
    public string TimeText => Record.ReceivedAt.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
    public string ShortTimeText => Record.ReceivedAt.ToLocalTime().Date == DateTime.Today
        ? Record.ReceivedAt.ToLocalTime().ToString("HH:mm") : Record.ReceivedAt.ToLocalTime().ToString("M/d HH:mm");
    public string SenderText => string.IsNullOrWhiteSpace(Record.Sender) ? "발신자 정보 없음" : Record.Sender;
    public string TitleText => string.IsNullOrWhiteSpace(Record.Title) ? "제목 없음" : Record.Title;
    public string BodyText => string.IsNullOrEmpty(Record.Body) ? "본문 없음" : Record.Body;
    public string PreviewText => BodyText.Replace('\r', ' ').Replace('\n', ' ');
    public string ClassificationText => Record.IsPassed == true ? "통과" : "차단";
    /// <summary>목록 행의 제목 줄: 보낸 사람이 있으면 보낸 사람, 없으면 알림 제목.</summary>
    public string HeadlineText => string.IsNullOrWhiteSpace(Record.Sender) ? TitleText : Record.Sender!;
    /// <summary>목록 행 오른쪽 시각("18:31"). 날짜는 집중 머리글에 있다.</summary>
    public string RowTimeText => Record.ReceivedAt.ToLocalTime().ToString("HH:mm");
    /// <summary>목록 행 제목: "김팀장 · Slack". 보낸 사람이 없으면 알림 제목을 쓴다.</summary>
    public string RowTitleText => $"{HeadlineText} · {DisplayName}";
    /// <summary>본문 머리 한 줄: "Slack · 오늘 18:31 · 집중 중 차단".</summary>
    public string DetailMetaText
    {
        get
        {
            var at = Record.ReceivedAt.ToLocalTime();
            string day = at.Date == DateTime.Today ? "오늘" : at.Date == DateTime.Today.AddDays(-1) ? "어제"
                : at.ToString("M월 d일", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"));
            return $"{DisplayName} · {day} {at:HH:mm} · 집중 중 {ClassificationText}";
        }
    }
    /// <summary>같은 집중의 첫 알림 위에 붙는 머리글("오늘 18:27 – 18:56 집중 · 6개"). 첫 알림이 아니면 null.</summary>
    public string? GroupHeaderText { get; set; }
    public bool HasGroupHeader => GroupHeaderText is not null;
    public bool IsFirstGroup { get; set; }
    public string AccessibleText => $"{DisplayName} · {SenderText} · {TitleText} · {ShortTimeText} · {ClassificationText}";
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

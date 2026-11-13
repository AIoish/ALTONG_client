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

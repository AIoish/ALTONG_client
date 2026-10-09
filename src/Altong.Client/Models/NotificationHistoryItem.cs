using System.ComponentModel;
using Altong.Client.Data.Models;

namespace Altong.Client.Models;

public sealed class NotificationHistoryItem(NotificationRecord record) : INotifyPropertyChanged
{
    public NotificationRecord Record { get; } = record;
    public string AppName => Record.AppName;
    public string Title => Record.Title;
    public string SenderText => string.IsNullOrWhiteSpace(Record.Sender) ? "알림" : Record.Sender;
    public string TimeText => Record.ReceivedAt.ToLocalTime().ToString("yyyy.MM.dd HH:mm");
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

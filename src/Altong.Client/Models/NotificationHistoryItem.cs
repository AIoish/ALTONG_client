using System.ComponentModel;
using Altong.Client.Data.Models;

namespace Altong.Client.Models;

public sealed class NotificationHistoryItem(NotificationRecord record) : INotifyPropertyChanged
{
    public NotificationRecord Record { get; } = record;
    public string AppName => Record.AppName;
    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? "알 수 없는 앱" : AppName;
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

using Altong.Client.Data.Models;

namespace Altong.Client.Services;

public sealed record NotificationDisplayItem(NotificationRecord Record)
{
    public string AppName => Record.AppName;
    public string Title => Record.Title;
    public string? Sender => Record.Sender;
    public string TimeText => Record.ReceivedAt.ToLocalTime().ToString("HH:mm");
    public string ReceivedAtText => TimeText;
    public string HourText => Record.ReceivedAt.ToLocalTime().ToString("M월 d일 HH시");
    public string StatusText => Record.IsPassed switch { true => "통과", false => "차단", null => "분류 중" };
    public string StatusColor => Record.IsPassed switch { true => "#16776F", false => "#B24B57", null => "#68758A" };
}

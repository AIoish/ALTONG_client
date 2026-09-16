namespace Altong.Client.Models;

/// <summary>
/// Windows 알림 리스너가 수신한 원본 알림 데이터.
/// README §6.1 Data Contract.
/// </summary>
public record RawNotification(
    string Id,
    string AppName,
    string Sender,
    string Title,
    string Body,
    DateTime Timestamp);

namespace Altong.Client.Data.Models;

public enum ActivitySessionStatus
{
    Active,
    Completed,
}

/// <summary>사용자가 기록 시작부터 기록 마치기까지 만든 활동 범위.</summary>
public sealed record ActivitySessionRecord(
    string ActivitySessionId,
    DateTime StartedAt,
    DateTime? EndedAt = null,
    ActivitySessionStatus Status = ActivitySessionStatus.Active,
    DateTime? LastSeenAt = null);

using Altong.Client.Models;

namespace Altong.Client.Services;

/// <summary>
/// 직전 작업 세션이 종료되었을 때([OUT] 시점) 전달되는 이벤트 데이터.
/// DB 엔티티(Data.Models)와 분리하여 Services 계층의 독립성을 유지합니다.
/// </summary>
public record WindowSessionEndedEventArgs(
    string ProcessName,
    string WindowTitle,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int DurationSeconds);

/// <summary>
/// 사용자의 현재 작업 맥락(활성 창)을 추적하는 서비스 인터페이스.
/// </summary>
public interface IActiveWindowTracker : IDisposable
{
    /// <summary>
    /// 현재 안정화된 최신 활성 작업 맥락.
    /// </summary>
    CurrentContext CurrentContext { get; }

    /// <summary>
    /// 작업 맥락이 안정적으로 변경되었을 때 발생하는 이벤트.
    /// </summary>
    event EventHandler<CurrentContext>? ContextChanged;

    /// <summary>
    /// 직전 작업 세션이 종료되고 총 체류 시간이 확정되었을 때 발생하는 이벤트 ([OUT] 시점).
    /// </summary>
    event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;

    /// <summary>
    /// 주기적 폴링 추적을 시작한다.
    /// </summary>
    void Start();

    /// <summary>
    /// 추적을 중지한다.
    /// </summary>
    void Stop();

    /// <summary>
    /// 알림 수신 등 즉각적인 판별이 필요한 시점에 최신 활성 창을 즉시 캡처하여 반환한다.
    /// </summary>
    CurrentContext CaptureNow();
}


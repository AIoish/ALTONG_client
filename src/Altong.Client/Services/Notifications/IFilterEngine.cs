using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 수신된 알림 원본과 현재 활성 작업 맥락(CurrentContext)을 비교 평가하는 AI 필터 엔진 계약.
/// </summary>
public interface IFilterEngine
{
    /// <summary>
    /// 알림과 맥락을 분석하여 긴급도/연관도 점수 및 통과 여부를 판정합니다.
    /// </summary>
    Task<FilterResult> EvaluateAsync(RawNotification notification, CurrentContext context);
}

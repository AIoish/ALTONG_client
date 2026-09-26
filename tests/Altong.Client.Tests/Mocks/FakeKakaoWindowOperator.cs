using Altong.Client.Services.Notifications;

namespace Altong.Client.Tests.Mocks;

/// <summary>
/// 단위 테스트용 가짜 KakaoWindowOperator.
/// OS P/Invoke 및 UI Automation 없이 숨김/표시/닫기 호출 여부를 기록하고 검증합니다.
/// </summary>
public sealed class FakeKakaoWindowOperator : IKakaoWindowOperator
{
    public HashSet<nint> NotificationWindows { get; } = [];
    public List<nint> HiddenWindows { get; } = [];
    public List<nint> ShownWindows { get; } = [];
    public List<nint> ClosedWindows { get; } = [];

    public Func<nint, KakaoNotificationText>? TextExtractor { get; set; }

    public bool IsNotificationWindow(nint hWnd)
    {
        return NotificationWindows.Contains(hWnd);
    }

    public bool HideWindow(nint hWnd)
    {
        HiddenWindows.Add(hWnd);
        return true;
    }

    public bool ShowWindow(nint hWnd)
    {
        ShownWindows.Add(hWnd);
        return true;
    }

    public bool CloseWindow(nint hWnd)
    {
        ClosedWindows.Add(hWnd);
        return true;
    }

    public KakaoNotificationText ExtractNotificationText(nint hWnd)
    {
        if (TextExtractor != null)
        {
            return TextExtractor(hWnd);
        }

        return new KakaoNotificationText("팀원A", "팀원A", "오늘 회의 언제 시작하나요?");
    }
}

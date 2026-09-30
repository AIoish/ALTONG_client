namespace Altong.Client.Services.Notifications;

/// <summary>
/// 카카오톡 알림 팝업 창에서 추출한 텍스트 데이터.
/// </summary>
public record KakaoNotificationText(string? Sender, string Title, string Body);

/// <summary>
/// 카카오톡 윈도우 감지, 가로채기(숨김/표시/닫기), 텍스트 추출을 추상화한 인터페이스.
/// 단위 테스트 시 가짜(Mock/Fake) 객체를 주입하기 위함입니다.
/// </summary>
public interface IKakaoWindowOperator
{
    /// <summary>
    /// 주어진 윈도우 핸들이 카카오톡 알림 팝업 창인지 판정합니다.
    /// </summary>
    bool IsNotificationWindow(nint hWnd);

    /// <summary>
    /// 알림 팝업 창을 화면에서 즉시 숨깁니다 (SW_HIDE).
    /// </summary>
    bool HideWindow(nint hWnd);

    /// <summary>
    /// 알림 팝업 창을 다시 화면에 표시합니다 (SW_SHOWNOACTIVATE).
    /// </summary>
    bool ShowWindow(nint hWnd);

    /// <summary>
    /// 차단된 알림 팝업 창을 조용히 닫습니다 (WM_CLOSE).
    /// </summary>
    bool CloseWindow(nint hWnd);

    /// <summary>
    /// 알림 팝업 창의 UI 요소로부터 발신자 및 메시지 텍스트를 추출합니다.
    /// </summary>
    KakaoNotificationText ExtractNotificationText(nint hWnd);
}

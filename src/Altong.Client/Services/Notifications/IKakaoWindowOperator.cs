namespace Altong.Client.Services.Notifications;

/// <summary>
/// 카카오톡 알림 팝업 창에서 추출한 텍스트 데이터.
/// ContentHash는 OCR에 쓴 팝업 이미지의 지문이고, 알 수 없으면 0이다.
/// </summary>
public record KakaoNotificationText(string? Sender, string Title, string Body)
{
    /// <summary>방 이름 줄만 읽고 발신자·본문 줄을 읽지 못했을 때의 본문.</summary>
    public const string MissingBody = "(새 메시지)";

    /// <summary>발신자 줄까지 읽고 본문 줄을 읽지 못했을 때의 본문.</summary>
    public const string MediaBody = "(이모티콘 또는 미디어)";

    /// <summary>OCR이 실패해 창 제목만 얻었을 때의 본문.</summary>
    public const string ArrivedBody = "(새 메시지가 도착했습니다)";

    /// <summary>창이 없어 아무것도 읽지 못했을 때의 본문.</summary>
    public const string EmptyBody = "(알림 내용 없음)";

    public ulong ContentHash { get; init; }

    /// <summary>
    /// 본문이 실제 메시지가 아니라, 본문을 읽지 못해 앱이 대신 넣은 문구인지 확인한다.
    /// 이런 알림은 누가 보냈는지만 알 수 있으므로 요약 모델에 본문으로 넣지 않는다.
    /// </summary>
    public static bool IsUnreadBody(string? body) =>
        body is MissingBody or MediaBody or ArrivedBody or EmptyBody;
}

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

    /// <summary>
    /// 지금 팝업에 그려진 내용의 지문을 구합니다. 확인할 수 없으면 0입니다.
    /// </summary>
    ulong CaptureContentHash(nint hWnd);
}

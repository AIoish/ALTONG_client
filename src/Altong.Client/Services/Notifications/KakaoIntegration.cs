namespace Altong.Client.Services.Notifications;

/// <summary>
/// 카카오톡 연동(팝업 가로채기·OCR·음소거)은 개발용 기능이다. 다른 앱의 팝업을 숨기고 읽는 방식이라
/// 출시판에서는 빠질 수 있어 Release 빌드에서는 기본으로 끈다.
/// 환경 변수 ALTONG_ENABLE_KAKAO=1이면 Release에서도 켜고, 0이면 Debug에서도 끈다.
/// </summary>
public static class KakaoIntegration
{
    public const string EnableVariable = "ALTONG_ENABLE_KAKAO";

    public static bool IsEnabled => Resolve(Environment.GetEnvironmentVariable(EnableVariable));

    internal static bool Resolve(string? value) => value?.Trim() switch
    {
        "1" => true,
        "0" => false,
#if DEBUG
        _ => true,
#else
        _ => false,
#endif
    };
}

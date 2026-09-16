namespace Altong.MockGenerator;

/// <summary>
/// 모의 알림 프리셋 데이터.
/// </summary>
public record NotificationPreset(
    string Label,
    string AppName,
    string Sender,
    string Title,
    string Body);

/// <summary>
/// 내장 프리셋 템플릿 목록.
/// 다양한 긴급도/맥락의 알림을 빠르게 생성할 수 있도록 지원합니다.
/// </summary>
public static class NotificationPresets
{
    public static readonly NotificationPreset[] All =
    [
        new("🔥 긴급 업무",
            "Slack",
            "김철수 팀장",
            "[긴급] 서버 배포 오류",
            "지금 102번 서버 에러로 인해서 긴급 핫픽스 부탁드립니다."),

        new("💬 잡담",
            "KakaoTalk",
            "친구 단톡방",
            "ㅋㅋㅋ 저녁 뭐먹지",
            "삼겹살 ㄱ? 6시에 정문 앞에서 보자"),

        new("📅 일정",
            "KakaoTalk",
            "캡스톤 단톡방",
            "내일 15시 회의 참석",
            "내일(화) 15:00 종합강의동 302호 중간 점검 회의입니다"),

        new("📢 광고",
            "Chrome",
            "쿠팡",
            "로켓배송 도착 예정",
            "주문하신 상품이 오늘 도착 예정입니다"),

        new("🛠️ 개발",
            "GitHub",
            "dependabot",
            "PR #42 review requested",
            "Changes requested on feature/auth branch"),
    ];
}

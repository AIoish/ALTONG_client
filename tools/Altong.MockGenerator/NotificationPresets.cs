namespace Altong.MockGenerator;

/// <summary>
/// 개별 알림 항목 데이터.
/// </summary>
public record NotificationItem(
    string AppName,
    string Sender,
    string Title,
    string Body);

/// <summary>
/// 단일 알림 프리셋.
/// </summary>
public record NotificationPreset(
    string Label,
    string AppName,
    string Sender,
    string Title,
    string Body,
    string ExpectedCategory = "");

/// <summary>
/// AI 묶음 요약 및 일정 추출 검증용 시나리오 프리셋.
/// </summary>
public record ScenarioPreset(
    string Label,
    string Description,
    NotificationItem[] Items);

/// <summary>
/// ALTONG AI v8 카테고리 정책 및 요약 엔진에 최적화된 프리셋 컬렉션.
/// </summary>
public static class NotificationPresets
{
    /// <summary>
    /// 단일 알림 프리셋 (필터링 엔진 및 기본 카테고리 분류 검증용)
    /// </summary>
    public static readonly NotificationPreset[] SinglePresets =
    [
        new("🔥 긴급 업무 (장애)",
            "Slack",
            "김철수 팀장",
            "[긴급] 서버 배포 오류",
            "지금 102번 서버 에러로 인해서 긴급 핫픽스 부탁드립니다.",
            "긴급 업무"),

        new("📋 일반 업무 (제출)",
            "Slack",
            "가상 조교",
            "AI 과제 제출 안내",
            "보고서 PDF와 소스 코드 링크를 함께 제출해야 합니다.",
            "일반 업무"),

        new("📅 일정/회의 (세미나)",
            "KakaoTalk",
            "캡스톤 단톡방",
            "세미나 발표 안내",
            "내일 14:00~16:00 온라인 Zoom으로 세미나 발표가 있습니다.",
            "일정/회의"),

        new("🏥 개인 중요 (병원 예약)",
            "KakaoTalk",
            "밝은안과",
            "진료 예약 안내",
            "10월 15일 오후 3시 라식 검진 예약이 확정되었습니다.",
            "개인 중요"),

        new("💬 개인 일반 (잡담)",
            "KakaoTalk",
            "친구 단톡방",
            "저녁 약속",
            "삼겹살 ㄱ? 6시에 정문 앞에서 보자",
            "개인 일반"),

        new("🎁 광고/홍보 (할인)",
            "Chrome",
            "쇼핑몰",
            "가을 깜짝 할인",
            "전 품목 20% 할인 쿠폰이 발급되었습니다.",
            "광고/홍보")
    ];

    /// <summary>
    /// 시나리오 시퀀스 프리셋 (전후 맥락 요약, 상태 변경, 분산 메시지 종합 검증용)
    /// </summary>
    public static readonly ScenarioPreset[] Scenarios =
    [
        new("🔄 [시나리오] 회의 장소 변경 (연속 2건)",
            "A회의실에서 B회의실로 장소 변경 반영 검증",
            [
                new("KakaoTalk", "기획팀 단톡방", "기획 회의", "내일 오후 2시 회의실 A에서 기획 회의 진행합니다."),
                new("KakaoTalk", "기획팀 단톡방", "기획 회의", "회의실 B로 장소 변경되었습니다.")
            ]),

        new("❌ [시나리오] 회의 취소 흐름 (연속 2건)",
            "이전 일정 무효화 및 최종 취소 상태 반영 검증",
            [
                new("Slack", "연구실 공지방", "주간 회의", "내일 오전 10시 세미나실에서 주간 회의 있습니다."),
                new("Slack", "연구실 공지방", "주간 회의", "교수님 출장으로 내일 주간 회의는 취소되었습니다.")
            ]),

        new("🧩 [시나리오] 쪼개진 단톡방 대화 (연속 3건)",
            "흩어진 핵심 사실(누가/언제/어디서/무엇을) 하나로 종합 검증",
            [
                new("KakaoTalk", "캡스톤 팀장", "캡스톤 프로젝트방", "중간 점검 회의 관련해서요"),
                new("KakaoTalk", "캡스톤 팀장", "캡스톤 프로젝트방", "내일 오후 3시에"),
                new("KakaoTalk", "캡스톤 팀장", "캡스톤 프로젝트방", "종합강의동 302호에서 할게요")
            ]),

        new("⚡ [시나리오] 긴급 장애 및 롤백 복구 (연속 2건)",
            "장애 발생 후 조치 완료 흐름 종합 요약 검증",
            [
                new("Slack", "운영팀장", "[긴급] 결제 서버 오류", "결제 API 500 오류가 발생하여 긴급 대응 중입니다."),
                new("Slack", "운영팀장", "[긴급] 결제 서버 오류", "이전 버전 롤백 조치가 완료되어 정상화되었습니다.")
            ]),

        new("📄 [시나리오] 과제 마감 및 제출 형식 (연속 2건)",
            "마감 날짜/시간과 제출물 핵심 사실 보존 검증",
            [
                new("Slack", "가상 조교", "AI 과제 마감 안내", "AI 서비스 과제는 내일 18시까지 제출해 주세요."),
                new("Slack", "가상 조교", "AI 과제 마감 안내", "보고서 PDF와 소스 코드 링크를 함께 제출해야 합니다.")
            ])
    ];

    // 기존 코드 호환성용
    public static readonly NotificationPreset[] All = SinglePresets;
}

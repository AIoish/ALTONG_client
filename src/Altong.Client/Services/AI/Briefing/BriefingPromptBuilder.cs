using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Altong.Client.Services.AI.Briefing;

public sealed record NotificationPromptItem(
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);

public sealed record GroupPromptContext(
    [property: JsonPropertyName("app_name")] string AppName,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("notifications_oldest_to_newest")] IReadOnlyList<NotificationPromptItem> NotificationsOldestToNewest);

/// <summary>
/// 요약 모델에 넣는 지시문·입력과 대화 형식(Gemma 4 / Qwen ChatML) 생성기.
/// 입력(사용자 부분)은 Python AI 레포의 category_prompt.build_messages와 같은 규격을 생성합니다.
/// </summary>
public static class BriefingPromptBuilder
{
    public const int MaxSummaryLines = 3;
    public const int MaxLineLength = 160;

    public static readonly string[] AllowedCategories =
    [
        "긴급 업무",
        "일반 업무",
        "일정/회의",
        "시스템/보안",
        "개인 중요",
        "개인 일반",
        "광고/홍보",
        "기타"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public const string SystemPrompt =
        "당신은 PC 집중 세션이 끝난 뒤 차단된 알림을 요약하는 도우미입니다.\n\n" +
        "반드시 다음 규칙을 지키세요.\n" +
        "1. 입력 알림에 명시된 사실만 사용하고 추측하거나 새로운 사실을 만들지 마세요.\n" +
        "2. notifications_oldest_to_newest 배열은 오래된 알림부터 정렬되어 있으며 마지막 항목이 가장 최신입니다.\n" +
        "3. 동일한 사건의 반복 알림은 하나의 흐름으로 합치세요.\n" +
        "4. 변경, 취소, 복구, 완료처럼 상태가 달라졌다면 해당 사건에서 마지막으로 확인된 상태를 우선하세요.\n" +
        "   이미 취소되거나 바뀐 이전 일정과 상태는 최종 요약에서 제외하세요.\n" +
        "5. 뒤 알림이 앞 알림을 취소하거나 변경한 것이 아니라 새로운 정보를 보탠 경우에는 두 알림의 핵심 사실을 모두 보존하세요.\n" +
        "6. 사용자가 알아야 할 핵심 사실과 최신 상태를 우선해서 쓰세요.\n" +
        "7. 제목만 나열하지 말고 본문에 있는 핵심 사실을 포함하세요.\n" +
        "8. 일정과 제출 알림은 날짜, 시간, 제출물 등 사용자가 행동하는 데 필요한 정보를 보존하세요.\n" +
        "9. 각 줄은 알림 표시나 필드 이름 없이 그 자체로 이해되는 완전한 문장이어야 합니다.\n" +
        "10. 한국어로 짧게 작성하고 사용자 메시지에 지정된 최대 요약 줄 수를 지키세요.\n" +
        "11. 마크다운, 설명, 사고 과정 없이 아래 JSON 객체 하나만 출력하세요.\n" +
        "12. 반드시 primary_category와 summary_lines가 있는 객체를 출력하세요.\n" +
        "13. 상태가 이어지는 알림은 제목과 상태를 따로 나열하지 말고 최신 상태가 담긴 완전한 문장으로 합치세요.\n" +
        "14. 같은 발신자가 가까운 시간에 보낸 짧은 메시지들은 하나의 대화로 읽고, 서로 다른 메시지에 흩어진 핵심 사실을 합쳐서 요약하세요.\n" +
        "15. 일정·회의 알림은 원문에 있는 경우 누가, 언제, 어디서, 무엇을, 왜, 어떻게를 빠뜨리지 마세요. 원문에 없는 항목은 추측하지 마세요.\n\n\n" +
        "카테고리도 입력 원문을 보고 직접 판단하세요. 허용값과 기준:\n" +
        "- 긴급 업무: 서비스 장애·배포 실패·배치 중단 등 즉각 대응이 필요한 업무 사건 (복구·롤백 완료 알림도 같은 사건으로 유지)\n" +
        "- 일반 업무: 업무 요청·진행·완료·제출 및 자료 공유\n" +
        "- 일정/회의: 회의·행사·교육 등의 일정 안내·변경·취소\n" +
        "- 시스템/보안: 계정·인증·보안·시스템 및 시설 점검 안내 (엘리베이터 점검 포함)\n" +
        "- 개인 중요: 개인 진료·예약·결제·환불·배송 등 중요한 개인 알림\n" +
        "- 개인 일반: 일상 대화·개인적인 일반 소식\n" +
        "- 광고/홍보: 할인·쿠폰·상품 홍보\n" +
        "- 기타: 위 항목에 해당하지 않거나 근거가 부족한 알림\n" +
        "날짜가 있다는 이유만으로 일정/회의로 분류하지 마세요. 업무 제출 마감은 일반 업무,\n" +
        "개인 병원 예약은 개인 중요일 수 있고, 이 경우에도 별도로 일정 정보를 추출합니다.\n" +
        "최신 상태와 묶음의 핵심 목적을 보고 대표 카테고리 하나를 선택하세요.\n" +
        "앱이나 방 이름만으로 판단하지 말고 본문을 우선하세요.\n" +
        "오리엔테이션 장소 정정은 일정/회의, 배송·환불·이용권 갱신은 개인 중요입니다.\n" +
        "줄 수는 상한이지 목표가 아닙니다. 짧은 요청·완료 알림은 한 문장으로 충분합니다.\n" +
        "'업무 진행 관련입니다' 같은 분류 설명이나 '마감은 검토하고 올려야 합니다' 같은 빈 문장은 쓰지 마세요.\n" +
        "카카오톡 title은 방 이름입니다. 방 이름을 장소·업로드 위치·참석자로 옮기지 마세요.\n" +
        "원문에 없는 날짜·시간·마감·장소·이유는 덧붙이지 말고, 있는 대상·제출물·위치·행동은 보존하세요.\n" +
        "timestamp는 수신 시각입니다. 원문에 없는 일정 시각을 수신 시각에서 만들어 내지 마세요.\n" +
        "출력은 primary_category와 summary_lines 두 필드만 있는 JSON 객체입니다.\n" +
        "예: {\"primary_category\":\"일정/회의\",\"summary_lines\":[\"회의가 내일 오후 1시에 진행됩니다.\"]}\n" +
        "\n\n" +
        "추가 요약 지침:\n" +
        "- 마지막 알림이 웃음, 인사, 감탄 또는 단순 확인이라면\n" +
        "  그 메시지만으로 사건의 결론을 만들지 마세요.\n" +
        "- 대화 전체에서 사건의 마지막으로 확인된 상태를 찾고,\n" +
        "  이후에 추가된 중요한 사실이 있으면 함께 반영하세요.\n" +
        "- 요청, 접수, 확인 중, 완료, 취소, 변경을 구분하세요.\n" +
        "  요청이나 접수만으로 완료됐다고 쓰지 마세요.\n" +
        "- 누가 말했는지와 누가 행동하는지를 구분하세요.\n" +
        "  행동 주체가 불분명하면 특정인을 추측하지 마세요.\n" +
        "- 웃음과 의미 없는 반복 문자는 핵심이 아니면 생략하세요.\n" +
        "  잡담만 있다면 주제를 간단히 설명하고,\n" +
        "  없는 일정이나 해야 할 일을 만들지 마세요.\n";

    public static string NormalizeAppIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var parts = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var key = string.Join(" ", parts);
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }
        return key;
    }

    public static string BuildSystemPrompt() => SystemPrompt;

    public static string BuildUserPrompt(
        string appName,
        string sender,
        IReadOnlyList<NotificationPromptItem> notifications,
        int maxSummaryLines = MaxSummaryLines)
    {
        var payload = new GroupPromptContext(
            AppName: NormalizeAppIdentity(appName),
            Sender: sender ?? string.Empty,
            NotificationsOldestToNewest: notifications
        );

        var sourceJson = JsonSerializer.Serialize(payload, JsonOptions);

        return $"다음 알림 그룹의 카테고리를 분류하고 최대 {maxSummaryLines}줄로 요약하세요.\n" +
               $"입력 JSON:\n{sourceJson}\n" +
               "오래된 알림부터 정렬되어 있습니다. 최신 상태와 흩어진 핵심 사실을 보존하세요.\n" +
               "형식: {\"primary_category\":\"허용 카테고리\",\"summary_lines\":[\"요약 문장\"]}";
    }

    public static string RenderChatMl(string systemPrompt, string userPrompt) =>
        RenderChatMlPrefix(systemPrompt) + RenderChatMlSuffix(userPrompt);

    /// <summary>모든 그룹에 공통인 시스템 부분. 한 번만 처리해 두고 그룹마다 재사용한다.</summary>
    public static string RenderChatMlPrefix(string systemPrompt) =>
        $"<|im_start|>system\n{systemPrompt}<|im_end|>\n";

    /// <summary>그룹마다 달라지는 사용자 부분과 비사고 모드 assistant 시작.</summary>
    public static string RenderChatMlSuffix(string userPrompt) =>
        $"<|im_start|>user\n{userPrompt}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

    /// <summary>
    /// GGUF의 general.architecture로 대화 형식을 고른다. gemma4는 Gemma 4 형식, 나머지는 Qwen ChatML.
    /// (Gemma 3 이전은 형식이 달라 지원하지 않는다.)
    /// </summary>
    public static BriefingChatTemplate TemplateForArchitecture(string? architecture) =>
        string.Equals(architecture, "gemma4", StringComparison.OrdinalIgnoreCase)
            ? BriefingChatTemplate.Gemma4
            : BriefingChatTemplate.QwenChatMl;

    /// <summary>모든 그룹에 공통인 시스템 부분. Gemma 4는 모델 공식 템플릿처럼 앞뒤 공백을 지우고 사고 모드를 켜지 않는다.</summary>
    public static string RenderPrefix(BriefingChatTemplate template, string systemPrompt) => template switch
    {
        BriefingChatTemplate.Gemma4 => $"<|turn>system\n{systemPrompt.Trim()}<turn|>\n",
        _ => RenderChatMlPrefix(systemPrompt),
    };

    /// <summary>그룹마다 달라지는 사용자 부분과 모델 응답 시작.</summary>
    public static string RenderSuffix(BriefingChatTemplate template, string userPrompt) => template switch
    {
        BriefingChatTemplate.Gemma4 => $"<|turn>user\n{userPrompt.Trim()}<turn|>\n<|turn>model\n",
        _ => RenderChatMlSuffix(userPrompt),
    };

    /// <summary>응답을 멈출 표시. 이 표시가 나오면 그 앞까지만 응답으로 본다.</summary>
    public static IReadOnlyList<string> StopMarkers(BriefingChatTemplate template) => template switch
    {
        BriefingChatTemplate.Gemma4 => ["<turn|>", "<|turn>"],
        _ => ["<|im_end|>", "<|endoftext|>", "<|im_start|>"],
    };

    /// <summary>Gemma 4는 시작 토큰(BOS)으로 시작해야 한다. Qwen ChatML은 쓰지 않는다.</summary>
    public static bool AddBos(BriefingChatTemplate template) => template == BriefingChatTemplate.Gemma4;
}

/// <summary>요약 모델의 대화 형식.</summary>
public enum BriefingChatTemplate
{
    QwenChatMl,
    Gemma4,
}

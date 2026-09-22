using Altong.Client.Models;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 팀원 2의 온디바이스 SLM(Qwen 1.5B) 연동 전, 전체 E2E 파이프라인 검증 및
/// 추론 타임아웃 시 안전망으로 동작하는 룰 기반 필터 엔진 (임시 스텁 겸 폴백).
/// </summary>
public sealed class RuleBasedFilterEngine : IFilterEngine
{
    private static readonly string[] UrgentKeywords =
    [
        "[긴급]", "긴급", "서버", "장애", "오류", "에러", "핫픽스", "다운", "사고",
        "112", "119", "재난", "결제실패", "경고", "비상", "출동", "비밀번호 변경"
    ];

    private static readonly string[] LowPriorityKeywords =
    [
        "광고", "[광고]", "특가", "할인", "쿠폰", "점심", "저녁", "배달", "치킨",
        "ㅋㅋㅋ", "ㅎㅎㅎ", "메뉴", "쇼핑", "세일", "적립"
    ];

    public Task<FilterResult> EvaluateAsync(RawNotification notification, CurrentContext context)
    {
        string fullText = $"{notification.Title} {notification.Body}";

        // 1. 긴급 키워드 검사 (최우선 순위)
        bool isUrgent = UrgentKeywords.Any(k => fullText.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (isUrgent)
        {
            return Task.FromResult(new FilterResult(
                notification.Id,
                IsPassed: true,
                UrgencyScore: 5,
                RelevanceScore: CalculateRelevanceScore(notification, context),
                Category: "긴급 업무",
                AiSummaryReason: "긴급 키워드 감지 (서버/장애/보안 즉시 대응 권장)"));
        }

        // 2. 현재 작업 맥락과의 연관도 검사 (2순위)
        int relevance = CalculateRelevanceScore(notification, context);
        if (relevance >= 4)
        {
            return Task.FromResult(new FilterResult(
                notification.Id,
                IsPassed: true,
                UrgencyScore: 3,
                RelevanceScore: relevance,
                Category: "업무 연관",
                AiSummaryReason: $"현재 활성 작업({context.ActiveProcess})과 직접 관련된 업무 알림"));
        }

        // 3. 저우선순위 / 일상 잡담 / 프로모션 알림 검사
        bool isLowPriority = LowPriorityKeywords.Any(k => fullText.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (isLowPriority)
        {
            return Task.FromResult(new FilterResult(
                notification.Id,
                IsPassed: false,
                UrgencyScore: 1,
                RelevanceScore: 1,
                Category: "일반/잡담",
                AiSummaryReason: "일상 잡담 또는 쇼핑/프로모션 알림으로 몰입 유지를 위해 차단"));
        }

        // 4. 기본 일반 알림 (집중 모드 중에는 차단)
        return Task.FromResult(new FilterResult(
            notification.Id,
            IsPassed: false,
            UrgencyScore: 2,
            RelevanceScore: relevance,
            Category: "일반 알림",
            AiSummaryReason: "보통 중요도의 알림으로 집중 세션 종료 후 브리핑 제공"));
    }

    private static int CalculateRelevanceScore(RawNotification notification, CurrentContext context)
    {
        if (string.IsNullOrEmpty(context.ActiveProcess))
        {
            return 1;
        }

        int score = 1;
        string app = notification.AppName.ToLowerInvariant();
        string active = context.ActiveProcess.ToLowerInvariant();

        // 1) 알림 발신 앱과 현재 활성 프로세스가 직접 매칭되는 경우
        if (active.Contains(app) || app.Contains(active.Replace(".exe", "")))
        {
            score = 5;
        }
        // 2) 개발 관련 도구 매칭 (VS Code, 터미널, GitHub, Slack)
        else if ((active.Contains("code") || active.Contains("devenv") || active.Contains("antigravity")) &&
                 (app.Contains("slack") || app.Contains("github") || app.Contains("jira") || app.Contains("gitlab")))
        {
            score = 4;
        }
        // 3) 콤비 앱(RecentProcesses)에 포함된 경우
        else if (context.RecentProcesses is not null && context.RecentProcesses.Any(p => p.ToLowerInvariant().Contains(app)))
        {
            score = 3;
        }

        // 4) 창 제목과 알림 본문 간의 단어 겹침 확인
        if (!string.IsNullOrEmpty(context.WindowTitle))
        {
            string[] titleWords = context.WindowTitle.Split([' ', '-', '_', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            int matchedWords = titleWords.Count(w => w.Length >= 3 && notification.Title.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (matchedWords > 0)
            {
                score = Math.Max(score, 4);
            }
        }

        return score;
    }
}

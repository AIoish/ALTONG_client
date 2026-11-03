using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Services.AI.Briefing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class BriefingEngineTests
{
    [TestMethod]
    public void BriefingScheduleExtractor_ExtractsMeetingSchedule_Correctly()
    {
        var refTime = new DateTime(2026, 10, 13, 9, 0, 0);
        var notifications = new[]
        {
            "내일 오후 2시에 회의실 A에서 프로젝트 진행 회의를 합니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "프로젝트 진행 회의가 내일 오후 2시에 회의실 A에서 진행됩니다.",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        StringAssert.Contains(schedule!.Title, "회의");
        Assert.AreEqual("회의실 A", schedule.Location);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 14, 14, 0, 0, TimeSpan.FromHours(9)), schedule.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 14, 15, 0, 0, TimeSpan.FromHours(9)), schedule.End);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_ExtractsTimeRange_Correctly()
    {
        var refTime = new DateTime(2026, 10, 13, 9, 0, 0);
        var notifications = new[]
        {
            "오늘 14:00~16:00에 온라인 Zoom으로 세미나 발표가 있습니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "온라인 Zoom 세미나 발표",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        StringAssert.Contains(schedule!.Title, "세미나");
        Assert.AreEqual("온라인 (Zoom)", schedule.Location);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 13, 14, 0, 0, TimeSpan.FromHours(9)), schedule.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 13, 16, 0, 0, TimeSpan.FromHours(9)), schedule.End);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_NonSchedule_ReturnsFalse()
    {
        var refTime = new DateTime(2026, 10, 13, 9, 0, 0);
        var notifications = new[]
        {
            "문서 작성 완료했습니다. 확인 부탁드립니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "문서 작성 완료 및 확인 요청",
            primaryCategory: "일반 업무",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsFalse(isScheduleRelated);
        Assert.IsNull(schedule);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_AssignmentDeadlineInGeneralWork_ExtractsScheduleCorrectly()
    {
        var refTime = new DateTime(2026, 10, 27, 18, 46, 0);
        var notifications = new[]
        {
            "10월 28일 18시까지 데이터베이스 과제 제출 하십시오."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "10월 28일 18시까지 데이터베이스 과제 제출 하십시오.",
            primaryCategory: "일반 업무",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated, "과제 제출 마감은 일정/스케줄로 인정되어야 합니다.");
        Assert.IsNotNull(schedule);
        StringAssert.Contains(schedule.Title, "과제");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.FromHours(9)), schedule.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 28, 19, 0, 0, TimeSpan.FromHours(9)), schedule.End);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_Cancellation_IncludesStatus()
    {
        var refTime = new DateTime(2026, 10, 13, 9, 0, 0);
        var notifications = new[]
        {
            "오늘 3시 회의는 취소되었습니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "오늘 3시 회의 취소 안내",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        StringAssert.Contains(schedule!.Details, "취소");
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_HandlesPureJson()
    {
        var rawJson = "{\"primary_category\":\"일정/회의\",\"summary_lines\":[\"내일 2시에 주간 회의를 진행합니다.\"]}";

        var result = LlamaBriefingEngine.ParseResponse(rawJson);

        Assert.IsNotNull(result);
        Assert.AreEqual("일정/회의", result!.PrimaryCategory);
        Assert.AreEqual(1, result.SummaryLines.Count);
        Assert.AreEqual("내일 2시에 주간 회의를 진행합니다.", result.SummaryLines[0]);
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_HandlesMarkdownCodeBlock()
    {
        var rawMarkdown = "```json\n{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"작업 완료 보고서가 제출되었습니다.\"]}\n```";

        var result = LlamaBriefingEngine.ParseResponse(rawMarkdown);

        Assert.IsNotNull(result);
        Assert.AreEqual("일반 업무", result!.PrimaryCategory);
        Assert.AreEqual("작업 완료 보고서가 제출되었습니다.", result.SummaryLines[0]);
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_MalformedJson_ReturnsNullForProviderFallback()
    {
        // 엔진은 형식을 벗어난 응답을 고치거나 일부만 주워 쓰지 않는다. 대체 요약은 제공자가 만든다.
        Assert.IsNull(LlamaBriefingEngine.ParseResponse("이것은 일반 텍스트 요약입니다."));
    }

    // AI 레포 category_prompt.parse_response와 같은 기준: 기준을 벗어난 응답은 모두 거부한다.
    [DataTestMethod]
    [DataRow("{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"하나\",\"둘\",\"셋\",\"넷\"]}", DisplayName = "3줄 초과")]
    [DataRow("{\"primary_category\":\"일반 업무\",\"summary_lines\":[]}", DisplayName = "빈 배열")]
    [DataRow("{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"  \"]}", DisplayName = "빈 줄")]
    [DataRow("{\"primary_category\":\"업무\",\"summary_lines\":[\"보고서를 제출했습니다.\"]}", DisplayName = "허용되지 않은 분류")]
    [DataRow("{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"보고서를 제출했습니다.\"],\"note\":\"x\"}", DisplayName = "추가 필드")]
    [DataRow("{\"summary_lines\":[\"보고서를 제출했습니다.\"]}", DisplayName = "분류 없음")]
    [DataRow("{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"보고서를 제출했습니다.\"]} 추가 설명", DisplayName = "뒤에 붙은 문장")]
    [DataRow("{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"회의는 3시", DisplayName = "잘린 JSON")]
    [DataRow("[\"보고서를 제출했습니다.\"]", DisplayName = "배열만 출력")]
    public void LlamaBriefingEngine_ParseResponse_RejectsOutputOutsideContract(string raw)
    {
        Assert.IsNull(LlamaBriefingEngine.ParseResponse(raw));
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_RejectsLineOver160CharsAndMoreLinesThanRequested()
    {
        var longLine = new string('가', BriefingPromptBuilder.MaxLineLength + 1);
        Assert.IsNull(LlamaBriefingEngine.ParseResponse($"{{\"primary_category\":\"기타\",\"summary_lines\":[\"{longLine}\"]}}"));

        const string twoLines = "{\"primary_category\":\"기타\",\"summary_lines\":[\"첫 문장입니다.\",\"둘째 문장입니다.\"]}";
        Assert.IsNull(LlamaBriefingEngine.ParseResponse(twoLines, maxSummaryLines: 1));
        Assert.AreEqual(2, LlamaBriefingEngine.ParseResponse(twoLines, maxSummaryLines: 2)!.SummaryLines.Count);
    }

    private sealed class FakeNotificationRepository(IReadOnlyList<NotificationRecord> records) : INotificationRepository
    {
        public Task InsertAsync(NotificationRecord notification) => Task.CompletedTask;
        public Task UpdateFilterResultAsync(string id, bool isPassed, int urgencyScore, int relevanceScore, string category, string? reason) => Task.CompletedTask;
        public Task<NotificationRecord?> GetByIdAsync(string id) =>
            Task.FromResult(records.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<NotificationRecord>> GetBlockedNotificationsAsync(string? sessionId = null, int limit = 50) =>
            Task.FromResult<IReadOnlyList<NotificationRecord>>(records.Where(r => r.SessionId == sessionId).ToList());
        public Task<IReadOnlyList<NotificationRecord>> GetRecentNotificationsAsync(int limit = 50) =>
            Task.FromResult(records);
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_NoBlockedNotifications_ReturnsEmptySupply()
    {
        var fakeRepo = new FakeNotificationRepository(Array.Empty<NotificationRecord>());
        using var provider = new LlamaReportSummaryProvider(fakeRepo, "non_existent_model.gguf");

        var supply = await provider.ReadAsync("session-123", CancellationToken.None);

        Assert.IsNotNull(supply);
        Assert.AreEqual(0, supply.Items.Count);
        Assert.IsFalse(supply.IsDemo);
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_WithBlockedNotifications_FallbackGeneratesItems()
    {
        var now = new DateTime(2026, 10, 13, 10, 0, 0);
        var fakeNotifications = new[]
        {
            new NotificationRecord("1", "KakaoTalk", "김팀장", "프로젝트방", "내일 오후 3시 회의실 B에서 기획 회의 합시다.", now,
                IsPassed: false, SessionId: "session-123")
        };
        var fakeRepo = new FakeNotificationRepository(fakeNotifications);

        using var provider = new LlamaReportSummaryProvider(
            fakeRepo,
            "non_existent_model.gguf",
            () => now);

        var supply = await provider.ReadAsync("session-123", CancellationToken.None);

        Assert.IsNotNull(supply);
        Assert.AreEqual(1, supply.Items.Count);
        var item = supply.Items[0];
        StringAssert.Contains(item.Text, "김팀장");
        Assert.IsTrue(item.IsScheduleRelated);
        Assert.IsNotNull(item.Schedule);
        StringAssert.Contains(item.Schedule!.Title, "회의");
    }

    [TestMethod]
    public async Task LlamaBriefingEngine_RealModelInference_Succeeds()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "briefing", "qwen_briefing_v8_q8_0.gguf");
        if (!File.Exists(modelPath))
        {
            Assert.Inconclusive("Model file not found.");
            return;
        }

        using var engine = new LlamaBriefingEngine(modelPath);
        Assert.IsTrue(engine.IsModelAvailable);

        var notifications = new[]
        {
            new NotificationRecord("id-1", "KakaoTalk", "이과장", "기획팀 회의방",
                "내일 오후 2시에 회의실 3에서 프로젝트 기획 회의 진행합니다.", DateTime.UtcNow)
        };

        // 정확성 확인용 제한. CPU가 바쁜 PC에서는 요약 1건이 30초를 넘기도 해서(실측 17~37초) 여유를 둔다.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var output = await engine.SummarizeNotificationGroupAsync("KakaoTalk", notifications, maxSummaryLines: 2, ct: cts.Token);
        Assert.IsNotNull(output);
        Assert.IsFalse(string.IsNullOrWhiteSpace(output!.PrimaryCategory));
        Assert.IsTrue(output.SummaryLines.Count > 0);

        Console.WriteLine("\n================ [실시간 온디바이스 AI 추론 결과] ================");
        Console.WriteLine($"▶ 모델: Qwen 0.6B Q8_0 GGUF (In-Process CPU C++)");
        Console.WriteLine($"▶ 분류 카테고리: {output.PrimaryCategory}");
        for (int i = 0; i < output.SummaryLines.Count; i++)
        {
            Console.WriteLine($"▶ 핵심 요약 [{i + 1}]: {output.SummaryLines[i]}");
        }

        var (isSchedule, schedule) = BriefingScheduleExtractor.Extract(
            string.Join(" ", output.SummaryLines),
            output.PrimaryCategory,
            notifications.Select(n => n.Body).ToList(),
            DateTime.Now);

        if (isSchedule && schedule is not null)
        {
            Console.WriteLine($"▶ 캘린더 일정 연동: {schedule.Title}");
            Console.WriteLine($"   일시: {schedule.Start:yyyy-MM-dd HH:mm} ~ {schedule.End:HH:mm}");
            Console.WriteLine($"   장소: {schedule.Location}");
        }
        Console.WriteLine("=================================================================\n");
        if (Environment.GetEnvironmentVariable("ALTONG_RUN_SLOW_AI_TESTS") != "1")
        {
            return;
        }

        var scenarios = new (string Name, string App, string Sender, (string Title, string Body)[] Items)[]
        {
            (
                "1. 긴급 업무 (서버 오류 핫픽스)",
                "Slack",
                "김철수 팀장",
                [
                    ("[긴급] 서버 배포 오류", "지금 102번 서버 에러로 인해서 긴급 핫픽스 부탁드립니다.")
                ]
            ),
            (
                "2. 일정 및 회의 (시간/장소)",
                "KakaoTalk",
                "캡스톤 단톡방",
                [
                    ("내일 15시 회의 참석", "내일(화) 15:00 종합강의동 302호 중간 점검 회의입니다")
                ]
            ),
            (
                "3. 상태 변경 흐름 (회의 공지 -> 취소)",
                "KakaoTalk",
                "연구실 단톡방",
                [
                    ("주간 회의", "내일 오전 10시에 주간 회의 진행합니다."),
                    ("주간 회의 취소", "교수님 출장으로 내일 주간 회의는 취소되었습니다.")
                ]
            ),
            (
                "4. 일상 잡담",
                "KakaoTalk",
                "친구 단톡방",
                [
                    ("저녁 뭐먹지", "삼겹살 ㄱ? 6시에 정문 앞에서 보자")
                ]
            )
        };

        foreach (var (name, app, sender, items) in scenarios)
        {
            var notis = items.Select((it, idx) =>
                new NotificationRecord($"id-{idx}", app, sender, it.Title, it.Body, DateTime.UtcNow.AddMinutes(idx * 5))
            ).ToList();

            using var scenarioCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var scenarioOutput = await engine.SummarizeNotificationGroupAsync(app, sender, notis, maxSummaryLines: 2, ct: scenarioCts.Token);

            Console.WriteLine($"\n================== [시나리오 검증: {name}] ==================");
            Console.WriteLine($"[입력 알림]");
            foreach (var item in items)
            {
                Console.WriteLine($"  - [{item.Title}] {item.Body}");
            }
            Console.WriteLine($"[AI 출력 결과]");
            Console.WriteLine($"  ▶ 카테고리: {scenarioOutput?.PrimaryCategory ?? "(null)"}");
            if (scenarioOutput?.SummaryLines != null)
            {
                foreach (var line in scenarioOutput.SummaryLines)
                {
                    Console.WriteLine($"  ▶ 핵심 요약: {line}");
                }
            }

            var (isSched, sched) = BriefingScheduleExtractor.Extract(
                string.Join(" ", scenarioOutput?.SummaryLines ?? []),
                scenarioOutput?.PrimaryCategory ?? "기타",
                items.Select(i => i.Body).ToList(),
                DateTime.Now);

            Console.WriteLine($"[일정 연동 판정]: {(isSched ? $"성공 (제목: {sched?.Title}, 일시: {sched?.Start:MM/dd HH:mm}~{sched?.End:HH:mm}, 장소: {sched?.Location})" : "일정 없음(일반 알림)")}");
            Console.WriteLine("==========================================================================");

            Assert.IsNotNull(scenarioOutput);
            Assert.IsFalse(string.IsNullOrWhiteSpace(scenarioOutput.PrimaryCategory));
            Assert.IsTrue(scenarioOutput.SummaryLines.Count > 0);
        }
    }

    [TestMethod]
    public void ComputeDeterministicId_ReturnsIdenticalId_ForIdenticalInputs()
    {
        var id1 = LlamaReportSummaryProvider.ComputeDeterministicId("sum", "session-1", "group-a", ["noti-2", "noti-1"]);
        var id2 = LlamaReportSummaryProvider.ComputeDeterministicId("sum", "session-1", "group-a", ["noti-1", "noti-2"]);
        var id3 = LlamaReportSummaryProvider.ComputeDeterministicId("sum", "session-2", "group-a", ["noti-1", "noti-2"]);

        Assert.AreEqual(id1, id2);
        Assert.AreNotEqual(id1, id3);
        StringAssert.StartsWith(id1, "sum-");
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_ReadAsync_ReturnsConsistentDeterministicIdsAcrossCalls()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0);
        var fakeNotifications = new[]
        {
            new NotificationRecord("noti-1", "KakaoTalk", "김팀장", "프로젝트방", "내일 오후 3시 회의실 B에서 기획 회의 합시다.", now,
                IsPassed: false, SessionId: "session-abc")
        };
        var fakeRepo = new FakeNotificationRepository(fakeNotifications);

        using var provider = new LlamaReportSummaryProvider(
            fakeRepo,
            "non_existent_model.gguf",
            () => now);

        var first = await provider.ReadAsync("session-abc", CancellationToken.None);
        var second = await provider.ReadAsync("session-abc", CancellationToken.None);

        Assert.AreEqual(first.Items[0].SummaryId, second.Items[0].SummaryId);
        Assert.AreEqual(first.Items[0].Schedule?.EventId, second.Items[0].Schedule?.EventId);
        StringAssert.StartsWith(first.Items[0].SummaryId, "sum-");
        StringAssert.StartsWith(first.Items[0].Schedule!.EventId, "evt-");
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_StripsCompletedThinkBlock()
    {
        var rawWithThink = "<think>\nLet me analyze the notification carefully. The user has a meeting tomorrow.\n</think>\n{\"primary_category\":\"일정/회의\",\"summary_lines\":[\"내일 오후 3시에 기획 회의를 진행합니다.\"]}";

        var result = LlamaBriefingEngine.ParseResponse(rawWithThink);

        Assert.IsNotNull(result);
        Assert.AreEqual("일정/회의", result!.PrimaryCategory);
        Assert.AreEqual(1, result.SummaryLines.Count);
        Assert.AreEqual("내일 오후 3시에 기획 회의를 진행합니다.", result.SummaryLines[0]);
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_TruncatedThinkBlock_DoesNotLeakEnglishMonologue()
    {
        var truncatedThink = "<think>\nOkay, the user wants to summarize \"meeting room changed\" and \"tomorrow at 2pm\" into Korean JSON.";

        Assert.IsNull(LlamaBriefingEngine.ParseResponse(truncatedThink));
    }

    [TestMethod]
    public void BriefingScheduleExtractor_PrioritizesLatestLocationAndDateChange()
    {
        var refTime = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        var notifications = new[]
        {
            "9월 28일 오후 2시 회의실 A에서 기획 회의 진행합니다.",
            "9월 30일 오후 4시 회의실 B로 일정 및 장소 변경되었습니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "기획 회의가 9월 30일 오후 4시 회의실 B로 변경되었습니다.",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        Assert.AreEqual("회의실 B", schedule!.Location);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.FromHours(9)), schedule.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.FromHours(9)), schedule.End);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_ExtractsAmPmTimeRangeAndBuildingRoomLocation_AndCleansTitle()
    {
        var refTime = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var notifications = new[]
        {
            "중간 점검 회의 관련해서요",
            "내일 오후 2시~4시에",
            "종합강의동 302호에서 할게요"
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "내일 오후 2시에 회의실 변경 없이 종합강의동 302호에서 중간 점검 회의를 진행합니다.",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        Assert.AreEqual("중간 점검 회의", schedule!.Title);
        Assert.AreEqual("종합강의동 302호", schedule.Location);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 14, 14, 0, 0, TimeSpan.FromHours(9)), schedule.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 14, 16, 0, 0, TimeSpan.FromHours(9)), schedule.End);
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_GroupsConsecutiveNotificationsBySenderAndNormalizesAppName()
    {
        // 카카오톡 title은 방 이름이다 (AI 레포와의 계약). 같은 방·같은 발신자의 연속 알림.
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var fakeNotifications = new[]
        {
            new NotificationRecord("1", "KakaoTalk.exe", "조교", "연구실 단톡방", "내일 오전 10시에 주간 회의 진행합니다.", now,
                IsPassed: false, SessionId: "session-group"),
            new NotificationRecord("2", "KakaoTalk", "조교", "연구실 단톡방", "교수님 출장으로 내일 주간 회의는 취소되었습니다.", now.AddMinutes(1),
                IsPassed: false, SessionId: "session-group")
        };
        var fakeRepo = new FakeNotificationRepository(fakeNotifications);

        using var provider = new LlamaReportSummaryProvider(
            fakeRepo,
            "non_existent_model.gguf",
            () => now);

        var supply = await provider.ReadAsync("session-group", CancellationToken.None);

        Assert.AreEqual(1, supply.Items.Count, "같은 방·같은 발신자의 연속 알림은 단일 그룹으로 묶여야 합니다.");
        Assert.AreEqual("KakaoTalk", supply.Items[0].AppName, ".exe 접미사가 제거된 정규화 앱 이름이어야 합니다.");
        StringAssert.Contains(supply.Items[0].Text, "취소되었습니다");
        Assert.AreEqual("조교", supply.Items[0].Sender);
        Assert.AreEqual("연구실 단톡방", supply.Items[0].Room);
        Assert.AreEqual("연구실 단톡방 · 조교", supply.Items[0].SourceLabel);
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_SplitsSameSenderByRoomAndThirtyMinuteSpan()
    {
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var fakeNotifications = new[]
        {
            new NotificationRecord("1", "KakaoTalk", "이시영", "Aton~", "회의 자료 올렸어요", now, IsPassed: false, SessionId: "s"),
            new NotificationRecord("2", "KakaoTalk", "이시영", "가족방", "회의 끝나고 전화할게", now.AddMinutes(1), IsPassed: false, SessionId: "s"),
            new NotificationRecord("3", "KakaoTalk", "이시영", "Aton~", "회의 자료 다시 올렸어요", now.AddMinutes(40), IsPassed: false, SessionId: "s"),
        };

        using var provider = new LlamaReportSummaryProvider(new FakeNotificationRepository(fakeNotifications), "non_existent_model.gguf", () => now);
        var supply = await provider.ReadAsync("s", CancellationToken.None);

        Assert.AreEqual(3, supply.Items.Count, "방이 다르거나 30분을 넘기면 다른 그룹입니다.");
        CollectionAssert.AreEqual(new[] { "Aton~", "가족방", "Aton~" }, supply.Items.Select(i => i.Room).ToArray());
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_SplitsLongConversationEvenlyInTimeOrder()
    {
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var fakeNotifications = Enumerable.Range(0, 15).Select(i => new NotificationRecord(
            $"m{i:00}", "KakaoTalk", "김팀장", "프로젝트 팀방", $"메시지 {i}", now.AddMinutes(i),
            IsPassed: false, SessionId: "long")).ToArray();

        using var provider = new LlamaReportSummaryProvider(new FakeNotificationRepository(fakeNotifications), "non_existent_model.gguf", () => now);
        var supply = await provider.ReadAsync("long", CancellationToken.None);

        // 15개 → 5 + 5 + 5. 대체 요약은 덩어리의 마지막 메시지를 담는다.
        Assert.AreEqual(3, supply.Items.Count);
        StringAssert.Contains(supply.Items[0].Text, "외 4건: 메시지 4");
        StringAssert.Contains(supply.Items[1].Text, "외 4건: 메시지 9");
        StringAssert.Contains(supply.Items[2].Text, "외 4건: 메시지 14");
        Assert.AreEqual(3, supply.Items.Select(i => i.SummaryId).Distinct().Count());
    }

    [TestMethod]
    public async Task LlamaReportSummaryProvider_EvictsCanceledTaskFromCache_AllowingRetry()
    {
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var fakeNotifications = new[]
        {
            new NotificationRecord("1", "Slack.exe", "운영팀장", "[긴급] 서버 오류", "서버 에러 발생", now,
                IsPassed: false, SessionId: "session-retry")
        };
        var fakeRepo = new FakeNotificationRepository(fakeNotifications);

        using var provider = new LlamaReportSummaryProvider(
            fakeRepo,
            "non_existent_model.gguf",
            () => now);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => provider.ReadAsync("session-retry", cts.Token));

        var retrySupply = await provider.ReadAsync("session-retry", CancellationToken.None);
        Assert.AreEqual(1, retrySupply.Items.Count);
        Assert.AreEqual("Slack", retrySupply.Items[0].AppName);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_ExtractsTitleWithKoreanParticles_AndStandaloneRoomLocation()
    {
        var refTime = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var notifications = new[]
        {
            "내일 오후 2시에 캡스톤 발표가 302호에서 진행됩니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "내일 오후 2시에 캡스톤 발표가 302호에서 진행됩니다.",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        Assert.AreEqual("캡스톤 발표", schedule!.Title);
        Assert.AreEqual("302호", schedule.Location);
    }

    [TestMethod]
    public void BriefingScheduleExtractor_SingleSentenceScheduleChange_ExtractsUpdatedDateTimeAndLocation()
    {
        var refTime = new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);
        var notifications = new[]
        {
            "9월 28일 오후 2시 회의실 A에서 진행 예정이던 기획 회의가 9월 30일 오후 4시 회의실 B로 변경되었습니다."
        };

        var (isScheduleRelated, schedule) = BriefingScheduleExtractor.Extract(
            groupSummary: "9월 28일 오후 2시 회의실 A 예정이던 기획 회의가 9월 30일 오후 4시 회의실 B로 변경되었습니다.",
            primaryCategory: "일정/회의",
            notificationTexts: notifications,
            referenceTime: refTime);

        Assert.IsTrue(isScheduleRelated);
        Assert.IsNotNull(schedule);
        Assert.AreEqual("회의실 B", schedule!.Location);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.FromHours(9)), schedule.Start);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.FromHours(9)), schedule.End);
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_UnclosedSecondThinkBlock_ReturnsNull()
    {
        var raw = "<think>first thought</think><think>second unclosed thought with \"english quoted phrase\"";

        Assert.IsNull(LlamaBriefingEngine.ParseResponse(raw));
    }
}

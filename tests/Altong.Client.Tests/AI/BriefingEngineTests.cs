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
        var fallback = Array.Empty<NotificationRecord>();

        var result = LlamaBriefingEngine.ParseResponse(rawJson, fallback);

        Assert.IsNotNull(result);
        Assert.AreEqual("일정/회의", result!.PrimaryCategory);
        Assert.AreEqual(1, result.SummaryLines.Count);
        Assert.AreEqual("내일 2시에 주간 회의를 진행합니다.", result.SummaryLines[0]);
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_HandlesMarkdownCodeBlock()
    {
        var rawMarkdown = "```json\n{\"primary_category\":\"일반 업무\",\"summary_lines\":[\"작업 완료 보고서가 제출되었습니다.\"]}\n```";
        var fallback = Array.Empty<NotificationRecord>();

        var result = LlamaBriefingEngine.ParseResponse(rawMarkdown, fallback);

        Assert.IsNotNull(result);
        Assert.AreEqual("일반 업무", result!.PrimaryCategory);
        Assert.AreEqual("작업 완료 보고서가 제출되었습니다.", result.SummaryLines[0]);
    }

    [TestMethod]
    public void LlamaBriefingEngine_ParseResponse_MalformedJson_FallsBackGracefully()
    {
        var malformed = "이것은 일반 텍스트 요약입니다.";
        var fallback = new[]
        {
            new NotificationRecord("id-1", "KakaoTalk", "홍길동", "팀방", "내일 회의 확인 바랍니다.", DateTime.UtcNow)
        };

        var result = LlamaBriefingEngine.ParseResponse(malformed, fallback);

        Assert.IsNotNull(result);
        Assert.AreEqual("기타", result!.PrimaryCategory);
        Assert.AreEqual(1, result.SummaryLines.Count);
        Assert.AreEqual("홍길동: 내일 회의 확인 바랍니다.", result.SummaryLines[0]);
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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

            using var scenarioCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
}

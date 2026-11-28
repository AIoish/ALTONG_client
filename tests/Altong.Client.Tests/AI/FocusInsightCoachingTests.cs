using System.IO;
using Altong.Client.Models;
using Altong.Client.Services.AI.Briefing;
using Altong.Client.Services.Insights;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.AI;

[TestClass]
public sealed class FocusInsightCoachingTests
{
    private static FocusInsightSnapshot CreatePopulatedSnapshot(
        InsightPeriod period = InsightPeriod.Today,
        string fingerprint = "fp-v1",
        InsightQualityMetrics? quality = null,
        int blockedCount = 9)
    {
        return new FocusInsightSnapshot(
            Period: period,
            GeneratedAtUtc: new DateTime(2026, 11, 26, 5, 0, 0, DateTimeKind.Utc),
            TotalFocusDuration: TimeSpan.FromMinutes(95),
            FocusSessionCount: 2,
            TotalNotifications: blockedCount + 3,
            BlockedCount: blockedCount,
            PassedCount: 3,
            UrgentPassedCount: 1,
            RelevancePassedCount: 2,
            OtherPassedCount: 0,
            BlockRatePercent: blockedCount == 0 ? 0.0 : 75.0,
            GoldenHour: 14,
            GoldenHourDuration: TimeSpan.FromMinutes(55),
            DistractionPeakHour: 15,
            DistractionPeakCount: 6,
            HourlyBuckets: Array.Empty<InsightHourlyBucket>(),
            TopDistractors:
            [
                new InsightDistractorItem("KakaoTalk · 팀 단톡방", "KakaoTalk", "팀 단톡방", 6, 5, 1, 83.3),
                new InsightDistractorItem("Slack · #general", "Slack", "#general", 4, 3, 1, 75.0),
                new InsightDistractorItem("Discord", "Discord", null, 2, 1, 1, 50.0),
                new InsightDistractorItem("Mail", "Mail", null, 1, 0, 1, 0.0)
            ],
            TopWorkContexts:
            [
                new InsightWorkContextItem("Code.exe", "VS Code", TimeSpan.FromMinutes(60), 7, 5, 2, 71.4),
                new InsightWorkContextItem("chrome.exe", "Chrome", TimeSpan.FromMinutes(25), 3, 3, 0, 100.0),
                new InsightWorkContextItem("Notion.exe", "Notion", TimeSpan.FromMinutes(10), 2, 1, 1, 50.0),
                new InsightWorkContextItem("Figma.exe", "Figma", TimeSpan.FromMinutes(5), 0, 0, 0, 0.0)
            ],
            DataFingerprint: fingerprint,
            RuleSummaryLine: "오늘 1시간 35분 집중하며 9건 차단",
            Quality: quality);
    }

    private static InsightQualityMetrics CreateSampleQuality(
        int flowEligible = 8,
        int flowProtected = 7,
        double flowRate = 87.5,
        TimeSpan? workDuration = null,
        TimeSpan? neutralDuration = null,
        TimeSpan? leisureDuration = null,
        double workPurity = 72.0,
        double neutralPercent = 21.0,
        double leisurePercent = 7.0,
        string? topSelfDistractionApp = "KakaoTalk",
        TimeSpan? topSelfDistractionDuration = null,
        int deepFocusCount = 3,
        TimeSpan? avgSessionDuration = null,
        int currentStreakDays = 5,
        double? periodChangePercent = 18.4,
        double? selfDistractionChangePercent = null)
    {
        return new InsightQualityMetrics(
            FlowProtectionEligibleCount: flowEligible,
            FlowProtectedCount: flowProtected,
            FlowProtectionRatePercent: flowRate,
            WorkDuration: workDuration ?? TimeSpan.FromMinutes(68),
            NeutralDuration: neutralDuration ?? TimeSpan.FromMinutes(20),
            LeisureDuration: leisureDuration ?? TimeSpan.FromMinutes(7),
            WorkPurityPercent: workPurity,
            NeutralPercent: neutralPercent,
            LeisurePercent: leisurePercent,
            TopSelfDistractionApp: topSelfDistractionApp,
            TopSelfDistractionDuration: topSelfDistractionDuration ?? TimeSpan.FromMinutes(22),
            DeepFocusSessionCount: deepFocusCount,
            AverageSessionDuration: avgSessionDuration ?? TimeSpan.FromMinutes(42),
            CurrentStreakDays: currentStreakDays,
            PeriodChangePercent: periodChangePercent,
            DailyTrends: Array.Empty<InsightDailyTrendItem>(),
            SelfDistractionChangePercent: selfDistractionChangePercent);
    }

    private static FocusInsightSnapshot CreateEmptySnapshot(
        InsightPeriod period = InsightPeriod.Today,
        string fingerprint = "fp-empty")
    {
        return new FocusInsightSnapshot(
            Period: period,
            GeneratedAtUtc: new DateTime(2026, 11, 26, 5, 0, 0, DateTimeKind.Utc),
            TotalFocusDuration: TimeSpan.Zero,
            FocusSessionCount: 0,
            TotalNotifications: 0,
            BlockedCount: 0,
            PassedCount: 0,
            UrgentPassedCount: 0,
            RelevancePassedCount: 0,
            OtherPassedCount: 0,
            BlockRatePercent: 0.0,
            GoldenHour: null,
            GoldenHourDuration: TimeSpan.Zero,
            DistractionPeakHour: null,
            DistractionPeakCount: 0,
            HourlyBuckets: Array.Empty<InsightHourlyBucket>(),
            TopDistractors: Array.Empty<InsightDistractorItem>(),
            TopWorkContexts: Array.Empty<InsightWorkContextItem>(),
            DataFingerprint: fingerprint,
            RuleSummaryLine: string.Empty);
    }

    private static string CreateTempCachePath() =>
        Path.Combine(Path.GetTempPath(), $"altong-coaching-cache-{Guid.NewGuid():N}", "cache.json");

    private static void CleanupTempCache(string cachePath)
    {
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task GenerateAsync_ValidJsonResponse_ParsesAndReturnsAiResult()
    {
        var snapshot = CreatePopulatedSnapshot();
        var service = new FocusInsightCoachingService(
            textGenerator: (_, _, _) => Task.FromResult<string?>(
                "{\"headline\":\"14시 골든타임에 몰입이 가장 높았어요.\",\"findings\":[\"총 1시간 35분 동안 알림 9건을 차단했어요.\",\"KakaoTalk · 팀 단톡방 알림이 가장 많았어요.\",\"추가 세 번째 항목은 제외돼요.\"],\"action\":\"15시 방해 피크 전에 집중 모드를 유지해 보세요.\"}"),
            isAiAvailable: true);

        var result = await service.GenerateAsync(snapshot);

        Assert.IsTrue(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual("14시 골든타임에 몰입이 가장 높았어요.", result.Headline);
        Assert.AreEqual(2, result.Findings.Count);
        Assert.AreEqual("총 1시간 35분 동안 알림 9건을 차단했어요.", result.Findings[0]);
        Assert.AreEqual("KakaoTalk · 팀 단톡방 알림이 가장 많았어요.", result.Findings[1]);
        Assert.AreEqual("15시 방해 피크 전에 집중 모드를 유지해 보세요.", result.ActionTip);
        Assert.AreEqual(snapshot.DataFingerprint, result.DataFingerprint);
    }

    [TestMethod]
    public async Task GenerateAsync_MarkdownFencedJsonResponse_ParsesCleanly()
    {
        var snapshot = CreatePopulatedSnapshot();
        var fenced = "```json\r\n{\"headline\":\"VS Code 작업 중 방해를 잘 막았어요.\",\"findings\":[\"차단율이 75%로 안정적이에요.\",\"긴급 알림 1건만 선별 통과되었어요.\"],\"action\":\"오후 2시 집중 블록을 고정해 보세요.\"}\r\n```";
        var service = new FocusInsightCoachingService(
            textGenerator: (_, _, _) => Task.FromResult<string?>(fenced),
            isAiAvailable: true);

        var result = await service.GenerateAsync(snapshot);

        Assert.IsTrue(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual("VS Code 작업 중 방해를 잘 막았어요.", result.Headline);
        Assert.AreEqual(2, result.Findings.Count);
        Assert.AreEqual("오후 2시 집중 블록을 고정해 보세요.", result.ActionTip);
    }

    [TestMethod]
    public async Task GenerateAsync_GeneratorReturnsNull_FallsBackToRuleResult()
    {
        var snapshot = CreatePopulatedSnapshot();
        var service = new FocusInsightCoachingService(
            textGenerator: (_, _, _) => Task.FromResult<string?>(null),
            isAiAvailable: true);

        var result = await service.GenerateAsync(snapshot);

        Assert.IsFalse(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Headline));
        Assert.AreEqual(2, result.Findings.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ActionTip));
    }

    [TestMethod]
    public async Task GenerateAsync_GeneratorThrowsException_FallsBackToRuleResultWithoutCrashing()
    {
        var snapshot = CreatePopulatedSnapshot();
        var service = new FocusInsightCoachingService(
            textGenerator: (_, _, _) => throw new InvalidOperationException("inference failed"),
            isAiAvailable: true);

        var result = await service.GenerateAsync(snapshot);

        Assert.IsFalse(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual(2, result.Findings.Count);
        StringAssert.Contains(result.Headline, "9건");
    }

    [DataTestMethod]
    [DataRow("일반 텍스트 응답입니다", DisplayName = "비 JSON 텍스트")]
    [DataRow("{\"headline\":\"\",\"findings\":[\"유효\"],\"action\":\"실천\"}", DisplayName = "빈 headline")]
    [DataRow("{\"headline\":\"제목\",\"findings\":[],\"action\":\"실천\"}", DisplayName = "빈 findings")]
    [DataRow("{\"headline\":\"제목\",\"findings\":[\"1\",\"2\",\"3\",\"4\"],\"action\":\"실천\"}", DisplayName = "findings 3개 초과")]
    [DataRow("{\"headline\":\"제목\",\"findings\":[\"1\",\" \"],\"action\":\"실천\"}", DisplayName = "빈 finding 원소")]
    [DataRow("{\"headline\":\"제목\",\"findings\":[\"1\"],\"action\":\"\"}", DisplayName = "빈 action")]
    [DataRow("<think>끝나지 않은 사고", DisplayName = "닫히지 않은 think")]
    public async Task GenerateAsync_MalformedJson_FallsBackToRuleResult(string rawResponse)
    {
        var snapshot = CreatePopulatedSnapshot();
        var service = new FocusInsightCoachingService(
            textGenerator: (_, _, _) => Task.FromResult<string?>(rawResponse),
            isAiAvailable: true);

        var result = await service.GenerateAsync(snapshot);

        Assert.IsFalse(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual(2, result.Findings.Count);
    }

    [TestMethod]
    public async Task GenerateAsync_UnavailableModel_DoesNotInvokeGeneratorAndReturnsRuleFallback()
    {
        int calls = 0;
        var snapshot = CreatePopulatedSnapshot();
        var service = new FocusInsightCoachingService(
            textGenerator: (_, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>("{\"headline\":\"x\",\"findings\":[\"y\"],\"action\":\"z\"}");
            },
            isAiAvailable: false);

        Assert.IsFalse(service.IsAiAvailable);

        var result = await service.GenerateAsync(snapshot);

        Assert.AreEqual(0, calls);
        Assert.IsFalse(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual(2, result.Findings.Count);
    }

    [TestMethod]
    public void PublicConstructor_WithMissingModel_ReportsUnavailableAndProvidesRuleCoaching()
    {
        var cachePath = CreateTempCachePath();
        try
        {
            using var engine = new LlamaBriefingEngine("non_existent_model.gguf");
            var service = new FocusInsightCoachingService(engine, cachePath);

            Assert.IsFalse(service.IsAiAvailable);
            var initial = service.GetInitialOrCached(CreatePopulatedSnapshot());
            Assert.IsFalse(initial.IsAiGenerated);
            Assert.IsFalse(initial.IsFromCache);
            Assert.AreEqual(2, initial.Findings.Count);
        }
        finally
        {
            CleanupTempCache(cachePath);
        }
    }

    [TestMethod]
    public async Task EmptySnapshot_NeverCallsGeneratorAndIgnoresStaleCache()
    {
        var cachePath = CreateTempCachePath();
        try
        {
            int calls = 0;
            var populated = CreatePopulatedSnapshot(InsightPeriod.Today, "fp-old");
            var writer = new FocusInsightCoachingService(
                textGenerator: (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult<string?>(
                        "{\"headline\":\"이전 캐시 헤드라인\",\"findings\":[\"이전 발견 1\",\"이전 발견 2\"],\"action\":\"이전 액션\"}");
                },
                isAiAvailable: true,
                cacheFilePath: cachePath);

            var generated = await writer.GenerateAsync(populated);
            Assert.IsTrue(generated.IsAiGenerated);
            Assert.AreEqual(1, calls);

            // DB 초기화 등으로 빈 스냅샷이 들어온 경우 (심지어 같은 fingerprint여도 HasData == false)
            var emptySnapshot = CreateEmptySnapshot(InsightPeriod.Today, "fp-old");
            var reader = new FocusInsightCoachingService(
                textGenerator: (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult<string?>("{\"headline\":\"호출되면 안됨\",\"findings\":[\"x\"],\"action\":\"y\"}");
                },
                isAiAvailable: true,
                cacheFilePath: cachePath);

            var initial = reader.GetInitialOrCached(emptySnapshot);
            var generatedEmpty = await reader.GenerateAsync(emptySnapshot, forceRefresh: true);

            Assert.AreEqual(1, calls, "빈 스냅샷에서는 AI 생성기를 호출하면 안 됩니다.");
            Assert.IsFalse(initial.IsAiGenerated);
            Assert.IsFalse(initial.IsFromCache);
            Assert.AreNotEqual("이전 캐시 헤드라인", initial.Headline);
            Assert.IsFalse(generatedEmpty.IsAiGenerated);
            Assert.IsFalse(generatedEmpty.IsFromCache);
            Assert.AreNotEqual("이전 캐시 헤드라인", generatedEmpty.Headline);
        }
        finally
        {
            CleanupTempCache(cachePath);
        }
    }

    [TestMethod]
    public async Task CachePersistence_HitsAcrossServiceInstancesWhenPeriodAndFingerprintMatch()
    {
        var cachePath = CreateTempCachePath();
        try
        {
            var todaySnapshot = CreatePopulatedSnapshot(InsightPeriod.Today, "fp-today-1");
            var weekSnapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days, "fp-week-1");

            int calls = 0;
            var firstInstance = new FocusInsightCoachingService(
                textGenerator: (_, userPrompt, _) =>
                {
                    calls++;
                    var label = userPrompt.Contains("최근 7일", StringComparison.Ordinal) ? "주간 AI 코칭" : "오늘 AI 코칭";
                    return Task.FromResult<string?>(
                        $"{{\"headline\":\"{label}\",\"findings\":[\"발견 A\",\"발견 B\"],\"action\":\"실천 팁\"}}");
                },
                isAiAvailable: true,
                cacheFilePath: cachePath);

            var todayResult = await firstInstance.GenerateAsync(todaySnapshot);
            var weekResult = await firstInstance.GenerateAsync(weekSnapshot);
            Assert.IsTrue(todayResult.IsAiGenerated);
            Assert.IsFalse(todayResult.IsFromCache);
            Assert.IsTrue(weekResult.IsAiGenerated);
            Assert.AreEqual(2, calls);
            Assert.IsTrue(File.Exists(cachePath));

            // 새 인스턴스를 만들어 디스크 캐시에서 불러오는지 검증
            int secondCalls = 0;
            var secondInstance = new FocusInsightCoachingService(
                textGenerator: (_, _, _) =>
                {
                    secondCalls++;
                    return Task.FromResult<string?>("{\"headline\":\"새 호출\",\"findings\":[\"x\"],\"action\":\"y\"}");
                },
                isAiAvailable: true,
                cacheFilePath: cachePath);

            var cachedInitialToday = secondInstance.GetInitialOrCached(todaySnapshot);
            var cachedGenerateWeek = await secondInstance.GenerateAsync(weekSnapshot, forceRefresh: false);

            Assert.AreEqual(0, secondCalls);
            Assert.IsTrue(cachedInitialToday.IsAiGenerated);
            Assert.IsTrue(cachedInitialToday.IsFromCache);
            Assert.AreEqual("오늘 AI 코칭", cachedInitialToday.Headline);

            Assert.IsTrue(cachedGenerateWeek.IsAiGenerated);
            Assert.IsTrue(cachedGenerateWeek.IsFromCache);
            Assert.AreEqual("주간 AI 코칭", cachedGenerateWeek.Headline);
        }
        finally
        {
            CleanupTempCache(cachePath);
        }
    }

    [TestMethod]
    public async Task CacheInvalidation_FingerprintChangeAndForceRefreshTriggerRegeneration()
    {
        var cachePath = CreateTempCachePath();
        try
        {
            int calls = 0;
            var service = new FocusInsightCoachingService(
                textGenerator: (_, _, _) =>
                {
                    calls++;
                    return Task.FromResult<string?>(
                        $"{{\"headline\":\"응답 #{calls}\",\"findings\":[\"핵심 1\",\"핵심 2\"],\"action\":\"팁 #{calls}\"}}");
                },
                isAiAvailable: true,
                cacheFilePath: cachePath);

            var snapV1 = CreatePopulatedSnapshot(InsightPeriod.Today, "fp-v1");
            var r1 = await service.GenerateAsync(snapV1);
            Assert.AreEqual("응답 #1", r1.Headline);
            Assert.IsFalse(r1.IsFromCache);
            Assert.AreEqual(1, calls);

            // 같은 fingerprint + forceRefresh: false -> 캐시 히트
            var r1Cached = await service.GenerateAsync(snapV1, forceRefresh: false);
            Assert.AreEqual("응답 #1", r1Cached.Headline);
            Assert.IsTrue(r1Cached.IsFromCache);
            Assert.AreEqual(1, calls);

            // 같은 fingerprint + forceRefresh: true -> 재생성 및 캐시 갱신
            var r1Refreshed = await service.GenerateAsync(snapV1, forceRefresh: true);
            Assert.AreEqual("응답 #2", r1Refreshed.Headline);
            Assert.IsFalse(r1Refreshed.IsFromCache);
            Assert.AreEqual(2, calls);

            // 새 세션 추가로 fingerprint 변경 -> GetInitialOrCached는 캐시 미스(규칙 결과), GenerateAsync는 새로 생성
            var snapV2 = CreatePopulatedSnapshot(InsightPeriod.Today, "fp-v2");
            var initialV2 = service.GetInitialOrCached(snapV2);
            Assert.IsFalse(initialV2.IsAiGenerated);
            Assert.IsFalse(initialV2.IsFromCache);

            var r2 = await service.GenerateAsync(snapV2);
            Assert.AreEqual("응답 #3", r2.Headline);
            Assert.IsTrue(r2.IsAiGenerated);
            Assert.IsFalse(r2.IsFromCache);
            Assert.AreEqual("fp-v2", r2.DataFingerprint);
            Assert.AreEqual(3, calls);

            // 새 인스턴스에서도 갱신된 fp-v2 캐시가 유지되는지 확인
            var reloaded = new FocusInsightCoachingService(null, isAiAvailable: false, cacheFilePath: cachePath);
            var reloadedV2 = reloaded.GetInitialOrCached(snapV2);
            Assert.IsTrue(reloadedV2.IsAiGenerated);
            Assert.IsTrue(reloadedV2.IsFromCache);
            Assert.AreEqual("응답 #3", reloadedV2.Headline);
        }
        finally
        {
            CleanupTempCache(cachePath);
        }
    }

    [TestMethod]
    public async Task GenerateAsync_PromptContainsAggregatedMetricsAndTopThreeOnly()
    {
        var snapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days, "fp-prompt");
        string? capturedSystem = null;
        string? capturedUser = null;

        var service = new FocusInsightCoachingService(
            textGenerator: (sys, usr, _) =>
            {
                capturedSystem = sys;
                capturedUser = usr;
                return Task.FromResult<string?>(
                    "{\"headline\":\"요약\",\"findings\":[\"발견 1\",\"발견 2\"],\"action\":\"팁\"}");
            },
            isAiAvailable: true);

        await service.GenerateAsync(snapshot);

        Assert.IsNotNull(capturedSystem);
        Assert.IsNotNull(capturedUser);
        StringAssert.Contains(capturedSystem, "\"headline\"");
        StringAssert.Contains(capturedSystem, "\"findings\"");
        StringAssert.Contains(capturedSystem, "\"action\"");

        StringAssert.Contains(capturedUser, "최근 7일");
        StringAssert.Contains(capturedUser, "95분");
        StringAssert.Contains(capturedUser, "2회");
        StringAssert.Contains(capturedUser, "전체 12건");
        StringAssert.Contains(capturedUser, "차단 9건");
        StringAssert.Contains(capturedUser, "긴급 통과 1건");
        StringAssert.Contains(capturedUser, "연관 통과 2건");
        StringAssert.Contains(capturedUser, "75.0%");
        StringAssert.Contains(capturedUser, "14시");
        StringAssert.Contains(capturedUser, "15시");

        // Top 3 distractors & work contexts included, 4th excluded for compactness
        StringAssert.Contains(capturedUser, "KakaoTalk · 팀 단톡방");
        StringAssert.Contains(capturedUser, "Slack · #general");
        StringAssert.Contains(capturedUser, "Discord");
        Assert.IsFalse(capturedUser.Contains("Mail", StringComparison.Ordinal), "방해원은 상위 3개만 포함해야 합니다.");

        StringAssert.Contains(capturedUser, "VS Code");
        StringAssert.Contains(capturedUser, "Chrome");
        StringAssert.Contains(capturedUser, "Notion");
        Assert.IsFalse(capturedUser.Contains("Figma", StringComparison.Ordinal), "작업 맥락은 상위 3개만 포함해야 합니다.");
    }

    [TestMethod]
    public void BuildUserPrompt_WhenQualityPopulated_IncludesQualityMetrics()
    {
        var snapshot = CreatePopulatedSnapshot(
            InsightPeriod.Last7Days,
            "fp-quality",
            quality: CreateSampleQuality(selfDistractionChangePercent: -24.2));

        var prompt = FocusInsightCoachingService.BuildUserPrompt(snapshot);

        StringAssert.Contains(prompt, "몰입 유지율: 87.5% (차단 8건 중 7건 작업 흐름 유지)");
        StringAssert.Contains(prompt, "집중 순도: 순수 작업 72% · 메신저·웹 21% · 비작업 7%");
        StringAssert.Contains(prompt, "집중 중 직접 확인 최다 앱: KakaoTalk (22분)");
        StringAssert.Contains(prompt, "깊은 몰입(25분+): 3회, 평균 세션: 42분, 연속 집중: 5일");
        StringAssert.Contains(prompt, "전기 대비 집중 변화: +18%");
        StringAssert.Contains(prompt, "자발적 이탈 변화: -24%");
    }

    [TestMethod]
    public void BuildUserPrompt_WhenQualityIsNull_RemainsCleanWithoutQualityLines()
    {
        var snapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days, "fp-null-quality", quality: null);

        var prompt = FocusInsightCoachingService.BuildUserPrompt(snapshot);

        Assert.IsFalse(prompt.Contains("몰입 유지율", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("집중 순도", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("직접 확인 최다 앱", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("깊은 몰입", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("전기 대비", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildUserPrompt_WhenQualitySubMetricsZero_OmitsEmptyLinesAndKeepsDeepFocusLine()
    {
        var zeroQuality = CreateSampleQuality(
            flowEligible: 0,
            flowProtected: 0,
            flowRate: 0.0,
            workDuration: TimeSpan.Zero,
            neutralDuration: TimeSpan.Zero,
            leisureDuration: TimeSpan.Zero,
            workPurity: 0.0,
            neutralPercent: 0.0,
            leisurePercent: 0.0,
            topSelfDistractionApp: null,
            topSelfDistractionDuration: TimeSpan.Zero,
            deepFocusCount: 0,
            avgSessionDuration: TimeSpan.Zero,
            currentStreakDays: 0,
            periodChangePercent: null);
        var snapshot = CreatePopulatedSnapshot(InsightPeriod.Today, "fp-zero-quality", quality: zeroQuality);

        var prompt = FocusInsightCoachingService.BuildUserPrompt(snapshot);

        Assert.IsFalse(prompt.Contains("몰입 유지율", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("집중 순도", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("직접 확인 최다 앱", StringComparison.Ordinal));
        Assert.IsFalse(prompt.Contains("전기 대비", StringComparison.Ordinal));
        StringAssert.Contains(prompt, "깊은 몰입(25분+): 0회, 평균 세션: 0분, 연속 집중: 0일");
    }

    [TestMethod]
    public void GetInitialOrCached_WhenQualityPopulated_SurfacesFlowProtectionWorkPurityAndSelfDistractionTip()
    {
        var snapshot = CreatePopulatedSnapshot(
            InsightPeriod.Last7Days,
            "fp-rule-quality",
            quality: CreateSampleQuality());
        var service = new FocusInsightCoachingService(null, isAiAvailable: false);

        var result = service.GetInitialOrCached(snapshot);

        Assert.IsFalse(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual(2, result.Findings.Count);
        StringAssert.Contains(result.Findings[0], "차단 8건 중 88%");
        StringAssert.Contains(result.Findings[0], "흐름 유지");
        StringAssert.Contains(result.Findings[0], "순수 작업 72%");
        StringAssert.Contains(result.ActionTip, "KakaoTalk");
        StringAssert.Contains(result.ActionTip, "22분");
        StringAssert.Contains(result.ActionTip, "최소화");
    }

    [TestMethod]
    public void GetInitialOrCached_WhenSelfDistractionBelowThresholdOrNoBlocked_SkipsSelfDistractionTip()
    {
        var service = new FocusInsightCoachingService(null, isAiAvailable: false);

        var shortSelfDistraction = CreatePopulatedSnapshot(
            InsightPeriod.Today,
            "fp-short-sd",
            quality: CreateSampleQuality(topSelfDistractionDuration: TimeSpan.FromMinutes(10)));
        var shortResult = service.GetInitialOrCached(shortSelfDistraction);
        Assert.IsFalse(shortResult.ActionTip.Contains("직접 연 시간", StringComparison.Ordinal));

        var noBlocked = CreatePopulatedSnapshot(
            InsightPeriod.Today,
            "fp-no-blocked",
            quality: CreateSampleQuality(topSelfDistractionDuration: TimeSpan.FromMinutes(25)),
            blockedCount: 0);
        var noBlockedResult = service.GetInitialOrCached(noBlocked);
        Assert.IsFalse(noBlockedResult.ActionTip.Contains("직접 연 시간", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetInitialOrCached_WhenQualityIsNull_PreservesBackwardsCompatibleRuleCoaching()
    {
        var snapshot = CreatePopulatedSnapshot(InsightPeriod.Today, "fp-legacy", quality: null);
        var service = new FocusInsightCoachingService(null, isAiAvailable: false);

        var result = service.GetInitialOrCached(snapshot);

        Assert.IsFalse(result.IsAiGenerated);
        Assert.IsFalse(result.IsFromCache);
        Assert.AreEqual(2, result.Findings.Count);
        StringAssert.Contains(result.Findings[0], "골든타임");
        StringAssert.Contains(result.Findings[1], "KakaoTalk · 팀 단톡방");
        Assert.IsFalse(result.ActionTip.Contains("직접 연 시간", StringComparison.Ordinal));
    }
}

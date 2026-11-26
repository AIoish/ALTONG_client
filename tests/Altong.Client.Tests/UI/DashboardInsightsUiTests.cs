using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class DashboardInsightsUiTests
{
    [TestMethod]
    public void EmptySnapshot_ShowsEmptyStateCard_HidesDataRows_AndDisablesAiCoaching() => RunSta(() =>
    {
        foreach (var quality in new InsightQualityMetrics?[] { null, CreateZeroQuality() })
        {
            var repo = new FakeInsightRepository(_ => CreateEmptySnapshot(InsightPeriod.Last7Days, quality));
            var coaching = new FakeCoachingService { IsAiAvailable = true };
            var view = new DashboardInsightsView(repo, coaching);

            Await(view.RefreshAsync());
            Layout(view, new Size(860, 700));

            Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("EmptyStateCard")).Visibility);
            Assert.AreEqual("아직 쌓인 집중 기록이 없어요", ((TextBlock)view.FindName("EmptyStateTitleText")).Text);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)view.FindName("KpiRow")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)view.FindName("QualityTrendRow")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)view.FindName("HourlyChartCard")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)view.FindName("TopListsRow")).Visibility);
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("CoachingCard")).Visibility);

            var aiButton = (Button)view.FindName("GenerateCoachingButton");
            Assert.IsFalse(aiButton.IsEnabled);
            Assert.AreEqual("AI 코칭 분석", aiButton.Content);
            Assert.AreEqual("규칙 요약", ((TextBlock)view.FindName("CoachingStatusBadgeText")).Text);

            // Switching to Distraction sub-tab on empty snapshot should not crash and keeps EmptyStateCard visible
            var distractionTab = (RadioButton)view.FindName("SubTabDistractionButton");
            distractionTab.IsChecked = true;
            Layout(view, new Size(540, 480));
            Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("EmptyStateCard")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((FrameworkElement)view.FindName("OverviewSubTabPanel")).Visibility);
            Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("DistractionSubTabPanel")).Visibility);
        }
    });

    [TestMethod]
    public void PopulatedSnapshot_RendersKpis_Badges_Chart_TopLists_AndInitialRuleCoaching() => RunSta(() =>
    {
        var snapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days);
        var repo = new FakeInsightRepository(_ => snapshot);
        var coaching = new FakeCoachingService { IsAiAvailable = true };
        var view = new DashboardInsightsView(repo, coaching);

        Await(view.RefreshAsync());

        // Default sub-tab: Overview ("몰입 현황")
        var overviewButton = (RadioButton)view.FindName("SubTabOverviewButton");
        var distractionButton = (RadioButton)view.FindName("SubTabDistractionButton");
        var overviewPanel = (FrameworkElement)view.FindName("OverviewSubTabPanel");
        var distractionPanel = (FrameworkElement)view.FindName("DistractionSubTabPanel");
        Assert.AreEqual(true, overviewButton.IsChecked);
        Assert.AreEqual(Visibility.Visible, overviewPanel.Visibility);
        Assert.AreEqual(Visibility.Collapsed, distractionPanel.Visibility);

        foreach (var size in new[] { new Size(860, 700), new Size(540, 480) })
        {
            Layout(view, size);
            var scroll = (ScrollViewer)view.FindName("InsightsScroll");
            Assert.IsTrue(scroll.ScrollableWidth <= 1, $"몰입 현황 탭 가로 잘림이 없어야 합니다: {size.Width}");
        }

        Assert.AreEqual(Visibility.Collapsed, ((Border)view.FindName("EmptyStateCard")).Visibility);
        Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("KpiRow")).Visibility);
        Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("QualityTrendRow")).Visibility);
        Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("HourlyChartCard")).Visibility);
        Assert.AreEqual(Visibility.Visible, ((FrameworkElement)view.FindName("TopListsRow")).Visibility);

        // 4 KPI Cards
        Assert.AreEqual("3시간 25분", ((TextBlock)view.FindName("KpiTotalFocusText")).Text);
        Assert.AreEqual("세션 5회", ((TextBlock)view.FindName("KpiSessionCountText")).Text);
        Assert.AreEqual("82%", ((TextBlock)view.FindName("KpiBlockRateText")).Text);
        Assert.AreEqual("차단 28 / 전체 34건", ((TextBlock)view.FindName("KpiBlockRateSubText")).Text);
        Assert.AreEqual("4건", ((TextBlock)view.FindName("KpiUrgentPassedText")).Text);
        Assert.AreEqual("2건", ((TextBlock)view.FindName("KpiRelevancePassedText")).Text);

        // Quality & 7-Day Trend/Streak Row
        Assert.AreEqual("93.5%", ((TextBlock)view.FindName("FlowProtectionRateText")).Text);
        Assert.AreEqual("차단 1,519건 중 1,421건 흐름 유지", ((TextBlock)view.FindName("FlowProtectionSubText")).Text);
        var purityBar = (Grid)view.FindName("WorkPurityBarGrid");
        Assert.AreEqual(GridUnitType.Star, purityBar.ColumnDefinitions[0].Width.GridUnitType);
        Assert.AreEqual(68.0, purityBar.ColumnDefinitions[0].Width.Value, 0.01);
        Assert.AreEqual(29.0, purityBar.ColumnDefinitions[1].Width.Value, 0.01);
        Assert.AreEqual(3.0, purityBar.ColumnDefinitions[2].Width.Value, 0.01);
        Assert.AreEqual("순수 작업 68% · 메신저·웹 29% · 비작업 3%", ((TextBlock)view.FindName("WorkPurityLegendText")).Text);
        Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("SelfDistractionCallout")).Visibility);
        Assert.AreEqual("직접 확인 최다 · KakaoTalk (28시간 17분)", ((TextBlock)view.FindName("SelfDistractionText")).Text);
        Assert.AreEqual("깊은 몰입(25분+) 31회 · 평균 세션 37분", ((TextBlock)view.FindName("DeepFocusSummaryText")).Text);

        Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("StreakBadge")).Visibility);
        Assert.AreEqual("5일 연속 집중", ((TextBlock)view.FindName("StreakBadgeText")).Text);
        Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("BestStreakBadge")).Visibility);
        Assert.AreEqual("최장 12일", ((TextBlock)view.FindName("BestStreakBadgeText")).Text);
        Assert.AreEqual("🔥 5일 연속 집중 중", ((TextBlock)view.FindName("StreakHeroTitleText")).Text);
        StringAssert.Contains(((TextBlock)view.FindName("StreakMotivationText")).Text, "오늘 잔디를 심었어요");
        Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("PeriodChangeBadge")).Visibility);
        Assert.AreEqual("전기 대비 +18%", ((TextBlock)view.FindName("PeriodChangeBadgeText")).Text);
        var heatmapItems = (ItemsControl)view.FindName("StreakHeatmapItems");
        Assert.AreEqual(70, heatmapItems.Items.Count);

        // Initial Rule Coaching
        Assert.AreEqual("규칙 요약", ((TextBlock)view.FindName("CoachingStatusBadgeText")).Text);
        Assert.AreEqual("규칙 기반 집중 요약 헤드라인", ((TextBlock)view.FindName("CoachingHeadlineText")).Text);
        Assert.AreEqual(2, ((ItemsControl)view.FindName("CoachingFindingsItems")).Items.Count);
        Assert.AreEqual("오전 10시에 핵심 업무를 배치해 보세요.", ((TextBlock)view.FindName("CoachingActionTipText")).Text);

        var aiButton = (Button)view.FindName("GenerateCoachingButton");
        Assert.IsTrue(aiButton.IsEnabled);
        Assert.AreEqual("AI 코칭 분석", aiButton.Content);

        // Switch to sub-tab 2: Distraction & Context ("방해 · 맥락")
        distractionButton.IsChecked = true;
        Assert.AreEqual(Visibility.Collapsed, overviewPanel.Visibility);
        Assert.AreEqual(Visibility.Visible, distractionPanel.Visibility);

        foreach (var size in new[] { new Size(860, 700), new Size(540, 480) })
        {
            Layout(view, size);
            var scroll = (ScrollViewer)view.FindName("InsightsScroll");
            Assert.IsTrue(scroll.ScrollableWidth <= 1, $"방해 · 맥락 탭 가로 잘림이 없어야 합니다: {size.Width}");
        }

        // Golden Hour & Distraction Peak Badges
        Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("GoldenHourBadge")).Visibility);
        Assert.AreEqual("골든타임 10시", ((TextBlock)view.FindName("GoldenHourBadgeText")).Text);
        Assert.AreEqual(Visibility.Visible, ((Border)view.FindName("DistractionPeakBadge")).Visibility);
        Assert.AreEqual("방해 피크 15시", ((TextBlock)view.FindName("DistractionPeakBadgeText")).Text);

        // 24-hour chart columns
        var hourlyItems = (ItemsControl)view.FindName("HourlyChartItems");
        Assert.AreEqual(24, hourlyItems.Items.Count);

        // Top 5 Distractors & Work Contexts
        var distractors = (ItemsControl)view.FindName("TopDistractorsItems");
        Assert.AreEqual(2, distractors.Items.Count);
        Assert.AreEqual(Visibility.Collapsed, ((TextBlock)view.FindName("TopDistractorsEmptyText")).Visibility);

        var contexts = (ItemsControl)view.FindName("TopWorkContextsItems");
        Assert.AreEqual(2, contexts.Items.Count);
        Assert.AreEqual(Visibility.Collapsed, ((TextBlock)view.FindName("TopWorkContextsEmptyText")).Visibility);

        // Switch back to Overview sub-tab
        overviewButton.IsChecked = true;
        Assert.AreEqual(Visibility.Visible, overviewPanel.Visibility);
        Assert.AreEqual(Visibility.Collapsed, distractionPanel.Visibility);
    });

    [TestMethod]
    public void QualityMetrics_NullOrEdgeValues_RenderSafeFallbacksWithoutCrashing() => RunSta(() =>
    {
        // Case 1: Populated snapshot with Quality = null
        var nullQualitySnapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days) with { Quality = null };
        var repo = new FakeInsightRepository(_ => nullQualitySnapshot);
        var view = new DashboardInsightsView(repo, new FakeCoachingService { IsAiAvailable = true });

        Await(view.RefreshAsync());
        Layout(view, new Size(860, 700));

        Assert.AreEqual("-", ((TextBlock)view.FindName("FlowProtectionRateText")).Text);
        Assert.AreEqual("차단 알림 매칭 기록 없음", ((TextBlock)view.FindName("FlowProtectionSubText")).Text);
        Assert.AreEqual("순수 작업 0% · 메신저·웹 0% · 비작업 0%", ((TextBlock)view.FindName("WorkPurityLegendText")).Text);
        Assert.AreEqual(Visibility.Collapsed, ((Border)view.FindName("SelfDistractionCallout")).Visibility);
        Assert.AreEqual("깊은 몰입(25분+) 0회 · 평균 세션 0분", ((TextBlock)view.FindName("DeepFocusSummaryText")).Text);
        Assert.AreEqual(Visibility.Collapsed, ((Border)view.FindName("StreakBadge")).Visibility);
        Assert.AreEqual(Visibility.Collapsed, ((Border)view.FindName("BestStreakBadge")).Visibility);
        Assert.AreEqual("🌱 연속 집중을 시작해 보세요", ((TextBlock)view.FindName("StreakHeroTitleText")).Text);
        Assert.AreEqual(Visibility.Collapsed, ((Border)view.FindName("PeriodChangeBadge")).Visibility);
        Assert.AreEqual(70, ((ItemsControl)view.FindName("StreakHeatmapItems")).Items.Count);

        // Case 2: Negative period change, zero streak, null self-distraction, zero flow eligible
        var negativeChangeQuality = CreateZeroQuality() with { PeriodChangePercent = -12.4, BestStreakDays = 4 };
        var negativeSnapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days) with
        {
            DataFingerprint = "fp-negative-change",
            Quality = negativeChangeQuality
        };
        var repo2 = new FakeInsightRepository(_ => negativeSnapshot);
        var view2 = new DashboardInsightsView(repo2, new FakeCoachingService { IsAiAvailable = true });

        Await(view2.RefreshAsync());
        Layout(view2, new Size(860, 700));

        Assert.AreEqual("-", ((TextBlock)view2.FindName("FlowProtectionRateText")).Text);
        Assert.AreEqual(Visibility.Collapsed, ((Border)view2.FindName("SelfDistractionCallout")).Visibility);
        Assert.AreEqual(Visibility.Collapsed, ((Border)view2.FindName("StreakBadge")).Visibility);
        Assert.AreEqual(Visibility.Visible, ((Border)view2.FindName("BestStreakBadge")).Visibility);
        Assert.AreEqual("최장 4일", ((TextBlock)view2.FindName("BestStreakBadgeText")).Text);
        StringAssert.Contains(((TextBlock)view2.FindName("StreakMotivationText")).Text, "최장 4일 연속 기록이 있어요");
        Assert.AreEqual(Visibility.Visible, ((Border)view2.FindName("PeriodChangeBadge")).Visibility);
        Assert.AreEqual("전기 대비 -12%", ((TextBlock)view2.FindName("PeriodChangeBadgeText")).Text);

        // Case 3: Duolingo urgency nudge (active streak from yesterday, not focused today yet)
        var urgencyQuality = CreatePopulatedQuality() with
        {
            CurrentStreakDays = 4,
            BestStreakDays = 9,
            IsTodayFocused = false
        };
        var urgencySnapshot = CreatePopulatedSnapshot(InsightPeriod.Today) with
        {
            DataFingerprint = "fp-urgency",
            Quality = urgencyQuality
        };
        var repo3 = new FakeInsightRepository(_ => urgencySnapshot);
        var view3 = new DashboardInsightsView(repo3, new FakeCoachingService { IsAiAvailable = true });

        Await(view3.RefreshAsync());
        Layout(view3, new Size(860, 700));

        Assert.AreEqual("⚡ 4일 연속 기록 유지 중", ((TextBlock)view3.FindName("StreakHeroTitleText")).Text);
        StringAssert.Contains(((TextBlock)view3.FindName("StreakMotivationText")).Text, "오늘 한 번만 집중하면 5일 연속으로 이어져요");
    });

    [TestMethod]
    public void PeriodButtons_QueryRepositoryWithMatchingInsightPeriod() => RunSta(() =>
    {
        var requestedPeriods = new List<InsightPeriod>();
        var repo = new FakeInsightRepository(period =>
        {
            requestedPeriods.Add(period);
            return CreatePopulatedSnapshot(period);
        });
        var coaching = new FakeCoachingService { IsAiAvailable = true };
        var view = new DashboardInsightsView(repo, coaching);

        Await(view.RefreshAsync());
        Assert.AreEqual(InsightPeriod.Last7Days, view.SelectedPeriod);
        Assert.AreEqual(InsightPeriod.Last7Days, requestedPeriods.Last());

        var todayButton = (RadioButton)view.FindName("PeriodTodayButton");
        todayButton.IsChecked = true;
        Until(() => requestedPeriods.Contains(InsightPeriod.Today) &&
            ((TextBlock)view.FindName("SectionSubtitleText")).Text.Contains("오늘"));
        Assert.AreEqual(InsightPeriod.Today, view.SelectedPeriod);
        StringAssert.Contains(((TextBlock)view.FindName("SectionSubtitleText")).Text, "오늘");

        var allTimeButton = (RadioButton)view.FindName("PeriodAllTimeButton");
        allTimeButton.IsChecked = true;
        Until(() => requestedPeriods.Contains(InsightPeriod.AllTime) &&
            ((TextBlock)view.FindName("SectionSubtitleText")).Text.Contains("전체 기간"));
        Assert.AreEqual(InsightPeriod.AllTime, view.SelectedPeriod);
        StringAssert.Contains(((TextBlock)view.FindName("SectionSubtitleText")).Text, "전체 기간");

        var last7DaysButton = (RadioButton)view.FindName("PeriodLast7DaysButton");
        last7DaysButton.IsChecked = true;
        Until(() => requestedPeriods.Count >= 4 && requestedPeriods.Last() == InsightPeriod.Last7Days &&
            ((TextBlock)view.FindName("SectionSubtitleText")).Text.Contains("최근 7일"));
        Assert.AreEqual(InsightPeriod.Last7Days, view.SelectedPeriod);
    });

    [TestMethod]
    public void AiCoachingButton_InvokesGenerateAsync_AndUpdatesUiState() => RunSta(() =>
    {
        var snapshot = CreatePopulatedSnapshot(InsightPeriod.Last7Days);
        var repo = new FakeInsightRepository(_ => snapshot);
        var pending = new TaskCompletionSource<InsightCoachingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coaching = new FakeCoachingService
        {
            IsAiAvailable = true,
            GenerateHandler = (_, forceRefresh) =>
            {
                if (!forceRefresh) return pending.Task;
                return Task.FromResult(new InsightCoachingResult(
                    Headline: "재분석된 AI 코칭 헤드라인",
                    Findings: ["재분석 인사이트 1", "재분석 인사이트 2"],
                    ActionTip: "오후 3시 알림 차단을 유지하세요.",
                    IsAiGenerated: true,
                    IsFromCache: false,
                    GeneratedAtUtc: DateTime.UtcNow,
                    DataFingerprint: snapshot.DataFingerprint));
            }
        };
        var view = new DashboardInsightsView(repo, coaching);
        Await(view.RefreshAsync());

        var aiButton = (Button)view.FindName("GenerateCoachingButton");
        var loadingBanner = (Border)view.FindName("CoachingLoadingBanner");
        var badgeText = (TextBlock)view.FindName("CoachingStatusBadgeText");
        Assert.AreEqual(Visibility.Collapsed, loadingBanner.Visibility);

        aiButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // While running, button should be disabled and inline loading banner + badge should be visible
        Until(() => !aiButton.IsEnabled &&
            Equals(aiButton.Content, "Gemma 분석 중…") &&
            loadingBanner.Visibility == Visibility.Visible &&
            badgeText.Text == "분석 중…");

        pending.SetResult(new InsightCoachingResult(
            Headline: "Gemma AI 맞춤 집중 코칭",
            Findings: ["오전 10시 몰입도가 가장 높았습니다.", "카카오톡 알림이 방해의 65%를 차지했습니다."],
            ActionTip: "10시~12시 사이에는 메신저 알림을 완전 차단하세요.",
            IsAiGenerated: true,
            IsFromCache: false,
            GeneratedAtUtc: DateTime.UtcNow,
            DataFingerprint: snapshot.DataFingerprint));

        Until(() => aiButton.IsEnabled && Equals(aiButton.Content, "다시 분석"));
        Assert.AreEqual(Visibility.Collapsed, loadingBanner.Visibility);
        Assert.AreEqual("AI 코칭", badgeText.Text);
        Assert.AreEqual("Gemma AI 맞춤 집중 코칭", ((TextBlock)view.FindName("CoachingHeadlineText")).Text);
        Assert.AreEqual("10시~12시 사이에는 메신저 알림을 완전 차단하세요.", ((TextBlock)view.FindName("CoachingActionTipText")).Text);
        Assert.AreEqual(1, coaching.GenerateCallCount);
        Assert.IsFalse(coaching.LastForceRefresh);

        // Clicking again passes forceRefresh = true
        aiButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => coaching.GenerateCallCount == 2 && aiButton.IsEnabled);
        Assert.IsTrue(coaching.LastForceRefresh);
        Assert.AreEqual("재분석된 AI 코칭 헤드라인", ((TextBlock)view.FindName("CoachingHeadlineText")).Text);
    });

    [TestMethod]
    public void PeriodSwitching_WhileRepositoryPending_ShowsAndClearsPeriodLoadingBadge() => RunSta(() =>
    {
        var todayPending = new TaskCompletionSource<FocusInsightSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = new DeferredInsightRepository(period =>
            period == InsightPeriod.Today
                ? todayPending.Task
                : Task.FromResult(CreatePopulatedSnapshot(period)));
        var coaching = new FakeCoachingService { IsAiAvailable = true };
        var view = new DashboardInsightsView(repo, coaching);

        Await(view.RefreshAsync());
        var badge = (Border)view.FindName("PeriodLoadingBadge");
        var kpiRow = (FrameworkElement)view.FindName("KpiRow");
        var qualityTrendRow = (FrameworkElement)view.FindName("QualityTrendRow");
        Assert.AreEqual(Visibility.Collapsed, badge.Visibility);
        Assert.AreEqual(1.0, kpiRow.Opacity, 0.01);
        Assert.AreEqual(1.0, qualityTrendRow.Opacity, 0.01);

        var todayButton = (RadioButton)view.FindName("PeriodTodayButton");
        todayButton.IsChecked = true;

        Until(() => badge.Visibility == Visibility.Visible &&
            kpiRow.Opacity < 0.8 &&
            qualityTrendRow.Opacity < 0.8);

        todayPending.SetResult(CreatePopulatedSnapshot(InsightPeriod.Today));

        Until(() => badge.Visibility == Visibility.Collapsed &&
            Math.Abs(kpiRow.Opacity - 1.0) < 0.01 &&
            Math.Abs(qualityTrendRow.Opacity - 1.0) < 0.01 &&
            ((TextBlock)view.FindName("SectionSubtitleText")).Text.Contains("오늘"));
    });

    [TestMethod]
    public void DashboardWindow_NavInsightsButton_SelectsTab5_WithoutBreakingTabs0To4() => RunSta(() =>
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var results = new SessionResultsService(db, new SqliteFocusSessionRepository(db));
        var settings = new FocusSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var focus = new FocusModeService();
        using var tracker = new StubTracker();
        var repo = new FakeInsightRepository(CreatePopulatedSnapshot);
        var coaching = new FakeCoachingService { IsAiAvailable = true };

        var window = new DashboardWindow(
            settings,
            new FocusRoutineService(focus, settings),
            results,
            tracker,
            focusModeService: focus,
            insightRepository: repo,
            insightCoaching: coaching);
        try
        {
            var tabs = (TabControl)window.FindName("DashboardTabs");
            Assert.AreEqual(6, tabs.Items.Count);
            Assert.AreEqual("홈", ((TabItem)tabs.Items[0]).Header);
            Assert.AreEqual("알림 확인", ((TabItem)tabs.Items[1]).Header);
            Assert.AreEqual("활동 일지", ((TabItem)tabs.Items[2]).Header);
            Assert.AreEqual("설정", ((TabItem)tabs.Items[3]).Header);
            Assert.AreEqual("캘린더", ((TabItem)tabs.Items[4]).Header);
            Assert.AreEqual("인사이트", ((TabItem)tabs.Items[5]).Header);

            var navButton = (Button)window.FindName("NavInsightsButton");
            Assert.IsNotNull(navButton);
            Assert.AreEqual("5", navButton.Tag);
            Assert.AreEqual("집중 인사이트", AutomationProperties.GetName(navButton));

            navButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(5, tabs.SelectedIndex);
            Assert.AreEqual("집중 인사이트", ((TextBlock)window.FindName("PageTitleText")).Text);
            Assert.AreEqual("선택됨", AutomationProperties.GetItemStatus(navButton));
            Assert.AreEqual(Visibility.Visible, ((Button)window.FindName("BackButton")).Visibility);

            var insightsHost = (ContentControl)window.FindName("InsightsHost");
            Assert.IsInstanceOfType<DashboardInsightsView>(insightsHost.Content);
            Until(() => repo.CallCount > 0);

            // Tabs 0..4 still work identically
            ((Button)window.FindName("NavCalendarButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(4, tabs.SelectedIndex);
            Assert.AreEqual("캘린더", ((TextBlock)window.FindName("PageTitleText")).Text);

            ((Button)window.FindName("NavHomeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(0, tabs.SelectedIndex);
            Assert.AreEqual("메인 화면", ((TextBlock)window.FindName("PageTitleText")).Text);
        }
        finally { window.Close(); }
    });

    private static FocusInsightSnapshot CreateEmptySnapshot(InsightPeriod period, InsightQualityMetrics? quality = null)
    {
        var buckets = Enumerable.Range(0, 24)
            .Select(h => new InsightHourlyBucket(h, TimeSpan.Zero, 0, 0, 0, 0, false, false, $"{h}시 · 기록 없음"))
            .ToArray();
        return new FocusInsightSnapshot(
            period, DateTime.UtcNow, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, 0,
            null, TimeSpan.Zero, null, 0, buckets, [], [], $"empty-{period}", "기록 없음", quality);
    }

    private static InsightQualityMetrics CreateZeroQuality()
    {
        string[] labels = ["금", "토", "일", "월", "화", "수", "목"];
        var today = DateTime.Today;
        var trends = Enumerable.Range(0, 7)
            .Select(i => new InsightDailyTrendItem(
                LocalDate: today.AddDays(-6 + i),
                DayLabel: labels[i],
                FocusDuration: TimeSpan.Zero,
                BarHeight: 0,
                IsToday: i == 6,
                TooltipText: $"{today.AddDays(-6 + i):M/d} ({labels[i]}) · 집중 0분"))
            .ToArray();
        return new InsightQualityMetrics(
            FlowProtectionEligibleCount: 0,
            FlowProtectedCount: 0,
            FlowProtectionRatePercent: 0,
            WorkDuration: TimeSpan.Zero,
            NeutralDuration: TimeSpan.Zero,
            LeisureDuration: TimeSpan.Zero,
            WorkPurityPercent: 0,
            NeutralPercent: 0,
            LeisurePercent: 0,
            TopSelfDistractionApp: null,
            TopSelfDistractionDuration: TimeSpan.Zero,
            DeepFocusSessionCount: 0,
            AverageSessionDuration: TimeSpan.Zero,
            CurrentStreakDays: 0,
            PeriodChangePercent: null,
            DailyTrends: trends,
            BestStreakDays: 0,
            IsTodayFocused: false,
            HeatmapDays: BuildTestHeatmap(today, active: false));
    }

    private static InsightQualityMetrics CreatePopulatedQuality()
    {
        string[] labels = ["금", "토", "일", "월", "화", "수", "목"];
        int[] minutes = [45, 0, 60, 90, 75, 110, 120];
        var today = DateTime.Today;
        var trends = Enumerable.Range(0, 7)
            .Select(i => new InsightDailyTrendItem(
                LocalDate: today.AddDays(-6 + i),
                DayLabel: labels[i],
                FocusDuration: TimeSpan.FromMinutes(minutes[i]),
                BarHeight: minutes[i] / 120.0 * 72.0,
                IsToday: i == 6,
                TooltipText: $"{today.AddDays(-6 + i):M/d} ({labels[i]}) · 집중 {minutes[i]}분"))
            .ToArray();
        return new InsightQualityMetrics(
            FlowProtectionEligibleCount: 1519,
            FlowProtectedCount: 1421,
            FlowProtectionRatePercent: 93.548,
            WorkDuration: TimeSpan.FromHours(68),
            NeutralDuration: TimeSpan.FromHours(29),
            LeisureDuration: TimeSpan.FromHours(3),
            WorkPurityPercent: 68.0,
            NeutralPercent: 29.0,
            LeisurePercent: 3.0,
            TopSelfDistractionApp: "KakaoTalk",
            TopSelfDistractionDuration: TimeSpan.FromHours(28) + TimeSpan.FromMinutes(17),
            DeepFocusSessionCount: 31,
            AverageSessionDuration: TimeSpan.FromMinutes(37),
            CurrentStreakDays: 5,
            PeriodChangePercent: 18.2,
            DailyTrends: trends,
            BestStreakDays: 12,
            IsTodayFocused: true,
            HeatmapDays: BuildTestHeatmap(today, active: true));
    }

    private static IReadOnlyList<InsightHeatmapDay> BuildTestHeatmap(DateTime today, bool active)
    {
        int dow = (int)today.DayOfWeek;
        var start = today.AddDays(-dow - 9 * 7);
        var list = new InsightHeatmapDay[70];
        for (int i = 0; i < 70; i++)
        {
            var date = start.AddDays(i);
            bool isFuture = date > today;
            bool isToday = date == today;
            int level = !active || isFuture ? 0 : ((i % 4) + 1);
            var dur = level == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(level * 30);
            list[i] = new InsightHeatmapDay(
                LocalDate: date,
                FocusDuration: dur,
                IntensityLevel: level,
                IsToday: isToday,
                IsFuture: isFuture,
                TooltipText: isFuture ? string.Empty : $"{date:M월 d일} · 집중 {(int)dur.TotalMinutes}분");
        }
        return list;
    }

    private static FocusInsightSnapshot CreatePopulatedSnapshot(InsightPeriod period)
    {
        var buckets = Enumerable.Range(0, 24)
            .Select(h => new InsightHourlyBucket(
                Hour: h,
                FocusDuration: h == 10 ? TimeSpan.FromMinutes(95) : h == 14 ? TimeSpan.FromMinutes(55) : TimeSpan.Zero,
                BlockedCount: h == 15 ? 12 : h == 10 ? 4 : 0,
                PassedCount: h == 15 ? 2 : 0,
                FocusBarHeight: h == 10 ? 88 : h == 14 ? 50 : 0,
                NotificationBarHeight: h == 15 ? 88 : h == 10 ? 25 : 0,
                IsGoldenHour: h == 10,
                IsDistractionPeak: h == 15,
                TooltipText: $"{h}시 · 집중 {(h == 10 ? 95 : 0)}분 · 차단 {(h == 15 ? 12 : 0)}건"))
            .ToArray();

        return new FocusInsightSnapshot(
            Period: period,
            GeneratedAtUtc: DateTime.UtcNow,
            TotalFocusDuration: TimeSpan.FromMinutes(205), // 3시간 25분
            FocusSessionCount: 5,
            TotalNotifications: 34,
            BlockedCount: 28,
            PassedCount: 6,
            UrgentPassedCount: 4,
            RelevancePassedCount: 2,
            OtherPassedCount: 0,
            BlockRatePercent: 82.35,
            GoldenHour: 10,
            GoldenHourDuration: TimeSpan.FromMinutes(95),
            DistractionPeakHour: 15,
            DistractionPeakCount: 14,
            HourlyBuckets: buckets,
            TopDistractors:
            [
                new InsightDistractorItem("KakaoTalk · 팀 단톡방", "KakaoTalk", "팀 단톡방", 18, 16, 2, 88.9),
                new InsightDistractorItem("Slack · #general", "Slack", "#general", 10, 8, 2, 80.0)
            ],
            TopWorkContexts:
            [
                new InsightWorkContextItem("Code.exe", "VS Code", TimeSpan.FromMinutes(140), 20, 17, 3, 85.0),
                new InsightWorkContextItem("chrome.exe", "Chrome", TimeSpan.FromMinutes(65), 14, 11, 3, 78.6)
            ],
            DataFingerprint: $"fp-{period}-populated",
            RuleSummaryLine: "최근 7일간 총 3시간 25분 집중했습니다.",
            Quality: CreatePopulatedQuality());
    }

    private sealed class FakeInsightRepository(Func<InsightPeriod, FocusInsightSnapshot> factory) : IFocusInsightRepository
    {
        public int CallCount { get; private set; }

        public Task<FocusInsightSnapshot> ReadAsync(
            InsightPeriod period,
            DateTime nowUtc,
            FocusSessionRecord? activeSession = null,
            TimeZoneInfo? timeZone = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(factory(period));
        }
    }

    private sealed class DeferredInsightRepository(Func<InsightPeriod, Task<FocusInsightSnapshot>> factory) : IFocusInsightRepository
    {
        public Task<FocusInsightSnapshot> ReadAsync(
            InsightPeriod period,
            DateTime nowUtc,
            FocusSessionRecord? activeSession = null,
            TimeZoneInfo? timeZone = null,
            CancellationToken ct = default) => factory(period);
    }

    private sealed class FakeCoachingService : IFocusInsightCoachingService
    {
        public bool IsAiAvailable { get; set; }
        public int GenerateCallCount { get; private set; }
        public bool LastForceRefresh { get; private set; }
        public Func<FocusInsightSnapshot, bool, Task<InsightCoachingResult>>? GenerateHandler { get; set; }

        public InsightCoachingResult GetInitialOrCached(FocusInsightSnapshot snapshot) =>
            new(
                Headline: snapshot.HasData ? "규칙 기반 집중 요약 헤드라인" : "집중 기록이 아직 없어요.",
                Findings: snapshot.HasData
                    ? ["골든타임은 10시입니다.", "방해 피크는 15시입니다."]
                    : ["집중 모드를 완료하면 분석이 시작됩니다."],
                ActionTip: "오전 10시에 핵심 업무를 배치해 보세요.",
                IsAiGenerated: false,
                IsFromCache: false,
                GeneratedAtUtc: DateTime.UtcNow,
                DataFingerprint: snapshot.DataFingerprint);

        public Task<InsightCoachingResult> GenerateAsync(
            FocusInsightSnapshot snapshot,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            GenerateCallCount++;
            LastForceRefresh = forceRefresh;
            if (GenerateHandler is not null)
                return GenerateHandler(snapshot, forceRefresh);

            return Task.FromResult(new InsightCoachingResult(
                Headline: "AI 생성 코칭",
                Findings: ["AI 인사이트 1", "AI 인사이트 2"],
                ActionTip: "AI 실천 팁",
                IsAiGenerated: true,
                IsFromCache: false,
                GeneratedAtUtc: DateTime.UtcNow,
                DataFingerprint: snapshot.DataFingerprint));
        }
    }

    private sealed class StubTracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static void Await(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Until(Func<bool> completed)
    {
        if (completed()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.IsTrue(completed(), "인사이트 UI 작업 대기 시간 초과");
    }

    private static void Layout(FrameworkElement view, Size size)
    {
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "WPF 인사이트 테스트 시간 초과");
        if (error is not null) throw new AssertFailedException(error.ToString(), error);
    }
}

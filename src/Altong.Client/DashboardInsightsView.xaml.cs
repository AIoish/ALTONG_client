using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services;
using Brush = System.Windows.Media.Brush;

namespace Altong.Client;

public partial class DashboardInsightsView : System.Windows.Controls.UserControl
{
    private const int HeatmapWeeks = 20;
    private const int HeatmapDaysCount = HeatmapWeeks * 7;
    private static readonly string[] KoreanDayLabels = ["일", "월", "화", "수", "목", "금", "토"];

    private readonly IFocusInsightRepository? _repository;
    private readonly IFocusInsightCoachingService? _coaching;
    private readonly Func<FocusSessionRecord?>? _activeSessionProvider;

    private InsightPeriod _selectedPeriod = InsightPeriod.Last7Days;
    private FocusInsightSnapshot? _snapshot;
    private InsightCoachingResult? _currentCoaching;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _coachingCts;
    private int _refreshRevision;
    private int _coachingRevision;
    private bool _ready;
    private bool _isGeneratingCoaching;
    private InsightPeriod _coachingPeriod;
    private string? _coachingFingerprint;

    public DashboardInsightsView(
        IFocusInsightRepository? repository = null,
        IFocusInsightCoachingService? coaching = null,
        Func<FocusSessionRecord?>? activeSessionProvider = null)
    {
        _repository = repository;
        _coaching = coaching;
        _activeSessionProvider = activeSessionProvider;
        InitializeComponent();
        StreakHeatmapItems.Resources["HeatmapCellHeight"] = 14d; // 크기를 재기 전 기본값
        _ready = true;

        ApplySnapshot(CreateEmptySnapshot(_selectedPeriod));

        Loaded += (_, _) =>
        {
            if (_snapshot is null || _repository is not null)
                _ = RefreshAsync();
        };
        Unloaded += (_, _) => CancelPending();
    }

    public InsightPeriod SelectedPeriod => _selectedPeriod;

    public void CancelPending()
    {
        _refreshRevision++;
        _coachingRevision++;
        CancelAndDispose(ref _refreshCts);
        CancelAndDispose(ref _coachingCts);
        _isGeneratingCoaching = false;
        _coachingFingerprint = null;
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished && Dispatcher.CheckAccess())
        {
            SetRefreshingState(false);
            UpdateCoachingButtonState();
        }
    }

    public Task RefreshAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return Task.CompletedTask;
            return Dispatcher.InvokeAsync(RefreshAsync).Task.Unwrap();
        }
        return RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        var period = _selectedPeriod;
        int revision = ++_refreshRevision;
        CancelAndDispose(ref _refreshCts);
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var ct = cts.Token;

        StatusErrorText.Visibility = Visibility.Collapsed;
        StatusErrorText.Text = "";

        if (_repository is null)
        {
            var empty = CreateEmptySnapshot(period);
            if (revision == _refreshRevision && !ct.IsCancellationRequested)
                ApplySnapshot(empty);
            SetRefreshingState(false);
            return;
        }

        SetRefreshingState(true);

        FocusSessionRecord? activeSession = null;
        try { activeSession = _activeSessionProvider?.Invoke(); }
        catch { /* ignore provider race */ }

        try
        {
            var snapshot = await Task.Run(
                () => _repository.ReadAsync(period, DateTime.UtcNow, activeSession, null, ct),
                ct);
            if (revision != _refreshRevision || ct.IsCancellationRequested ||
                Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh or unloaded view.
        }
        catch (Exception)
        {
            if (revision != _refreshRevision || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            StatusErrorText.Text = "집중 데이터를 불러오지 못했습니다. 다시 시도하세요.";
            StatusErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (revision == _refreshRevision && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                SetRefreshingState(false);
        }
    }

    public Task GenerateCoachingAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return Task.CompletedTask;
            return Dispatcher.InvokeAsync(GenerateCoachingAsync).Task.Unwrap();
        }
        return GenerateCoachingCoreAsync();
    }

    private async Task GenerateCoachingCoreAsync()
    {
        if (_isGeneratingCoaching || _snapshot is not { HasData: true } snapshot ||
            _coaching is null || !_coaching.IsAiAvailable)
            return;

        bool forceRefresh = _currentCoaching is { IsAiGenerated: true };
        int revision = ++_coachingRevision;
        CancelAndDispose(ref _coachingCts);
        var cts = new CancellationTokenSource();
        _coachingCts = cts;
        var ct = cts.Token;

        _isGeneratingCoaching = true;
        _coachingPeriod = snapshot.Period;
        _coachingFingerprint = snapshot.DataFingerprint;
        CoachingErrorText.Visibility = Visibility.Collapsed;
        CoachingErrorText.Text = "";
        UpdateCoachingButtonState();

        try
        {
            var result = await Task.Run(
                () => _coaching.GenerateAsync(snapshot, forceRefresh, ct),
                ct);
            if (revision != _coachingRevision || ct.IsCancellationRequested ||
                Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            if (_snapshot is null || _snapshot.Period != snapshot.Period ||
                !string.Equals(_snapshot.DataFingerprint, snapshot.DataFingerprint, StringComparison.Ordinal))
                return;

            ApplyCoachingResult(result);
            MotionBehavior.Acknowledge(CoachingHeadlineText);
        }
        catch (OperationCanceledException)
        {
            // Superseded by period switch or view unload.
        }
        catch (Exception)
        {
            if (revision != _coachingRevision || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            CoachingErrorText.Text = "AI 분석 실패. 다시 시도하세요.";
            CoachingErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (revision == _coachingRevision && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _isGeneratingCoaching = false;
                _coachingFingerprint = null;
                UpdateCoachingButtonState();
            }
        }
    }

    private void ApplySnapshot(FocusInsightSnapshot snapshot)
    {
        bool periodOrDataChanged = _snapshot is null ||
            _snapshot.Period != snapshot.Period ||
            !string.Equals(_snapshot.DataFingerprint, snapshot.DataFingerprint, StringComparison.Ordinal);

        _snapshot = snapshot;

        if (_isGeneratingCoaching &&
            (_coachingPeriod != snapshot.Period ||
             !string.Equals(_coachingFingerprint, snapshot.DataFingerprint, StringComparison.Ordinal)))
        {
            _coachingRevision++;
            CancelAndDispose(ref _coachingCts);
            _isGeneratingCoaching = false;
            _coachingFingerprint = null;
        }

        bool hasData = snapshot.HasData;
        EmptyStateCard.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        HeroBentoRow.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        KpiStrip.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        QualityCard.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        HourlyChartCard.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        TopListsRow.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;

        // Row 2: 4 KPI Cards (total focus, block rate, flow protection applied inside ApplyQualityMetrics, selective passed)
        KpiTotalFocusText.Text = FormatDuration(snapshot.TotalFocusDuration);
        KpiSessionCountText.Text = $"세션 {snapshot.FocusSessionCount}회";

        double blockRate = SanitizePercent(snapshot.BlockRatePercent);
        KpiBlockRateText.Text = snapshot.TotalNotifications == 0 ? "0%" : $"{Math.Round(blockRate):0}%";
        KpiBlockRateSubText.Text = $"차단 {snapshot.BlockedCount} / 전체 {snapshot.TotalNotifications}건";

        int selectedPassed = snapshot.UrgentPassedCount + snapshot.RelevancePassedCount;
        KpiPassedTotalText.Text = $"{selectedPassed}건";
        KpiPassedSplitText.Text = $"긴급 {snapshot.UrgentPassedCount} · 관련 {snapshot.RelevancePassedCount}";

        // Row 3: Coaching (initial rule-based or cached AI)
        if (periodOrDataChanged || _currentCoaching is null)
        {
            CoachingErrorText.Visibility = Visibility.Collapsed;
            CoachingErrorText.Text = "";
            var initial = _coaching?.GetInitialOrCached(snapshot) ?? CreateFallbackCoaching(snapshot);
            ApplyCoachingResult(initial);
        }
        else
        {
            UpdateCoachingButtonState();
        }

        // Row 1 & Row 3 Right (Overview): Streak/Heatmap Bento + Flow Protection KPI + Work Purity Donut/Self-Distraction
        ApplyQualityMetrics(snapshot.Quality, snapshot.Period);
        UpdateResponsiveLayout();

        // Sub-Tab 2: 24-Hour Distribution
        if (snapshot.GoldenHour is { } goldenHour)
        {
            GoldenHourBadgeText.Text = $"골든타임 {goldenHour:00}시";
            GoldenHourBadge.Visibility = Visibility.Visible;
        }
        else
        {
            GoldenHourBadgeText.Text = "";
            GoldenHourBadge.Visibility = Visibility.Collapsed;
        }

        if (snapshot.DistractionPeakHour is { } peakHour)
        {
            DistractionPeakBadgeText.Text = $"방해 피크 {peakHour:00}시";
            DistractionPeakBadge.Visibility = Visibility.Visible;
        }
        else
        {
            DistractionPeakBadgeText.Text = "";
            DistractionPeakBadge.Visibility = Visibility.Collapsed;
        }

        HourlyChartItems.ItemsSource = BuildHourlyRows(snapshot.HourlyBuckets);

        // Sub-Tab 2: Top 5 Lists
        var distractors = snapshot.TopDistractors
            .Take(5)
            .Select(item => new DistractorRowViewModel(item))
            .ToArray();
        TopDistractorsItems.ItemsSource = distractors;
        TopDistractorsEmptyText.Visibility = distractors.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var contexts = snapshot.TopWorkContexts
            .Take(5)
            .Select(item => new WorkContextRowViewModel(item))
            .ToArray();
        TopWorkContextsItems.ItemsSource = contexts;
        TopWorkContextsEmptyText.Visibility = contexts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyQualityMetrics(InsightQualityMetrics? quality, InsightPeriod period)
    {
        if (quality is not null && quality.FlowProtectionEligibleCount > 0)
        {
            double flowRate = SanitizePercent(quality.FlowProtectionRatePercent);
            FlowProtectionRateText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", Math.Round(flowRate, 1));
            FlowProtectionSubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "차단 {0:N0}건 중 {1:N0}건 유지",
                quality.FlowProtectionEligibleCount,
                quality.FlowProtectedCount);
        }
        else
        {
            FlowProtectionRateText.Text = "-";
            FlowProtectionSubText.Text = "매칭 기록 없음";
        }

        double workPct = quality is null ? 0 : SanitizePercent(quality.WorkPurityPercent);
        double neutralPct = quality is null ? 0 : SanitizePercent(quality.NeutralPercent);
        double leisurePct = quality is null ? 0 : SanitizePercent(quality.LeisurePercent);
        double purityTotal = workPct + neutralPct + leisurePct;

        if (purityTotal > 0)
        {
            double workSweep = workPct / purityTotal * 360.0;
            double neutralSweep = neutralPct / purityTotal * 360.0;
            double leisureSweep = Math.Max(0.0, 360.0 - workSweep - neutralSweep);

            double cursorDeg = -90.0;
            WorkPurityArcPath.Data = BuildRingArcGeometry(48.0, 48.0, 43.0, cursorDeg, workSweep);
            cursorDeg += workSweep;
            NeutralPurityArcPath.Data = BuildRingArcGeometry(48.0, 48.0, 43.0, cursorDeg, neutralSweep);
            cursorDeg += neutralSweep;
            LeisurePurityArcPath.Data = BuildRingArcGeometry(48.0, 48.0, 43.0, cursorDeg, leisureSweep);
            WorkPurityCenterText.Text = $"{Math.Round(workPct):0}%";
        }
        else
        {
            WorkPurityArcPath.Data = System.Windows.Media.Geometry.Empty;
            NeutralPurityArcPath.Data = System.Windows.Media.Geometry.Empty;
            LeisurePurityArcPath.Data = System.Windows.Media.Geometry.Empty;
            WorkPurityCenterText.Text = "-";
        }

        WorkPurityPercentText.Text = $"{Math.Round(workPct):0}%";
        NeutralPurityPercentText.Text = $"{Math.Round(neutralPct):0}%";
        LeisurePurityPercentText.Text = $"{Math.Round(leisurePct):0}%";

        string legendText = $"순수 작업 {Math.Round(workPct):0}% · 메신저·웹 {Math.Round(neutralPct):0}% · 비작업 {Math.Round(leisurePct):0}%";
        AutomationProperties.SetName(WorkPurityDonutGrid, $"작업 순도: {legendText}");

        if (!string.IsNullOrWhiteSpace(quality?.TopSelfDistractionApp))
        {
            SelfDistractionText.Text = $"{quality.TopSelfDistractionApp} · {FormatDuration(quality.TopSelfDistractionDuration)}";
            SelfDistractionCallout.Visibility = Visibility.Visible;
            ApplySelfDistractionChangeBadge(quality.SelfDistractionChangePercent, period);
        }
        else
        {
            SelfDistractionText.Text = "";
            SelfDistractionCallout.Visibility = Visibility.Collapsed;
            SelfDistractionChangeBadgeText.Text = "";
            SelfDistractionChangeBadge.Visibility = Visibility.Collapsed;
        }

        int deepFocusCount = Math.Max(0, quality?.DeepFocusSessionCount ?? 0);
        TimeSpan avgDuration = quality is not null && quality.AverageSessionDuration > TimeSpan.Zero
            ? quality.AverageSessionDuration
            : TimeSpan.Zero;
        DeepFocusSummaryText.Text = $"깊은 몰입(25분+) {deepFocusCount}회 · 평균 {FormatDuration(avgDuration)}";

        int streakDays = Math.Max(0, quality?.CurrentStreakDays ?? 0);
        int bestStreak = Math.Max(0, quality?.BestStreakDays ?? 0);
        bool isTodayFocused = quality?.IsTodayFocused == true;

        if (bestStreak > 0)
        {
            BestStreakBadgeText.Text = $"최장 {bestStreak}일";
            BestStreakBadge.Visibility = Visibility.Visible;
        }
        else
        {
            BestStreakBadgeText.Text = "";
            BestStreakBadge.Visibility = Visibility.Collapsed;
        }

        if (streakDays > 0 && isTodayFocused)
        {
            StreakHeroTitleText.Foreground = (Brush)FindResource("BlackSuccess");
            StreakHeroTitleText.Text = $"🔥 {streakDays}일";
            StreakStateSubtitleText.Text = "연속 집중 중";

            var todayCell = quality?.HeatmapDays?.FirstOrDefault(d => d.IsToday);
            int todayMinutes = todayCell is not null
                ? Math.Max(0, (int)Math.Floor(todayCell.FocusDuration.TotalMinutes))
                : 0;
            StreakMotivationText.Text = todayMinutes < 30
                ? $"오늘 +{30 - todayMinutes}분 → 2단계"
                : todayMinutes < 60
                    ? $"오늘 +{60 - todayMinutes}분 → 3단계"
                    : todayMinutes < 120
                        ? $"오늘 +{120 - todayMinutes}분 → 4단계"
                        : "오늘 4단계 달성";
        }
        else if (streakDays > 0)
        {
            StreakHeroTitleText.Foreground = (Brush)FindResource("BlackFocus");
            StreakHeroTitleText.Text = $"{streakDays}일";
            StreakStateSubtitleText.Text = "연속 유지 중";
            StreakMotivationText.Text = $"오늘 집중 시 {streakDays + 1}일 연속";
        }
        else
        {
            StreakHeroTitleText.Foreground = (Brush)FindResource("BlackText");
            StreakHeroTitleText.Text = "0일";
            StreakStateSubtitleText.Text = "연속 기록 없음";
            StreakMotivationText.Text = bestStreak > 0
                ? "오늘 집중으로 재시작"
                : "오늘 첫 집중 대기";
        }

        if (quality?.PeriodChangePercent is { } rawChange &&
            !double.IsNaN(rawChange) && !double.IsInfinity(rawChange))
        {
            long rounded = (long)Math.Round(rawChange);
            string sign = rounded > 0 ? "+" : "";
            PeriodChangeBadgeText.Text = $"전기 대비 {sign}{rounded}%";
            if (rawChange >= 0)
            {
                PeriodChangeBadge.Background = (Brush)FindResource("BlackSelection");
                PeriodChangeBadge.BorderBrush = (Brush)FindResource("BlackSelectionRule");
                PeriodChangeBadgeText.Foreground = (Brush)FindResource("BlackAccentText");
            }
            else
            {
                PeriodChangeBadge.Background = (Brush)FindResource("BlackInput");
                PeriodChangeBadge.BorderBrush = (Brush)FindResource("BlackError");
                PeriodChangeBadgeText.Foreground = (Brush)FindResource("BlackError");
            }
            PeriodChangeBadge.Visibility = Visibility.Visible;
        }
        else
        {
            PeriodChangeBadgeText.Text = "";
            PeriodChangeBadge.Visibility = Visibility.Collapsed;
        }

        var normalizedDays = ResolveHeatmapDays(quality?.HeatmapDays);
        HeatmapMonthItems.ItemsSource = BuildHeatmapMonthLabels(normalizedDays);
        StreakHeatmapItems.ItemsSource = BuildHeatmapGridRows(normalizedDays);
    }

    private void ApplySelfDistractionChangeBadge(double? rawChange, InsightPeriod period)
    {
        if (rawChange is not { } value || double.IsNaN(value) || double.IsInfinity(value) ||
            period is not (InsightPeriod.Today or InsightPeriod.Last7Days))
        {
            SelfDistractionChangeBadgeText.Text = "";
            SelfDistractionChangeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        long rounded = (long)Math.Round(value);
        string prefix = period == InsightPeriod.Today ? "어제 대비" : "지난주 대비";
        string sign = rounded > 0 ? "+" : "";
        string arrow = rounded < 0 ? " ↓" : rounded > 0 ? " ↑" : "";
        SelfDistractionChangeBadgeText.Text = $"{prefix} {sign}{rounded}%{arrow}";

        if (rounded <= 0)
        {
            SelfDistractionChangeBadge.Background = (Brush)FindResource("BlackSuccessSurface");
            SelfDistractionChangeBadge.BorderBrush = (Brush)FindResource("BlackSuccessRule");
            SelfDistractionChangeBadgeText.Foreground = (Brush)FindResource("BlackSuccess");
        }
        else
        {
            SelfDistractionChangeBadge.Background = (Brush)FindResource("BlackInput");
            SelfDistractionChangeBadge.BorderBrush = (Brush)FindResource("BlackError");
            SelfDistractionChangeBadgeText.Foreground = (Brush)FindResource("BlackError");
        }

        SelfDistractionChangeBadge.Visibility = Visibility.Visible;
    }

    private void SetRefreshingState(bool isLoading)
    {
        PeriodLoadingBadge.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        double opacity = isLoading ? 0.55 : 1.0;
        EmptyStateCard.Opacity = opacity;
        HeroBentoRow.Opacity = opacity;
        KpiStrip.Opacity = opacity;
        CoachingCard.Opacity = opacity;
        QualityCard.Opacity = opacity;
        HourlyChartCard.Opacity = opacity;
        TopListsRow.Opacity = opacity;
    }

    private void ApplyCoachingBadge(InsightCoachingResult? result)
    {
        if (result is { IsAiGenerated: true, IsFromCache: true })
        {
            CoachingStatusBadgeText.Text = "저장된 AI 코칭";
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackAccentText");
        }
        else if (result is { IsAiGenerated: true })
        {
            CoachingStatusBadgeText.Text = "AI 코칭";
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackSuccess");
        }
        else
        {
            CoachingStatusBadgeText.Text = "규칙 요약";
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackMuted");
        }
    }

    private void ApplyCoachingResult(InsightCoachingResult result)
    {
        _currentCoaching = result;
        CoachingHeadlineText.Text = string.IsNullOrWhiteSpace(result.Headline)
            ? "집중 패턴 요약을 확인하세요."
            : result.Headline;

        var findings = result.Findings?.Where(f => !string.IsNullOrWhiteSpace(f)).Take(2).ToArray() ?? [];
        CoachingFindingsItems.ItemsSource = findings;
        CoachingFindingsItems.Visibility = findings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(result.ActionTip))
        {
            CoachingActionTipText.Text = result.ActionTip;
            CoachingActionTipBox.Visibility = Visibility.Visible;
        }
        else
        {
            CoachingActionTipText.Text = "";
            CoachingActionTipBox.Visibility = Visibility.Collapsed;
        }

        ApplyCoachingBadge(result);
        UpdateCoachingButtonState();
    }

    private void UpdateCoachingButtonState()
    {
        if (_isGeneratingCoaching)
        {
            CoachingLoadingBanner.Visibility = Visibility.Visible;
            CoachingStatusBadgeText.Text = "분석 중…";
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackAccentText");
            GenerateCoachingButton.IsEnabled = false;
            GenerateCoachingButton.Content = "분석 중…";
            GenerateCoachingButton.ToolTip = "AI 분석 중";
            return;
        }

        CoachingLoadingBanner.Visibility = Visibility.Collapsed;
        ApplyCoachingBadge(_currentCoaching);

        bool hasData = _snapshot?.HasData == true;
        bool aiAvailable = _coaching?.IsAiAvailable == true;

        if (!hasData)
        {
            GenerateCoachingButton.IsEnabled = false;
            GenerateCoachingButton.Content = "AI 코칭 분석";
            GenerateCoachingButton.ToolTip = "기록 없음";
            return;
        }

        if (!aiAvailable)
        {
            GenerateCoachingButton.IsEnabled = false;
            GenerateCoachingButton.Content = "AI 코칭 분석";
            GenerateCoachingButton.ToolTip = "로컬 AI 모델 없음";
            return;
        }

        GenerateCoachingButton.IsEnabled = true;
        GenerateCoachingButton.Content = _currentCoaching?.IsAiGenerated == true ? "다시 분석" : "AI 코칭 분석";
        GenerateCoachingButton.ToolTip = "현재 기간 AI 분석";
    }

    private void SubTabButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not System.Windows.Controls.RadioButton { Tag: string tag })
            return;

        bool isDistraction = string.Equals(tag, "Distraction", StringComparison.Ordinal);
        OverviewSubTabPanel.Visibility = isDistraction ? Visibility.Collapsed : Visibility.Visible;
        DistractionSubTabPanel.Visibility = isDistraction ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout();
    }

    private async void PeriodButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not System.Windows.Controls.RadioButton { Tag: string tag })
            return;

        var period = tag switch
        {
            "Today" => InsightPeriod.Today,
            "AllTime" => InsightPeriod.AllTime,
            _ => InsightPeriod.Last7Days
        };

        if (period == _selectedPeriod)
            return;

        _selectedPeriod = period;
        await RefreshAsync();
    }

    private async void GenerateCoachingButton_Click(object sender, RoutedEventArgs e)
    {
        await GenerateCoachingAsync();
    }

    /// <summary>
    /// 잔디밭 칸을 정사각형으로 유지한다. 칸 크기는 카드 폭과 20주 열 수에 맞춰 8–22px 사이에서 정하고,
    /// 월 라벨과 그리드 너비를 동일하게 맞춰 왼쪽 정렬한다.
    /// </summary>
    private void StreakHeatmapGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHeatmapCellSize();
    }

    private void UpdateHeatmapCellSize()
    {
        const double labelColumn = 22, gap = 4;
        double available = Math.Max(0, StreakHeatmapGrid.ActualWidth - labelColumn);
        if (available <= 0)
            return;
        double cell = Math.Clamp(Math.Floor(available / HeatmapWeeks), 8, 22);
        double gridWidth = cell * HeatmapWeeks;
        StreakHeatmapItems.Width = gridWidth;
        HeatmapMonthItems.Width = gridWidth;
        StreakHeatmapItems.Resources["HeatmapCellHeight"] = Math.Max(6, cell - gap);
    }

    private void InsightsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (HeroBentoRow is null || KpiRow is null || QualityInnerGrid is null || TopListsRow is null)
            return;

        bool compact = InsightsScroll.ActualWidth > 0 && InsightsScroll.ActualWidth < 680;
        ApplyKpiStripLayout(compact);

        ApplyWeightedTwoColumnLayout(HeroBentoRow, HeatmapTileCard, 2.8, 7.2, compact);

        QualityInnerGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1.0 : 5.5, GridUnitType.Star);
        QualityInnerGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 20);
        QualityInnerGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 4.5, compact ? GridUnitType.Pixel : GridUnitType.Star);
        Grid.SetColumn(QualityRightColumn, compact ? 0 : 2);
        Grid.SetRow(QualityRightColumn, compact ? 1 : 0);
        QualityRightColumn.BorderThickness = compact ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        QualityRightColumn.Padding = compact ? new Thickness(0, 12, 0, 0) : new Thickness(20, 4, 0, 4);
        QualityRightColumn.Margin = compact ? new Thickness(0, 14, 0, 0) : new Thickness(0);

        ApplyWeightedTwoColumnLayout(TopListsRow, TopWorkContextsCard, 1.0, 1.0, compact);
    }

    private void ApplyKpiStripLayout(bool compact)
    {
        KpiRow.Columns = compact ? 2 : 4;
        if (KpiCell0 is null || KpiCell1 is null || KpiCell2 is null || KpiCell3 is null)
            return;

        if (compact)
        {
            KpiCell0.BorderThickness = new Thickness(0);
            KpiCell0.Padding = new Thickness(0, 4, 12, 10);

            KpiCell1.BorderThickness = new Thickness(1, 0, 0, 0);
            KpiCell1.Padding = new Thickness(14, 4, 0, 10);

            KpiCell2.BorderThickness = new Thickness(0, 1, 0, 0);
            KpiCell2.Padding = new Thickness(0, 10, 12, 4);

            KpiCell3.BorderThickness = new Thickness(1, 1, 0, 0);
            KpiCell3.Padding = new Thickness(14, 10, 0, 4);
        }
        else
        {
            KpiCell0.BorderThickness = new Thickness(0);
            KpiCell0.Padding = new Thickness(0, 2, 12, 2);

            KpiCell1.BorderThickness = new Thickness(1, 0, 0, 0);
            KpiCell1.Padding = new Thickness(16, 2, 12, 2);

            KpiCell2.BorderThickness = new Thickness(1, 0, 0, 0);
            KpiCell2.Padding = new Thickness(16, 2, 12, 2);

            KpiCell3.BorderThickness = new Thickness(1, 0, 0, 0);
            KpiCell3.Padding = new Thickness(16, 2, 0, 2);
        }
    }

    private static void ApplyWeightedTwoColumnLayout(
        Grid rowGrid,
        FrameworkElement secondCard,
        double leftWeight,
        double rightWeight,
        bool compact)
    {
        rowGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1.0 : leftWeight, GridUnitType.Star);
        rowGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 16);
        rowGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : rightWeight, compact ? GridUnitType.Pixel : GridUnitType.Star);

        Grid.SetColumn(secondCard, compact ? 0 : 2);
        Grid.SetRow(secondCard, compact ? 1 : 0);
        secondCard.Margin = new Thickness(0, compact ? 16 : 0, 0, 0);
    }

    private static System.Windows.Media.Geometry BuildRingArcGeometry(
        double centerX,
        double centerY,
        double radius,
        double startDeg,
        double sweepDeg)
    {
        if (sweepDeg <= 0.1 || double.IsNaN(sweepDeg) || double.IsInfinity(sweepDeg))
            return System.Windows.Media.Geometry.Empty;

        if (sweepDeg >= 359.9)
        {
            double topRad = -Math.PI / 2.0;
            double bottomRad = Math.PI / 2.0;
            var top = new System.Windows.Point(centerX + radius * Math.Cos(topRad), centerY + radius * Math.Sin(topRad));
            var bottom = new System.Windows.Point(centerX + radius * Math.Cos(bottomRad), centerY + radius * Math.Sin(bottomRad));
            var arcSize = new System.Windows.Size(radius, radius);

            var fullGeom = new System.Windows.Media.StreamGeometry();
            using (var ctx = fullGeom.Open())
            {
                ctx.BeginFigure(top, isFilled: false, isClosed: true);
                ctx.ArcTo(bottom, arcSize, 0, false, System.Windows.Media.SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
                ctx.ArcTo(top, arcSize, 0, false, System.Windows.Media.SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }
            fullGeom.Freeze();
            return fullGeom;
        }

        double startRad = startDeg * Math.PI / 180.0;
        double endRad = (startDeg + sweepDeg) * Math.PI / 180.0;
        var startPt = new System.Windows.Point(centerX + radius * Math.Cos(startRad), centerY + radius * Math.Sin(startRad));
        var endPt = new System.Windows.Point(centerX + radius * Math.Cos(endRad), centerY + radius * Math.Sin(endRad));
        bool isLargeArc = sweepDeg > 180.0;

        var geom = new System.Windows.Media.StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(startPt, isFilled: false, isClosed: false);
            ctx.ArcTo(endPt, new System.Windows.Size(radius, radius), 0, isLargeArc, System.Windows.Media.SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geom.Freeze();
        return geom;
    }

    private static IReadOnlyList<InsightHeatmapDay> ResolveHeatmapDays(IReadOnlyList<InsightHeatmapDay>? heatmapDays)
    {
        if (heatmapDays is { Count: HeatmapDaysCount })
            return heatmapDays;

        var today = DateTime.Now.Date;
        int todayDow = (int)today.DayOfWeek;
        var gridStart = today.AddDays(-todayDow - (HeatmapWeeks - 1) * 7);
        var fallback = new InsightHeatmapDay[HeatmapDaysCount];
        for (int offset = 0; offset < HeatmapDaysCount; offset++)
        {
            var date = gridStart.AddDays(offset);
            bool isFuture = date > today;
            bool isToday = date == today;
            fallback[offset] = new InsightHeatmapDay(
                LocalDate: date,
                FocusDuration: TimeSpan.Zero,
                IntensityLevel: 0,
                IsToday: isToday,
                IsFuture: isFuture,
                TooltipText: isFuture ? string.Empty : $"{date:M월 d일} · 기록 없음");
        }
        return fallback;
    }

    private static string[] BuildHeatmapMonthLabels(IReadOnlyList<InsightHeatmapDay> days)
    {
        var labels = new string[HeatmapWeeks];
        int lastMonth = -1;
        int lastLabeledWeek = -10;

        for (int week = 0; week < HeatmapWeeks; week++)
        {
            var weekStart = days[week * 7].LocalDate;
            if (week == 0 || weekStart.Month != lastMonth)
            {
                if (week == 0 || week - lastLabeledWeek >= 2)
                {
                    labels[week] = $"{weekStart.Month}월";
                    lastLabeledWeek = week;
                }
                else
                {
                    labels[week] = string.Empty;
                }
                lastMonth = weekStart.Month;
            }
            else
            {
                labels[week] = string.Empty;
            }
        }

        return labels;
    }

    private static HeatmapCellViewModel[] BuildHeatmapGridRows(IReadOnlyList<InsightHeatmapDay> source)
    {
        // UniformGrid(Rows=7, Columns=20)은 행 우선(row-major)으로 채우므로
        // (요일 row 0..6, 주 col 0..19) 순서로 재배열한다.
        var gridCells = new HeatmapCellViewModel[HeatmapDaysCount];
        for (int dow = 0; dow < 7; dow++)
        {
            for (int week = 0; week < HeatmapWeeks; week++)
            {
                gridCells[dow * HeatmapWeeks + week] = new HeatmapCellViewModel(source[week * 7 + dow]);
            }
        }
        return gridCells;
    }

    private static HourlyBucketRowViewModel[] BuildHourlyRows(IReadOnlyList<InsightHourlyBucket>? buckets)
    {
        var byHour = buckets?.GroupBy(b => b.Hour).ToDictionary(g => g.Key, g => g.First())
            ?? new Dictionary<int, InsightHourlyBucket>();

        var rows = new HourlyBucketRowViewModel[24];
        for (int h = 0; h < 24; h++)
        {
            if (!byHour.TryGetValue(h, out var bucket))
            {
                bucket = new InsightHourlyBucket(
                    h, TimeSpan.Zero, 0, 0, 0, 0, false, false,
                    $"{h}시 · 집중 0분 · 차단 0건 · 통과 0건");
            }
            rows[h] = new HourlyBucketRowViewModel(bucket);
        }
        return rows;
    }

    private static FocusInsightSnapshot CreateEmptySnapshot(InsightPeriod period)
    {
        var buckets = Enumerable.Range(0, 24)
            .Select(h => new InsightHourlyBucket(
                h, TimeSpan.Zero, 0, 0, 0, 0, false, false,
                $"{h}시 · 집중 0분 · 차단 0건 · 통과 0건"))
            .ToArray();

        return new FocusInsightSnapshot(
            Period: period,
            GeneratedAtUtc: DateTime.UtcNow,
            TotalFocusDuration: TimeSpan.Zero,
            FocusSessionCount: 0,
            TotalNotifications: 0,
            BlockedCount: 0,
            PassedCount: 0,
            UrgentPassedCount: 0,
            RelevancePassedCount: 0,
            OtherPassedCount: 0,
            BlockRatePercent: 0,
            GoldenHour: null,
            GoldenHourDuration: TimeSpan.Zero,
            DistractionPeakHour: null,
            DistractionPeakCount: 0,
            HourlyBuckets: buckets,
            TopDistractors: [],
            TopWorkContexts: [],
            DataFingerprint: "empty",
            RuleSummaryLine: "집중 기록 없음");
    }

    private static InsightCoachingResult CreateFallbackCoaching(FocusInsightSnapshot snapshot)
    {
        if (!snapshot.HasData)
        {
            return new InsightCoachingResult(
                Headline: "집중 기록 없음",
                Findings: ["집중 세션 완료 시 패턴·방해 집계"],
                ActionTip: "집중 모드 시작 후 기록 생성",
                IsAiGenerated: false,
                IsFromCache: false,
                GeneratedAtUtc: DateTime.UtcNow,
                DataFingerprint: snapshot.DataFingerprint);
        }

        return new InsightCoachingResult(
            Headline: string.IsNullOrWhiteSpace(snapshot.RuleSummaryLine)
                ? $"총 {FormatDuration(snapshot.TotalFocusDuration)} 집중 · 알림 {snapshot.BlockedCount}건 차단"
                : snapshot.RuleSummaryLine,
            Findings:
            [
                $"세션 {snapshot.FocusSessionCount}회 · 차단율 {Math.Round(SanitizePercent(snapshot.BlockRatePercent)):0}%",
                $"긴급 {snapshot.UrgentPassedCount}건 · 작업 관련 {snapshot.RelevancePassedCount}건 통과"
            ],
            ActionTip: snapshot.GoldenHour is { } gh
                ? $"{gh:00}시 골든타임에 핵심 작업 배치 권장"
                : "집중 세션 기록 유지",
            IsAiGenerated: false,
            IsFromCache: false,
            GeneratedAtUtc: DateTime.UtcNow,
            DataFingerprint: snapshot.DataFingerprint);
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "0분";

        int totalMinutes = (int)Math.Floor(duration.TotalMinutes);
        if (totalMinutes == 0)
            return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))}초";

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        if (hours == 0)
            return $"{minutes}분";
        return minutes == 0 ? $"{hours}시간" : $"{hours}시간 {minutes}분";
    }

    private static double SanitizeBarHeight(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 88);

    private static double SanitizePercent(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 100);

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts is null) return;
        var current = cts;
        cts = null;
        try { current.Cancel(); }
        catch { /* ignore */ }
        current.Dispose();
    }

    private sealed class HeatmapCellViewModel
    {
        public HeatmapCellViewModel(InsightHeatmapDay day)
        {
            IntensityLevel = day.IsFuture ? 0 : Math.Clamp(day.IntensityLevel, 0, 4);
            IsToday = day.IsToday;
            IsFuture = day.IsFuture;
            IsInteractive = !day.IsFuture;

            string dayLabel = KoreanDayLabels[(int)day.LocalDate.DayOfWeek];
            DateHeaderText = day.IsToday
                ? $"{day.LocalDate:M월 d일} ({dayLabel}) · 오늘"
                : $"{day.LocalDate:M월 d일} ({dayLabel})";
            LevelLabelText = IntensityLevel == 0 ? "기록 없음" : $"{IntensityLevel}단계";
            FocusDurationText = FormatDuration(day.FocusDuration);
            SessionCountText = $"{Math.Max(0, day.SessionCount)}회";
            BlockedCountText = $"{Math.Max(0, day.BlockedCount)}건";
            WorkPurityText = day.WorkPurityPercent is { } wp && !double.IsNaN(wp) && !double.IsInfinity(wp)
                ? $"{Math.Round(SanitizePercent(wp)):0}%"
                : "-";

            TooltipText = day.IsFuture
                ? string.Empty
                : string.IsNullOrWhiteSpace(day.TooltipText)
                    ? $"{day.LocalDate:M월 d일} ({dayLabel}) · 집중 {FocusDurationText}"
                    : day.TooltipText;
        }

        public int IntensityLevel { get; }
        public bool IsToday { get; }
        public bool IsFuture { get; }
        public bool IsInteractive { get; }
        public string DateHeaderText { get; }
        public string LevelLabelText { get; }
        public string FocusDurationText { get; }
        public string SessionCountText { get; }
        public string BlockedCountText { get; }
        public string WorkPurityText { get; }
        public string TooltipText { get; }
    }

    private sealed class HourlyBucketRowViewModel
    {
        public HourlyBucketRowViewModel(InsightHourlyBucket bucket)
        {
            Hour = bucket.Hour;
            FocusBarHeight = SanitizeBarHeight(bucket.FocusBarHeight);
            NotificationBarHeight = SanitizeBarHeight(bucket.NotificationBarHeight);
            IsGoldenHour = bucket.IsGoldenHour;
            IsDistractionPeak = bucket.IsDistractionPeak;
            TooltipText = string.IsNullOrWhiteSpace(bucket.TooltipText)
                ? $"{bucket.Hour}시 · 집중 {FormatDuration(bucket.FocusDuration)} · 차단 {bucket.BlockedCount}건 · 통과 {bucket.PassedCount}건"
                : bucket.TooltipText;
            AxisLabel = bucket.Hour is 0 or 6 or 12 or 18 or 23 ? bucket.Hour.ToString() : "";
        }

        public int Hour { get; }
        public double FocusBarHeight { get; }
        public double NotificationBarHeight { get; }
        public bool IsGoldenHour { get; }
        public bool IsDistractionPeak { get; }
        public string TooltipText { get; }
        public string AxisLabel { get; }
    }

    private sealed class DistractorRowViewModel
    {
        public DistractorRowViewModel(InsightDistractorItem item)
        {
            DisplayLabel = item.DisplayLabel;
            AppName = item.AppName;
            CountSummaryText = $"차단 {item.BlockedCount} · 통과 {item.PassedCount}";
            BlockRatePercent = SanitizePercent(item.BlockRatePercent);
            BlockRateText = $"{Math.Round(BlockRatePercent):0}%";
        }

        public string DisplayLabel { get; }
        public string AppName { get; }
        public string CountSummaryText { get; }
        public double BlockRatePercent { get; }
        public string BlockRateText { get; }
    }

    private sealed class WorkContextRowViewModel
    {
        public WorkContextRowViewModel(InsightWorkContextItem item)
        {
            ProcessName = item.ProcessName;
            DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.ProcessName : item.DisplayName;
            CountSummaryText = $"차단 {item.BlockedCount} · 통과 {item.PassedCount}";
            FocusDurationText = FormatDuration(item.FocusDuration);
        }

        public string ProcessName { get; }
        public string DisplayName { get; }
        public string CountSummaryText { get; }
        public string FocusDurationText { get; }
    }
}

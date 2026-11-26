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
            StatusErrorText.Text = "집중 인사이트를 불러오지 못했어요. 잠시 후 다시 시도해 주세요.";
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
            CoachingErrorText.Text = "AI 코칭 생성에 실패했어요. 잠시 후 다시 시도해 주세요.";
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

        SectionSubtitleText.Text = snapshot.Period switch
        {
            InsightPeriod.Today => "오늘 · 집중 패턴 · 방해 차단 · Gemma AI 코칭",
            InsightPeriod.AllTime => "전체 기간 · 집중 패턴 · 방해 차단 · Gemma AI 코칭",
            _ => "최근 7일 · 집중 패턴 · 방해 차단 · Gemma AI 코칭"
        };

        bool hasData = snapshot.HasData;
        EmptyStateCard.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        KpiRow.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        QualityTrendRow.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        HourlyChartCard.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        TopListsRow.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;

        // Row 1: 4 KPI Cards
        KpiTotalFocusText.Text = FormatDuration(snapshot.TotalFocusDuration);
        KpiSessionCountText.Text = $"세션 {snapshot.FocusSessionCount}회";

        double blockRate = SanitizePercent(snapshot.BlockRatePercent);
        KpiBlockRateText.Text = snapshot.TotalNotifications == 0 ? "0%" : $"{Math.Round(blockRate):0}%";
        KpiBlockRateSubText.Text = $"차단 {snapshot.BlockedCount} / 전체 {snapshot.TotalNotifications}건";

        KpiUrgentPassedText.Text = $"{snapshot.UrgentPassedCount}건";
        KpiRelevancePassedText.Text = $"{snapshot.RelevancePassedCount}건";

        // Row 2: Coaching (initial rule-based or cached AI)
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

        // Row 3 (Overview): Quality & 7-Day Trend/Streak
        ApplyQualityMetrics(snapshot.Quality);

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

    private void ApplyQualityMetrics(InsightQualityMetrics? quality)
    {
        if (quality is not null && quality.FlowProtectionEligibleCount > 0)
        {
            double flowRate = SanitizePercent(quality.FlowProtectionRatePercent);
            FlowProtectionRateText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", Math.Round(flowRate, 1));
            FlowProtectionSubText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "차단 {0:N0}건 중 {1:N0}건 흐름 유지",
                quality.FlowProtectionEligibleCount,
                quality.FlowProtectedCount);
        }
        else
        {
            FlowProtectionRateText.Text = "-";
            FlowProtectionSubText.Text = "차단 알림 매칭 기록 없음";
        }

        double workPct = quality is null ? 0 : SanitizePercent(quality.WorkPurityPercent);
        double neutralPct = quality is null ? 0 : SanitizePercent(quality.NeutralPercent);
        double leisurePct = quality is null ? 0 : SanitizePercent(quality.LeisurePercent);
        double purityTotal = workPct + neutralPct + leisurePct;

        if (purityTotal > 0)
        {
            WorkPurityBarGrid.ColumnDefinitions[0].Width = new GridLength(workPct, GridUnitType.Star);
            WorkPurityBarGrid.ColumnDefinitions[1].Width = new GridLength(neutralPct, GridUnitType.Star);
            WorkPurityBarGrid.ColumnDefinitions[2].Width = new GridLength(leisurePct, GridUnitType.Star);
        }
        else
        {
            WorkPurityBarGrid.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Pixel);
            WorkPurityBarGrid.ColumnDefinitions[1].Width = new GridLength(0, GridUnitType.Pixel);
            WorkPurityBarGrid.ColumnDefinitions[2].Width = new GridLength(0, GridUnitType.Pixel);
        }

        string legendText = $"순수 작업 {Math.Round(workPct):0}% · 메신저·웹 {Math.Round(neutralPct):0}% · 비작업 {Math.Round(leisurePct):0}%";
        WorkPurityLegendText.Text = legendText;
        AutomationProperties.SetName(WorkPurityBarGrid, $"작업 순도: {legendText}");

        if (!string.IsNullOrWhiteSpace(quality?.TopSelfDistractionApp))
        {
            SelfDistractionText.Text = $"직접 확인 최다 · {quality.TopSelfDistractionApp} ({FormatDuration(quality.TopSelfDistractionDuration)})";
            SelfDistractionCallout.Visibility = Visibility.Visible;
        }
        else
        {
            SelfDistractionText.Text = "";
            SelfDistractionCallout.Visibility = Visibility.Collapsed;
        }

        int deepFocusCount = Math.Max(0, quality?.DeepFocusSessionCount ?? 0);
        TimeSpan avgDuration = quality is not null && quality.AverageSessionDuration > TimeSpan.Zero
            ? quality.AverageSessionDuration
            : TimeSpan.Zero;
        DeepFocusSummaryText.Text = $"깊은 몰입(25분+) {deepFocusCount}회 · 평균 세션 {FormatDuration(avgDuration)}";

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

        if (streakDays > 0)
        {
            StreakBadgeText.Text = $"{streakDays}일 연속 집중";
            StreakBadge.Visibility = Visibility.Visible;
        }
        else
        {
            StreakBadgeText.Text = "";
            StreakBadge.Visibility = Visibility.Collapsed;
        }

        if (streakDays > 0 && isTodayFocused)
        {
            StreakHeroBox.Background = (Brush)FindResource("BlackSuccessSurface");
            StreakHeroBox.BorderBrush = (Brush)FindResource("BlackSuccessRule");
            StreakHeroTitleText.Foreground = (Brush)FindResource("BlackSuccess");
            StreakHeroTitleText.Text = $"🔥 {streakDays}일 연속 집중 중";
            StreakMotivationText.Text = streakDays >= 7
                ? "오늘도 잔디를 심어 1주 이상 연속 기록을 이어가고 있어요!"
                : streakDays >= 2
                    ? "오늘 잔디를 심었어요. 내일도 이어가면 기록이 더 단단해져요!"
                    : "오늘 첫 잔디를 심었어요. 내일도 이어서 연속 기록을 시작해 보세요!";
        }
        else if (streakDays > 0)
        {
            StreakHeroBox.Background = (Brush)FindResource("BlackSelection");
            StreakHeroBox.BorderBrush = (Brush)FindResource("BlackAction");
            StreakHeroTitleText.Foreground = (Brush)FindResource("BlackFocus");
            StreakHeroTitleText.Text = $"⚡ {streakDays}일 연속 기록 유지 중";
            StreakMotivationText.Text = $"오늘 한 번만 집중하면 {streakDays + 1}일 연속으로 이어져요. 기록을 지켜보세요!";
        }
        else
        {
            StreakHeroBox.Background = (Brush)FindResource("BlackInput");
            StreakHeroBox.BorderBrush = (Brush)FindResource("BlackRule");
            StreakHeroTitleText.Foreground = (Brush)FindResource("BlackText");
            StreakHeroTitleText.Text = "🌱 연속 집중을 시작해 보세요";
            StreakMotivationText.Text = bestStreak > 0
                ? $"최장 {bestStreak}일 연속 기록이 있어요. 오늘 첫 잔디를 다시 심어 보세요!"
                : "집중 세션을 마치면 오늘 칸에 첫 잔디가 채워져요.";
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

        StreakHeatmapItems.ItemsSource = BuildHeatmapGridRows(quality?.HeatmapDays);
    }

    private void SetRefreshingState(bool isLoading)
    {
        PeriodLoadingBadge.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        double opacity = isLoading ? 0.55 : 1.0;
        EmptyStateCard.Opacity = opacity;
        KpiRow.Opacity = opacity;
        CoachingCard.Opacity = opacity;
        QualityTrendRow.Opacity = opacity;
        HourlyChartCard.Opacity = opacity;
        TopListsRow.Opacity = opacity;
    }

    private void ApplyCoachingBadge(InsightCoachingResult? result)
    {
        if (result is { IsAiGenerated: true, IsFromCache: true })
        {
            CoachingStatusBadgeText.Text = "저장된 AI 코칭";
            CoachingStatusBadge.Background = (Brush)FindResource("BlackSelection");
            CoachingStatusBadge.BorderBrush = (Brush)FindResource("BlackSelectionRule");
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackAccentText");
        }
        else if (result is { IsAiGenerated: true })
        {
            CoachingStatusBadgeText.Text = "AI 코칭";
            CoachingStatusBadge.Background = (Brush)FindResource("BlackSuccessSurface");
            CoachingStatusBadge.BorderBrush = (Brush)FindResource("BlackSuccessRule");
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackSuccess");
        }
        else
        {
            CoachingStatusBadgeText.Text = "규칙 요약";
            CoachingStatusBadge.Background = (Brush)FindResource("BlackInput");
            CoachingStatusBadge.BorderBrush = (Brush)FindResource("BlackRule");
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
            CoachingStatusBadge.Background = (Brush)FindResource("BlackSelection");
            CoachingStatusBadge.BorderBrush = (Brush)FindResource("BlackSelectionRule");
            CoachingStatusBadgeText.Foreground = (Brush)FindResource("BlackAccentText");
            GenerateCoachingButton.IsEnabled = false;
            GenerateCoachingButton.Content = "Gemma 분석 중…";
            GenerateCoachingButton.ToolTip = "Gemma AI가 집중·방해 패턴을 분석하고 있습니다.";
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
            GenerateCoachingButton.ToolTip = "집중 기록이 쌓이면 AI 코칭을 분석할 수 있어요.";
            return;
        }

        if (!aiAvailable)
        {
            GenerateCoachingButton.IsEnabled = false;
            GenerateCoachingButton.Content = "AI 코칭 분석";
            GenerateCoachingButton.ToolTip = "로컬 Gemma AI 모델을 사용할 수 없어 규칙 요약만 표시합니다.";
            return;
        }

        GenerateCoachingButton.IsEnabled = true;
        GenerateCoachingButton.Content = _currentCoaching?.IsAiGenerated == true ? "다시 분석" : "AI 코칭 분석";
        GenerateCoachingButton.ToolTip = "현재 기간의 집중·방해 기록을 Gemma AI로 분석합니다.";
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

    private void InsightsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (KpiRow is null || QualityTrendRow is null || TopListsRow is null)
            return;

        bool compact = InsightsScroll.ActualWidth > 0 && InsightsScroll.ActualWidth < 680;
        KpiRow.Columns = compact ? 2 : 4;

        ApplyTwoColumnLayout(QualityTrendRow, TrendCard, compact);
        ApplyTwoColumnLayout(TopListsRow, TopWorkContextsCard, compact);
    }

    private static void ApplyTwoColumnLayout(Grid rowGrid, FrameworkElement secondCard, bool compact)
    {
        rowGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        rowGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 16);
        rowGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 1, compact ? GridUnitType.Pixel : GridUnitType.Star);

        Grid.SetColumn(secondCard, compact ? 0 : 2);
        Grid.SetRow(secondCard, compact ? 1 : 0);
        secondCard.Margin = new Thickness(0, compact ? 16 : 0, 0, 0);
    }

    private static HeatmapCellViewModel[] BuildHeatmapGridRows(IReadOnlyList<InsightHeatmapDay>? heatmapDays)
    {
        IReadOnlyList<InsightHeatmapDay> source;
        if (heatmapDays is { Count: 70 })
        {
            source = heatmapDays;
        }
        else
        {
            var today = DateTime.Now.Date;
            int todayDow = (int)today.DayOfWeek;
            var gridStart = today.AddDays(-todayDow - 9 * 7);
            var fallback = new InsightHeatmapDay[70];
            for (int offset = 0; offset < 70; offset++)
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
                    TooltipText: isFuture ? string.Empty : $"{date:M월 d일} · 집중 기록 없음");
            }
            source = fallback;
        }

        // UniformGrid(Rows=7, Columns=10)은 행 우선(row-major)으로 채우므로
        // (요일 row 0..6, 주 col 0..9) 순서로 재배열한다.
        var gridCells = new HeatmapCellViewModel[70];
        for (int dow = 0; dow < 7; dow++)
        {
            for (int week = 0; week < 10; week++)
            {
                gridCells[dow * 10 + week] = new HeatmapCellViewModel(source[week * 7 + dow]);
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
            RuleSummaryLine: "아직 분석할 집중 기록이 없어요.");
    }

    private static InsightCoachingResult CreateFallbackCoaching(FocusInsightSnapshot snapshot)
    {
        if (!snapshot.HasData)
        {
            return new InsightCoachingResult(
                Headline: "집중 기록이 쌓이면 맞춤 코칭을 제공해요.",
                Findings: ["집중 모드를 완료하면 시간대별 몰입도와 방해 패턴을 자동으로 분석합니다."],
                ActionTip: "홈 화면에서 집중 모드를 켜고 첫 번째 세션을 시작해 보세요.",
                IsAiGenerated: false,
                IsFromCache: false,
                GeneratedAtUtc: DateTime.UtcNow,
                DataFingerprint: snapshot.DataFingerprint);
        }

        return new InsightCoachingResult(
            Headline: string.IsNullOrWhiteSpace(snapshot.RuleSummaryLine)
                ? $"총 {FormatDuration(snapshot.TotalFocusDuration)} 집중하며 방해 알림 {snapshot.BlockedCount}건을 차단했어요."
                : snapshot.RuleSummaryLine,
            Findings:
            [
                $"세션 {snapshot.FocusSessionCount}회 동안 방해 차단율 {Math.Round(SanitizePercent(snapshot.BlockRatePercent)):0}%를 기록했습니다.",
                $"긴급 알림 {snapshot.UrgentPassedCount}건, 작업 관련 알림 {snapshot.RelevancePassedCount}건만 선별 통과했습니다."
            ],
            ActionTip: snapshot.GoldenHour is { } gh
                ? $"{gh:00}시 전후 골든타임에 핵심 작업을 배치해 보세요."
                : "집중 루틴을 꾸준히 기록하면 나만의 골든타임을 찾을 수 있어요.",
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
            TooltipText = day.IsFuture
                ? string.Empty
                : string.IsNullOrWhiteSpace(day.TooltipText)
                    ? $"{day.LocalDate:M월 d일} · 집중 {FormatDuration(day.FocusDuration)}"
                    : day.TooltipText;
        }

        public int IntensityLevel { get; }
        public bool IsToday { get; }
        public bool IsFuture { get; }
        public bool IsInteractive { get; }
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

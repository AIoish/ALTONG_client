using System.Windows;
using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Altong.Client.Services;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services.Calendar;
using Altong.Client.Services.Notifications;
using Altong.Client.Services.AI.Briefing;
using Brush = System.Windows.Media.Brush;

namespace Altong.Client;

public partial class SessionReportWindow : Window
{
    private SessionResult _result;
    private readonly SqliteSessionReportRepository? _reports;
    private bool _saving;
    private bool _saveSucceeded;
    private bool _loadingSummaries;
    private bool _closed;
    private readonly CancellationTokenSource _summaryLifetime = new();
    private readonly IReportSummaryProvider? _summaryProvider;
    private readonly bool _demoMode;
    private readonly DashboardCalendarStore _calendar;
    private readonly DashboardSummaryStateStore _summaryState;
    private readonly NotificationSummaryCalendarView _summaryCards;
    private readonly ReportNotificationItem[] _notificationItems;
    private sealed record ReportNotificationItem(string AppName, string? Sender, string Title, string Body,
        bool? IsPassed, string? ReplyText);
    private int _notificationDisplayLimit = 50;
    private int _appDisplayLimit = 50;
    private int _memoDisplayLimit = 20;
    private readonly ReportMemoItem[] _memoItems;

    internal sealed class ReportMemoItem(QuickMemoEntry entry) : INotifyPropertyChanged
    {
        public QuickMemoEntry Entry => entry;
        public string Text => entry.Text;
        public string WhenText => entry.WhenText;
        private bool _expanded;
        public bool IsExpanded
        {
            get => _expanded;
            set { if (_expanded == value) return; _expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public SessionReportWindow(SessionResult result, SqliteSessionReportRepository? reports = null, bool isSaved = false,
        IReportSummaryProvider? summaryProvider = null, DashboardCalendarStore? calendar = null,
        string? summaryStatePath = null)
    {
        _result = result;
        _memoItems = result.Memos.OrderBy(memo => memo.CreatedAt).ThenBy(memo => memo.Id, StringComparer.Ordinal)
            .Select(memo => new ReportMemoItem(memo)).ToArray();
        _reports = reports;
        _demoMode = Environment.GetEnvironmentVariable("ALTONG_CALENDAR_DEMO") == "1";
        var summaryFolder = Environment.GetEnvironmentVariable("ALTONG_SUMMARY_DIR");
        _summaryProvider = summaryProvider ?? (_demoMode ? new FakeReportSummaryProvider()
            : string.IsNullOrWhiteSpace(summaryFolder) ? null : new JsonReportSummaryProvider(summaryFolder));
        _calendar = calendar ?? new DashboardCalendarStore();
        _summaryState = new(summaryStatePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altong", "dashboard-summary-state.json"));
        _summaryState.Load();
        InitializeComponent();
        bool entered = false;
        ContentRendered += (_, _) =>
        {
            if (entered) return;
            entered = true;
            MotionBehavior.Transition(ReportSurface, prominent: !isSaved);
        };
        Title = isSaved ? "저장된 집중 리포트" : "집중 완료 · 집중 리포트";
        ReportHeadingText.Text = "집중 리포트";
        ReportBadgeText.Text = isSaved ? "보관됨" : "집중 완료";
        _summaryCards = new([], _demoMode);
        _summaryCards.Resources["SummaryCardSurface"] = FindResource("ReportSummarySurface");
        _summaryCards.ScheduleCorrected += SaveCorrection;
        _summaryCards.ScheduleSaveRequested += SaveSchedule;
        CalendarSummaryHost.Content = _summaryCards;
        _calendar.Changed += Calendar_Changed;
        ReloadSummaryButton.Visibility = _summaryProvider is null || result.SessionId is null ? Visibility.Collapsed : Visibility.Visible;
        Closing += (_, e) => e.Cancel = _saving && !_saveSucceeded;
        Closed += (_, _) =>
        {
            _closed = true;
            _summaryLifetime.Cancel();
            _calendar.Changed -= Calendar_Changed;
        };
        if (isSaved || reports is null || result.SessionId is null)
        {
            SaveReportButton.Visibility = Visibility.Collapsed;
            SkipSaveButton.Content = "확인";
            SaveStatusText.Text = isSaved ? "저장된 리포트" : "";
        }
        else
            SaveStatusText.Text = "저장 전";
        SourceInitialized += (_, _) => FitToWorkArea();
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        };
        var maximumSeconds = result.Apps.Count == 0 ? 0 : result.Apps.Max(app => app.Seconds);
        DataContext = result with
        {
            Apps = result.Apps.Select(app => app with
            {
                Percentage = maximumSeconds > 0 ? app.Seconds / maximumSeconds * 100 : 0
            }).ToArray()
        };
        var highlightApps = result.Notifications.Select(notification => notification.AppName)
            .Where(app => !string.IsNullOrWhiteSpace(app)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(app => app.Length).ToArray();
        LegacyHighlightItemsControl.ItemsSource = result.NotificationHighlights.Select(text =>
        {
            var app = highlightApps.FirstOrDefault(name => text.StartsWith(name + " · ", StringComparison.OrdinalIgnoreCase));
            return new { AppName = app, AppDisplayName = app is null ? "알림 요약" : AppNameDisplayConverter.Format(app), Text = app is null ? text : text[(app.Length + 3)..] };
        }).ToArray();
        var remainingReplies = result.QuickReplies.ToList();
        _notificationItems = result.Notifications.Select(notification =>
        {
            var replyIndex = remainingReplies.FindIndex(reply =>
                string.Equals(reply.AppName, notification.AppName, StringComparison.OrdinalIgnoreCase)
                && reply.Sender == notification.Sender && reply.NotificationTitle == notification.Title);
            var replyText = replyIndex >= 0 ? remainingReplies[replyIndex].Text
                : notification.IsPassed == false ? "확인했습니다. 곧 답변드릴게요." : null;
            if (replyIndex >= 0) remainingReplies.RemoveAt(replyIndex);
            return new ReportNotificationItem(notification.AppName, notification.Sender, notification.Title,
                notification.Body, notification.IsPassed, replyText);
        }).ToArray();
        UpdateSummaryContentVisibility();
        if (result.SummaryItems.Count > 0) ShowSummaryItems(result.SummaryItems);
        else if (_summaryProvider is not null && result.SessionId is not null) _ = LoadSummaryItemsAsync();
        else SetSummaryStatus(result.BlockedCount == 0 ? "요약할 알림이 없습니다." : "AI 요약이 아직 없습니다.");
        UpdateDetailLists();
    }

    private void UpdateDetailLists()
    {
        MemoSectionBorder.Visibility = _result.Memos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MemosExpander.Header = $"집중 중 메모 · {_result.Memos.Count}개";
        MemoPresenceText.Text = $"메모 {_result.Memos.Count}개";
        MemoPresenceBadge.Visibility = MemoSectionBorder.Visibility;
        MemoItemsControl.ItemsSource = _memoItems.Take(_memoDisplayLimit).ToArray();
        MoreMemosButton.Visibility = _result.Memos.Count > _memoDisplayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreMemosButton.Content = $"메모 더 보기 · {Math.Min(_memoDisplayLimit, _result.Memos.Count)}/{_result.Memos.Count}";
        NotificationItemsControl.ItemsSource = _notificationItems.Take(_notificationDisplayLimit).ToArray();
        AppItemsControl.ItemsSource = ((SessionResult)DataContext).Apps.Take(_appDisplayLimit).ToArray();
        MoreNotificationsButton.Visibility = _result.Notifications.Count > _notificationDisplayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreAppsButton.Visibility = _result.Apps.Count > _appDisplayLimit ? Visibility.Visible : Visibility.Collapsed;
        MoreNotificationsButton.Content = $"알림 더 보기 · {Math.Min(_notificationDisplayLimit, _result.Notifications.Count)}/{_result.Notifications.Count}";
        MoreAppsButton.Content = $"앱 더 보기 · {Math.Min(_appDisplayLimit, _result.Apps.Count)}/{_result.Apps.Count}";
    }

    private void MoreMemos_Click(object sender, RoutedEventArgs e)
    {
        _memoDisplayLimit += 20;
        UpdateDetailLists();
    }

    private void MemoPreview_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBlock preview || preview.ActualWidth < 1 ||
            preview.Tag is not System.Windows.Controls.Expander expander || !preview.IsVisible) return;
        var text = new FormattedText(preview.Text ?? "", CultureInfo.CurrentUICulture, preview.FlowDirection,
            new Typeface(preview.FontFamily, preview.FontStyle, preview.FontWeight, preview.FontStretch),
            preview.FontSize, preview.Foreground, VisualTreeHelper.GetDpi(preview).PixelsPerDip)
        {
            MaxTextWidth = preview.ActualWidth,
            LineHeight = preview.LineHeight
        };
        expander.Visibility = text.Height > preview.MaxHeight + 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notificationDisplayLimit += 50;
        UpdateDetailLists();
    }

    private void MoreApps_Click(object sender, RoutedEventArgs e)
    {
        _appDisplayLimit += 50;
        UpdateDetailLists();
    }

    private void ShowSummaryItems(IReadOnlyList<NotificationSummaryItem> items, bool updateSnapshot = true)
    {
        items = SummaryGroupResolver.Restore(items, _result.SessionId, _result.Notifications);
        items = items.Select(item =>
        {
            if (!string.Equals(item.AppName?.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
                "Discord", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(item.Sender)) return item;
            // 예전 요약에 발신자가 없으면 같은 앱의 원본 차단 알림에서 유일한 출처만 보완한다.
            var senders = _result.Notifications.Where(notification => notification.IsPassed == false
                    && string.Equals(notification.AppName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
                        "Discord", StringComparison.OrdinalIgnoreCase))
                .Select(notification => NotificationSenderResolver.Resolve(notification.AppName, notification.Sender, notification.Title))
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return senders.Length == 1 && !string.IsNullOrWhiteSpace(senders[0]) ? item with { Sender = senders[0] } : item;
        }).ToArray();
        if (updateSnapshot)
        {
            _result = _result with { SummaryItems = items };
            if (DataContext is SessionResult displayed)
                DataContext = displayed with { SummaryItems = items };
        }
        var existing = _summaryCards.Items.ToDictionary(row => row.Summary.SummaryId);
        _summaryCards.SetItems(items.Select(item => existing.TryGetValue(item.SummaryId, out var row) && row.Summary == item
            ? row : new CalendarSummaryItemViewModel(item, _result.SessionId, correction: _result.SessionId is { } session
                ? _summaryState.Get(session, item.SummaryId)?.Correction ?? _calendar.Get(session, item.SummaryId)?.Schedule : null)).ToArray());
        UpdateSavedStates();
        UpdateSummaryContentVisibility();
    }

    private void UpdateSummaryContentVisibility()
    {
        bool hasSummary = _summaryCards.Items.Length > 0;
        CalendarSummaryHost.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;
        LegacySummaryPanel.Visibility = !hasSummary && LegacyHighlightItemsControl.HasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetSummaryStatus(string? message, bool busy = false, bool failed = false)
    {
        var visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        SummaryLoadStatusText.Text = message ?? "";
        SummaryLoadStatusText.Visibility = visibility;
        SummaryStatusPanel.Visibility = visibility;
        SummaryLoadingIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RetrySummaryButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        SummaryStatusPanel.Background = (System.Windows.Media.Brush)FindResource(busy ? "BlackSelection" : "BlackInput");
        SummaryStatusPanel.BorderBrush = (System.Windows.Media.Brush)FindResource(failed ? "BlackError" : busy ? "BlackSelectionRule" : "BlackRule");
        SummaryLoadStatusText.Foreground = (System.Windows.Media.Brush)FindResource(failed ? "BlackError" : busy ? "BlackAccentText" : "BlackMuted");
    }

    private void Calendar_Changed(object? sender, EventArgs e) => UpdateSavedStates();

    private void UpdateSavedStates()
    {
        foreach (var row in _summaryCards.Items)
        {
            var saved = row.SessionId is { } session ? _calendar.Get(session, row.Summary.SummaryId) : null;
            if (row.IsSaved && saved is null) row.Status = "일정 삭제됨 · 다시 저장할 수 있어요.";
            row.IsSaved = saved?.Schedule == row.Draft;
        }
    }

    private void SaveCorrection(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null) return;
        try { _summaryState.Save(new(row.SessionId, row.Summary.SummaryId, row.Draft)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { row.Status = "보완 내용 저장 실패 · 다시 적용하세요."; }
    }

    private void SaveSchedule(object? sender, CalendarSummaryItemViewModel row)
    {
        if (row.SessionId is null || row.IsBusy || row.IsSaved) return;
        row.IsBusy = true;
        try
        {
            _calendar.Save(new(row.SessionId, row.Summary.SummaryId, row.Summary.AppName, row.Draft));
            row.IsSaved = true;
            row.Status = "캘린더에 저장했어요.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { row.Status = _calendar.LoadWarning ?? "일정 저장 실패 · 다시 시도하세요."; }
        finally { row.IsBusy = false; }
    }

    private async void ReloadSummary_Click(object sender, RoutedEventArgs e) => await LoadSummaryItemsAsync();

    private async Task LoadSummaryItemsAsync()
    {
        if (_loadingSummaries || _saving || _closed || _summaryProvider is null || _result.SessionId is null) return;
        _loadingSummaries = true;
        SaveReportButton.IsEnabled = false;
        if (SaveReportButton.Visibility == Visibility.Visible)
        {
            SaveAvailabilityText.Visibility = Visibility.Visible;
            SaveStatusText.Visibility = Visibility.Collapsed;
        }
        ReloadSummaryButton.IsEnabled = false;
        SetSummaryStatus(_summaryCards.Items.Length > 0 ? "AI 요약을 갱신하고 있어요" : "AI가 알림을 요약하고 있어요", busy: true);
        try
        {
            var supply = await _summaryProvider.ReadAsync(_result.SessionId, _summaryLifetime.Token);
            if (_closed) return;
            if (supply.Items.Count > 0)
            {
                var previous = _summaryCards.Items.Select(row => row.Summary).ToArray();
                ShowSummaryItems(supply.Items, updateSnapshot: !supply.IsDemo);
                SetSummaryStatus(null);
                if (IsLoaded && !previous.SequenceEqual(_summaryCards.Items.Select(row => row.Summary)))
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (!_closed && !_loadingSummaries) MotionBehavior.Transition(CalendarSummaryHost);
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else SetSummaryStatus(_summaryCards.Items.Length > 0 ? "새 요약이 없어 기존 요약을 표시합니다."
                : _result.BlockedCount == 0 ? "요약할 알림이 없습니다." : "생성된 AI 요약이 없습니다.");
        }
        catch (OperationCanceledException)
        {
            if (!_closed) SetSummaryStatus("AI 요약이 중단되었습니다.", failed: true);
        }
        catch (Exception)
        {
            if (!_closed) SetSummaryStatus("AI 요약을 불러오지 못했습니다.", failed: true);
        }
        finally
        {
            _loadingSummaries = false;
            if (!_closed)
            {
                SaveAvailabilityText.Visibility = Visibility.Collapsed;
                SaveStatusText.Visibility = Visibility.Visible;
                SaveReportButton.IsEnabled = true;
                ReloadSummaryButton.IsEnabled = true;
            }
        }
    }

    private void FitToWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var available = transform.Transform(new Vector(area.Width, area.Height));
        double width = Math.Max(1, available.X - 24);
        double height = Math.Max(1, available.Y - 24);
        MinWidth = Math.Min(480, width);
        MinHeight = Math.Min(380, height);
        Width = Math.Min(600, width);
        Height = Math.Min(660, height);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_saving || _saveSucceeded) Close();
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _saveSucceeded || _loadingSummaries || _closed || _reports is null) return;
        _saving = true;
        SaveReportButton.IsEnabled = false;
        SkipSaveButton.IsEnabled = false;
        SetSaveLabel("저장 중");
        SaveStatusText.Text = "리포트 저장 중…";
        try
        {
            await Task.Run(() => _reports.SaveAsync(_result));
        }
        catch (Exception)
        {
            _saving = false;
            SaveReportButton.IsEnabled = true;
            SkipSaveButton.IsEnabled = true;
            SetSaveLabel("다시 저장");
            SaveStatusText.Text = "저장 실패 · 다시 시도하세요.";
            return;
        }

        // Persistence is finished. Feedback failure must never be presented as a save failure.
        _saveSucceeded = true;
        try
        {
            SetSaveLabel("저장 완료");
            SaveStatusText.Text = "캘린더에 저장했어요";
            SkipSaveButton.Content = "닫기";
            SkipSaveButton.IsEnabled = true;
            MotionBehavior.SetButtonFeedback(SaveReportButton, false);
            // Keep the completed button non-interactive without dimming its confirmation.
            if (SaveReportButton.Template.FindName("Surface", SaveReportButton) is System.Windows.Controls.Border surface)
                surface.Opacity = 1;
            MotionBehavior.SetBrush(SaveReportButton, System.Windows.Controls.Control.BackgroundProperty, (Brush)FindResource("BlackSuccessSurface"));
            MotionBehavior.SetBrush(SaveReportButton, System.Windows.Controls.Control.BorderBrushProperty, (Brush)FindResource("BlackSuccessRule"));
            MotionBehavior.SetBrush(SaveReportButton, System.Windows.Controls.Control.ForegroundProperty, (Brush)FindResource("BlackSuccess"));
            SaveCompletedIcon.Visibility = Visibility.Visible;
            MotionBehavior.DrawCheck(SaveCompletedIcon);
            MotionBehavior.Acknowledge(SaveReportLabel);
            await Task.Delay(TimeSpan.FromMilliseconds(600), _summaryLifetime.Token);
            await MotionBehavior.FadeOutAsync(ReportSurface, _summaryLifetime.Token);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception)
        {
            // The report is already saved; close normally if its optional feedback cannot run.
        }
        finally
        {
            _saving = false;
            if (!_closed) Close();
        }
    }

    private void SetSaveLabel(string text)
    {
        SaveReportLabel.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(SaveReportButton, text);
    }

    private void CopyQuickReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string draft }) return;
        try
        {
            System.Windows.Clipboard.SetText(draft);
            CopyStatusText.Text = "복사 완료";
        }
        catch (Exception)
        {
            CopyStatusText.Text = "복사 실패 · 다시 시도하세요.";
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
}

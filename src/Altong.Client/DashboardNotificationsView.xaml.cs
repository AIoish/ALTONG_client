using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;

namespace Altong.Client;

public partial class DashboardNotificationsView : System.Windows.Controls.UserControl
{
    public const int PageSize = 30;
    private readonly SqliteDashboardNotificationRepository _repository;
    private readonly HashSet<string> _checkedIds = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DashboardNotificationSnapshot? _snapshot;
    private DashboardNotificationQuery _appliedQuery = new();
    private NotificationHistoryItem[] _rows = [];
    private NotificationHistoryItem? _detail;
    private int _revision;
    private int? _retryPage;
    private bool _ready, _loading, _mutating, _probing, _settingApps, _settingSelection;
    private bool _narrow = true, _detailRequested;
    private double _listOffset;
    public int PageIndex { get; private set; }
    public int TotalCount => _snapshot?.Ids.Count ?? 0;
    internal IReadOnlyList<NotificationHistoryItem> VisibleItems => _rows;

    public DashboardNotificationsView(SqliteDashboardNotificationRepository repository)
    {
        _repository = repository;
        InitializeComponent();
        AppFilter.ItemsSource = new[] { new NotificationAppOption(null, "모든 앱") };
        AppFilter.SelectedIndex = 0;
        _ready = true;
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RefreshAsync(); };
        _pollTimer.Tick += async (_, _) => { if (IsVisible) await CheckForUpdatesAsync(); };
        Loaded += async (_, _) =>
        {
            _pollTimer.Start();
            if (_snapshot is null) await RefreshAsync();
            else await CheckForUpdatesAsync();
        };
        Unloaded += (_, _) =>
        {
            _searchTimer.Stop();
            _pollTimer.Stop();
            _revision++;
            _loading = false;
            UpdateActions();
        };
        UpdateActions();
        UpdateResponsiveLayout();
    }

    private DashboardNotificationQuery ReadQuery() => new(PassedTab.IsChecked == true ? true
        : BlockedTab.IsChecked == true ? false : null, (AppFilter.SelectedItem as NotificationAppOption)?.AppName, SearchBox.Text.Trim());

    public async Task RefreshAsync(bool resetPage = true)
    {
        _searchTimer.Stop();
        var query = ReadQuery();
        bool changedQuery = query != _appliedQuery;
        if (changedQuery)
        {
            _checkedIds.Clear();
            _snapshot = null;
            PageIndex = 0;
            ReplaceRows([]);
            SetDetail(null);
            _detailRequested = false;
            NewArrivalsButton.Visibility = Visibility.Collapsed;
            AllTab.Content = "전체";
            PassedTab.Content = "통과";
            BlockedTab.Content = "차단";
        }
        _retryPage = null;
        int revision = ++_revision;
        int wantedPage = resetPage || changedQuery ? 0 : PageIndex;
        string? selectedId = changedQuery ? null : _detail?.Record.Id;
        _loading = true;
        StatusText.Text = "알림 불러오는 중…";
        RetryButton.Visibility = Visibility.Collapsed;
        if (_rows.Length == 0) ShowEmpty("알림 불러오는 중…", false);
        UpdateActions();
        try
        {
            var data = await Task.Run(async () =>
            {
                var snapshot = await _repository.ReadSnapshotAsync(query);
                int page = Math.Min(wantedPage, Math.Max(0, (snapshot.Ids.Count - 1) / PageSize));
                var records = await _repository.ReadSnapshotPageAsync(snapshot, page, PageSize);
                return (Snapshot: snapshot, Page: page, Records: records);
            });
            if (revision != _revision) return;
            _snapshot = data.Snapshot;
            _appliedQuery = query;
            PageIndex = data.Page;
            _checkedIds.IntersectWith(_snapshot.Ids);
            UpdateAppOptions(_snapshot.Apps, query.AppName);
            AllTab.Content = $"전체 {_snapshot.PassedCount + _snapshot.BlockedCount}";
            PassedTab.Content = $"통과 {_snapshot.PassedCount}";
            BlockedTab.Content = $"차단 {_snapshot.BlockedCount}";
            ApplyRecords(data.Records, selectedId, resetPage || changedQuery);
            NewArrivalsButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "";
        }
        catch (Exception)
        {
            if (revision != _revision) return;
            StatusText.Text = "알림 조회 실패 · 다시 시도하세요.";
            RetryButton.Visibility = Visibility.Visible;
            if (_rows.Length == 0) ShowEmpty("알림을 불러오지 못했습니다.", false);
        }
        finally
        {
            if (revision == _revision) { _loading = false; UpdateActions(); UpdateResponsiveLayout(); }
        }
    }

    public async Task GoToPageAsync(int pageIndex)
    {
        if (_loading || _mutating || _searchTimer.IsEnabled || _snapshot is null || pageIndex < 0 || pageIndex >= PageCount) return;
        int revision = ++_revision;
        var snapshot = _snapshot;
        _loading = true;
        _retryPage = null;
        StatusText.Text = "알림 불러오는 중…";
        RetryButton.Visibility = Visibility.Collapsed;
        UpdateActions();
        try
        {
            var records = await Task.Run(() => _repository.ReadSnapshotPageAsync(snapshot, pageIndex, PageSize));
            if (revision != _revision) return;
            PageIndex = pageIndex;
            ApplyRecords(records, null, true);
            StatusText.Text = "";
        }
        catch (Exception)
        {
            if (revision == _revision)
            {
                _retryPage = pageIndex;
                StatusText.Text = "페이지 조회 실패 · 다시 시도하세요.";
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        finally { if (revision == _revision) { _loading = false; UpdateActions(); UpdateResponsiveLayout(); } }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_snapshot is null || _loading || _mutating || _probing || _searchTimer.IsEnabled) return;
        int revision = _revision;
        var snapshot = _snapshot;
        var query = _appliedQuery;
        _probing = true;
        try
        {
            var latest = await Task.Run(() => _repository.ReadSnapshotAsync(query));
            if (revision != _revision || !ReferenceEquals(snapshot, _snapshot)) return;
            bool changed = !snapshot.Ids.SequenceEqual(latest.Ids) || snapshot.PassedCount != latest.PassedCount || snapshot.BlockedCount != latest.BlockedCount;
            int added = latest.Ids.Except(snapshot.Ids, StringComparer.Ordinal).Count();
            NewArrivalsButton.Content = added > 0 ? $"새 알림 {added}개 확인" : "목록 변경 · 갱신";
            NewArrivalsButton.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            if (revision == _revision) { _retryPage = null; StatusText.Text = "새 알림 확인 실패 · 다시 시도하세요."; RetryButton.Visibility = Visibility.Visible; }
        }
        finally { _probing = false; }
    }

    private void UpdateAppOptions(IReadOnlyList<NotificationAppOption> options, string? appName)
    {
        _settingApps = true;
        try
        {
            var choices = options.Select(option => !string.IsNullOrWhiteSpace(option.AppName)
                    && option.Label.StartsWith(option.AppName, StringComparison.OrdinalIgnoreCase)
                ? option with { Label = Services.AppNameDisplayConverter.Format(option.AppName) + option.Label[option.AppName.Length..] }
                : option).ToList();
            if (appName is not null && !choices.Any(option => string.Equals(option.AppName, appName, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new(appName, Services.AppNameDisplayConverter.Format(appName)));
            AppFilter.ItemsSource = choices;
            AppFilter.SelectedItem = choices.First(option => string.Equals(option.AppName, appName, StringComparison.OrdinalIgnoreCase));
        }
        finally { _settingApps = false; }
    }

    private void ReplaceRows(NotificationHistoryItem[] rows)
    {
        foreach (var row in _rows) row.PropertyChanged -= RowSelectionChanged;
        _rows = rows;
        foreach (var row in rows) row.PropertyChanged += RowSelectionChanged;
        _settingSelection = true;
        try { NotificationsList.ItemsSource = rows; }
        finally { _settingSelection = false; }
    }

    private void ApplyRecords(IReadOnlyList<Altong.Client.Data.Models.NotificationRecord> records, string? selectedId, bool resetScroll)
    {
        double previousOffset = ListScroll()?.VerticalOffset ?? 0;
        ReplaceRows(records.Select(record => new NotificationHistoryItem(record) { IsSelected = _checkedIds.Contains(record.Id) }).ToArray());
        var selected = _rows.FirstOrDefault(row => row.Record.Id == selectedId);
        _settingSelection = true;
        try { NotificationsList.SelectedItem = selected; }
        finally { _settingSelection = false; }
        SetDetail(selected);
        if (selected is null) _detailRequested = false;
        if (_rows.Length == 0)
            ShowEmpty(_snapshot?.Ids.Count > 0 ? "목록이 변경되었습니다. 새로고침해 주세요."
                : _appliedQuery != new DashboardNotificationQuery() ? "조건에 맞는 알림 없음" : "알림 없음", _appliedQuery != new DashboardNotificationQuery());
        else EmptyPanel.Visibility = Visibility.Collapsed;
        if (resetScroll) { ListScroll()?.ScrollToTop(); _listOffset = 0; }
        else ListScroll()?.ScrollToVerticalOffset(previousOffset);
    }

    private void ShowEmpty(string text, bool canReset)
    {
        EmptyStateText.Text = text;
        EmptyPanel.Visibility = Visibility.Visible;
        ResetFiltersButton.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
    }

    private int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    private void UpdateActions()
    {
        bool busy = _loading || _mutating || _searchTimer.IsEnabled;
        NotificationsList.IsEnabled = !busy;
        SelectPageButton.IsEnabled = !busy && _rows.Length > 0;
        SelectPageButton.Content = _rows.Length > 0 && _rows.All(row => row.IsSelected) ? "페이지 선택 해제" : "페이지 전체 선택";
        DismissSelectedButton.IsEnabled = !busy && _checkedIds.Count > 0;
        SelectedCountText.Text = _checkedIds.Count == 0 ? "" : $"선택 {_checkedIds.Count}개";
        DismissNotificationButton.IsEnabled = !busy && _detail is not null;
        CopyNotificationButton.IsEnabled = _detail is not null;
        RefreshButton.IsEnabled = !busy;
        NewArrivalsButton.IsEnabled = !busy;
        PreviousPageButton.IsEnabled = !busy && PageIndex > 0;
        NextPageButton.IsEnabled = !busy && PageIndex + 1 < PageCount;
        PageNumberText.Text = $"{PageIndex + 1} / {PageCount}";
        ResultCountText.Text = TotalCount == 0 ? "0개" : $"{PageIndex * PageSize + 1}–{Math.Min((PageIndex + 1) * PageSize, TotalCount)} / {TotalCount}개";
    }

    private void RowSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not NotificationHistoryItem row || args.PropertyName != nameof(row.IsSelected)) return;
        if (row.IsSelected) _checkedIds.Add(row.Record.Id); else _checkedIds.Remove(row.Record.Id);
        UpdateActions();
    }

    private void SelectPage_Click(object sender, RoutedEventArgs args)
    {
        if (_loading || _mutating) return;
        bool selected = !_rows.All(row => row.IsSelected);
        foreach (var row in _rows) row.IsSelected = selected;
    }

    private async void DismissSelected_Click(object sender, RoutedEventArgs args) => await DismissAsync(_checkedIds.ToArray());
    private async void DismissNotification_Click(object sender, RoutedEventArgs args)
    {
        if (_detail is { } detail) await DismissAsync([detail.Record.Id]);
    }
    internal async Task DismissAsync(string[] ids)
    {
        if (_loading || _mutating || ids.Length == 0) return;
        _mutating = true;
        UpdateActions();
        try
        {
            await Task.Run(() => _repository.DismissAsync(ids));
            _checkedIds.ExceptWith(ids);
            await RefreshAsync(false);
            StatusText.Text = $"알림 {ids.Length}개 제거됨";
        }
        catch (Exception) { StatusText.Text = "제거 실패 · 선택을 유지했습니다. 다시 시도하세요."; }
        finally { _mutating = false; UpdateActions(); }
    }

    private void SetDetail(NotificationHistoryItem? row)
    {
        bool changed = _detail?.Record.Id != row?.Record.Id;
        _detail = row;
        DetailContent.DataContext = row;
        DetailContent.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailEmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (changed)
        {
            DetailScroll.ScrollToTop();
            if (!_narrow) Services.MotionBehavior.Transition(DetailContent, offsetX: 4, offsetY: 0);
        }
        UpdateActions();
    }

    private void Notification_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_settingSelection || !ReferenceEquals(args.Source, NotificationsList)) return;
        SetDetail(NotificationsList.SelectedItem as NotificationHistoryItem);
    }
    private void Notification_MouseUp(object sender, MouseButtonEventArgs args)
    {
        if (!_narrow || _detail is null || _loading) return;
        bool clickedRow = false;
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != NotificationsList; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is CheckBox) return;
            if (node is FrameworkElement { DataContext: NotificationHistoryItem }) clickedRow = true;
        }
        if (clickedRow) OpenCompactDetail();
    }
    private void Notification_KeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key == Key.Enter && _detail is not null && _narrow) { OpenCompactDetail(); args.Handled = true; }
    }
    internal void OpenCompactDetail()
    {
        if (_detail is null) return;
        _listOffset = ListScroll()?.VerticalOffset ?? 0;
        _detailRequested = true;
        UpdateResponsiveLayout();
        Services.MotionBehavior.Transition(DetailPane, offsetX: 4, offsetY: 0);
        BackToListButton.Focus();
    }
    private void BackToList_Click(object sender, RoutedEventArgs args)
    {
        _detailRequested = false;
        UpdateResponsiveLayout();
        UpdateLayout();
        ListScroll()?.ScrollToVerticalOffset(_listOffset);
        Services.MotionBehavior.Transition(ListPane, offsetX: -4, offsetY: 0);
        NotificationsList.Focus();
    }
    private ScrollViewer? ListScroll() => Descendants(NotificationsList).OfType<ScrollViewer>().FirstOrDefault();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private void ReadingLayout_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateResponsiveLayout();
    private void UpdateResponsiveLayout()
    {
        if (ReadingLayout is null) return;
        _narrow = ReadingLayout.ActualWidth < 680;
        bool compactDetail = _narrow && _detailRequested && _detail is not null;
        ReadingLayout.ColumnDefinitions[0].Width = _narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(330);
        ReadingLayout.ColumnDefinitions[1].Width = new GridLength(_narrow ? 0 : 12);
        ReadingLayout.ColumnDefinitions[2].Width = new GridLength(_narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(DetailPane, _narrow ? 0 : 2);
        DetailPane.Visibility = !_narrow || compactDetail ? Visibility.Visible : Visibility.Collapsed;
        ListPane.Visibility = compactDetail ? Visibility.Collapsed : Visibility.Visible;
        BackToListButton.Visibility = compactDetail ? Visibility.Visible : Visibility.Collapsed;
        PaginationPanel.Visibility = compactDetail ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void StatusFilter_Changed(object sender, RoutedEventArgs args) { if (_ready) await RefreshAsync(); }
    private async void AppFilter_Changed(object sender, SelectionChangedEventArgs args) { if (_ready && !_settingApps) await RefreshAsync(); }
    private void Search_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (!_ready) return;
        _searchTimer.Stop();
        _searchTimer.Start();
        StatusText.Text = "검색 중…";
        UpdateActions();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();
    private async void Retry_Click(object sender, RoutedEventArgs args)
    {
        if (_retryPage is { } page) await GoToPageAsync(page);
        else await RefreshAsync(false);
    }
    private async void PreviousPage_Click(object sender, RoutedEventArgs args) => await GoToPageAsync(PageIndex - 1);
    private async void NextPage_Click(object sender, RoutedEventArgs args) => await GoToPageAsync(PageIndex + 1);
    private async void ResetFilters_Click(object sender, RoutedEventArgs args)
    {
        _ready = false;
        AllTab.IsChecked = true;
        SearchBox.Text = "";
        AppFilter.SelectedIndex = 0;
        _ready = true;
        await RefreshAsync();
    }
    private void CopyNotification_Click(object sender, RoutedEventArgs args)
    {
        if (_detail is null) return;
        try
        {
            Clipboard.SetText($"{_detail.Record.AppName}\n{_detail.SenderText}\n{_detail.Record.Title}\n\n{_detail.Record.Body}");
            StatusText.Text = "내용 복사됨";
            Services.MotionBehavior.Acknowledge(StatusText);
        }
        catch (Exception) { StatusText.Text = "복사 실패 · 다시 시도하세요."; }
    }
}

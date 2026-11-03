using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Altong.Client.Models;
using Altong.Client.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace Altong.Client;

/// <summary>
/// 상단 노치에서 아래로 늘어나는 알림 패널의 내용(목록·상세·하단 버튼).
/// 바탕과 테두리는 <see cref="NotificationDockWindow"/>가 노치와 한 도형으로 그린다.
/// 모든 상태는 <see cref="DockNotificationState"/>에 있으며, 이 컨트롤은 그 상태를 보여 주고 조작만 전달한다.
/// </summary>
public partial class NotificationPanel : System.Windows.Controls.UserControl
{
    /// <summary>목록 영역 폭. 노치가 열리면 노치 바닥이 이 폭까지 넓어진다.</summary>
    public const double ListWidth = TopNotchLayout.PanelWidth;
    /// <summary>상세 영역 폭. 목록 왼쪽에 붙는다.</summary>
    public const double DetailWidth = TopNotchLayout.DetailWidth;
    private readonly DockNotificationState _state;
    private DockNotificationItem? _displayedDetail;
    private TopBandStyle _bandStyle;

    public event EventHandler? ShowDashboardRequested;
    public event EventHandler<TopBandStyle>? BandStyleRequested;
    /// <summary>[임시] 노치 크기 비교 버튼을 눌렀을 때.</summary>
    public event EventHandler<NotchSize>? NotchSizeRequested;

    public NotificationPanel(DockNotificationState state)
    {
        InitializeComponent();
        _state = state;
        NotificationItems.ItemsSource = state.Items;
        BuildNotchSizeButtons();
        Refresh();
    }

    public TopBandStyle BandStyle => _bandStyle;
    public bool HasDetail => _state.SelectedItem is not null;
    public double PanelWidth => ListWidth + (HasDetail ? DetailWidth : 0);

    /// <summary>상단 띠 색 선택을 표시한다.</summary>
    public void SetBandStyle(TopBandStyle style)
    {
        _bandStyle = style;
        foreach (var button in new[] { BandBlackButton, BandWallpaperButton })
            MarkSelected(button, button.Tag is TopBandStyle option && option == style);
    }

    private void MarkSelected(Button button, bool selected)
    {
        button.Background = selected ? (System.Windows.Media.Brush)FindResource("Accent") : Brushes.Transparent;
        button.Foreground = selected ? Brushes.Black : (System.Windows.Media.Brush)FindResource("MutedText");
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "선택됨" : "선택 안 됨");
    }

    /// <summary>[임시] 노치 크기 후보마다 비교 버튼을 만든다.</summary>
    private void BuildNotchSizeButtons()
    {
        foreach (var size in TopNotchLayout.SizeOptions)
        {
            var button = new Button
            {
                Content = size.Label,
                Tag = size,
                Width = 36,
                Height = 26,
                Margin = new Thickness(1),
                FontSize = 11,
                Style = (Style)FindResource("HeaderButton"),
                ToolTip = $"높이 {size.Height:0}, 폭 {size.Width:0}",
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"노치 높이 {size.Label}");
            button.Click += (_, _) => NotchSizeRequested?.Invoke(this, size);
            NotchSizeButtons.Children.Add(button);
        }
        SetNotchSize(TopNotchLayout.Size);
    }

    /// <summary>[임시] 지금 노치 크기 버튼을 선택 표시한다.</summary>
    public void SetNotchSize(NotchSize size)
    {
        foreach (Button button in NotchSizeButtons.Children)
            MarkSelected(button, button.Tag is NotchSize option && option == size);
    }

    private void BandStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TopBandStyle style })
            BandStyleRequested?.Invoke(this, style);
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowDashboardRequested?.Invoke(this, EventArgs.Empty);

    public void Refresh()
    {
        CountText.Text = $"미확인 {_state.UnreadCount}개 · 전체 {_state.Items.Count}개";
        MarkAllReadButton.IsEnabled = _state.UnreadCount > 0;
        DeleteReadButton.IsEnabled = _state.Items.Any(item => item.IsRead);
        ListPinButton.IsChecked = DetailPinButton.IsChecked = _state.IsPinned;
        string pinHint = _state.IsPinned ? "고정 해제" : "목록과 상세 함께 고정";
        ListPinButton.ToolTip = DetailPinButton.ToolTip = pinHint;
        EmptyState.Visibility = _state.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = _state.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        bool hasDetail = HasDetail;
        bool openingDetail = hasDetail && DetailPanel.Visibility != Visibility.Visible;
        DetailColumn.Width = new GridLength(hasDetail ? DetailWidth : 0);
        DetailPanel.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        Width = PanelWidth;
        if (_displayedDetail != _state.SelectedItem)
        {
            _displayedDetail = _state.SelectedItem;
            DetailContent.DataContext = _displayedDetail;
            DetailScroll.ScrollToTop();
        }
        // 새 알림 수신·읽음 갱신 시에는 재생하지 않고 상세를 펼칠 때만 강조한다.
        if (openingDetail && IsVisible) AnimateEntrance(DetailPanel);
        else if (!hasDetail) ResetEntrance(DetailPanel);
    }

    /// <summary>상세가 나타날 때 한 번만 옆에서 들어온다.</summary>
    private static void AnimateEntrance(FrameworkElement panel)
    {
        ResetEntrance(panel);
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        }.AtDisplayRate(), HandoffBehavior.SnapshotAndReplace);
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(6, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            }.AtDisplayRate(), HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetEntrance(FrameworkElement panel)
    {
        panel.BeginAnimation(OpacityProperty, null);
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
    }

    /// <summary>새 카드 삽입 전 보이던 카드의 화면 위치를 유지한다.</summary>
    public void PreserveScrollPosition(Action update)
    {
        DockNotificationItem? anchor = null;
        double oldY = 0;
        if (IsVisible && ListScroll.VerticalOffset > 0)
        {
            UpdateLayout();
            foreach (var item in _state.Items)
            {
                if (NotificationItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement element)
                    continue;
                double y = element.TranslatePoint(new System.Windows.Point(), ListScroll).Y;
                if (y + element.ActualHeight > 0)
                {
                    anchor = item;
                    oldY = y;
                    break;
                }
            }
        }
        update();
        if (anchor is null || !IsVisible) return;
        UpdateLayout();
        if (NotificationItems.ItemContainerGenerator.ContainerFromItem(anchor) is FrameworkElement current)
        {
            double newY = current.TranslatePoint(new System.Windows.Point(), ListScroll).Y;
            ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset + newY - oldY);
        }
    }

    private void Notification_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DockNotificationItem item }) _state.OpenDetail(item.Id);
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => _state.TogglePin();
    private void MarkAllRead_Click(object sender, RoutedEventArgs e) =>
        PreserveScrollPosition(_state.MarkAllRead);
    private void DeleteRead_Click(object sender, RoutedEventArgs e) => _state.DeleteRead();
    private void CloseDetail_Click(object sender, RoutedEventArgs e) => _state.CloseDetail();
    private void CloseList_Click(object sender, RoutedEventArgs e) => _state.ClosePanel();
}

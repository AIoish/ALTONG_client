using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Altong.Client.Models;
using Altong.Client.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace Altong.Client;

public partial class NotificationFlyoutWindow : Window
{
    public const double ListWidth = 332;
    public const double DetailWidth = 372;
    public const double NotchListWidth = 332;
    private readonly DockNotificationState _state;
    private DockNotificationItem? _displayedDetail;
    private HwndSource? _source;
    private DockPresentation _presentation;

    public event EventHandler<DockPresentation>? SwitchPresentationRequested;
    public event EventHandler? ShowDashboardRequested;

    public NotificationFlyoutWindow(DockNotificationState state)
    {
        InitializeComponent();
        _state = state;
        NotificationItems.ItemsSource = state.Items;
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnVisibilityChanged;
        Closed += OnClosed;
        Refresh();
    }

    public void SetPresentation(DockPresentation presentation)
    {
        _presentation = presentation;
        bool notch = presentation != DockPresentation.Classic;
        ListColumn.Width = new GridLength((notch ? NotchListWidth : ListWidth) - 16);
        foreach (var button in new[] { NotchDesignButton, ClassicDesignButton })
        {
            bool selected = button.Tag is DockPresentation option && option == presentation;
            button.Background = selected ? (System.Windows.Media.Brush)FindResource("Accent") : Brushes.Transparent;
            button.Foreground = selected ? Brushes.Black : (System.Windows.Media.Brush)FindResource("MutedText");
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "선택됨" : "선택 안 됨");
        }
        Refresh();
    }

    private void SwitchPresentation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DockPresentation presentation })
            SwitchPresentationRequested?.Invoke(this, presentation);
    }
    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowDashboardRequested?.Invoke(this, EventArgs.Empty);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
    }

    private static nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // 클릭해 읽어도 작업 중인 앱의 활성 창 맥락을 바꾸지 않는다.
        if (msg == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        return 0;
    }

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

        bool hasDetail = _state.SelectedItem is not null;
        bool openingDetail = hasDetail && DetailPanel.Visibility != Visibility.Visible;
        DetailColumn.Width = new GridLength(hasDetail ? DetailWidth : 0);
        DetailPanel.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        Width = (_presentation != DockPresentation.Classic ? NotchListWidth : ListWidth) + (hasDetail ? DetailWidth : 0);
        if (_displayedDetail != _state.SelectedItem)
        {
            _displayedDetail = _state.SelectedItem;
            DetailContent.DataContext = _displayedDetail;
            DetailScroll.ScrollToTop();
        }
        // 새 알림 수신·읽음 갱신 시에는 재생하지 않고 패널을 펼칠 때만 강조한다.
        if (openingDetail && IsVisible) AnimateEntrance(DetailPanel);
        else if (!hasDetail) ResetEntrance(DetailPanel);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            AnimateEntrance(ListPanel);
            if (DetailPanel.Visibility == Visibility.Visible) AnimateEntrance(DetailPanel);
        }
        else
        {
            ResetEntrance(ListPanel);
            ResetEntrance(DetailPanel);
        }
    }

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
        }, HandoffBehavior.SnapshotAndReplace);
        // 창 크기나 배치는 애니메이션하지 않아 커서 이동과 스크롤 기준을 유지한다.
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(6, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            }, HandoffBehavior.SnapshotAndReplace);
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

    private void OnClosed(object? sender, EventArgs e)
    {
        _source?.RemoveHook(WindowProc);
        ResetEntrance(ListPanel);
        ResetEntrance(DetailPanel);
        SourceInitialized -= OnSourceInitialized;
        IsVisibleChanged -= OnVisibilityChanged;
        Closed -= OnClosed;
    }
}

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
    private TopBandStyle _bandStyle;
    // 상단 노치에서는 패널을 노치와 같은 검정으로 칠하고, 테두리는 노치 윤곽선과 같은 은색을 쓴다.
    private static readonly SolidColorBrush NotchBlack = Frozen(System.Windows.Media.Colors.Black);
    private static readonly SolidColorBrush NotchEdge = Frozen(System.Windows.Media.Color.FromArgb(0x8C, 0xC4, 0xC8, 0xD0));

    private static SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public event EventHandler<DockPresentation>? SwitchPresentationRequested;
    public event EventHandler<TopBandStyle>? BandStyleRequested;
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
        foreach (var button in new[] { NotchDesignButton, ClassicDesignButton, StealthDesignButton, TopNotchDesignButton })
            MarkSelected(button, button.Tag is DockPresentation option && option == presentation);
        BandStyleBar.Visibility = presentation == DockPresentation.TopNotch ? Visibility.Visible : Visibility.Collapsed;
        ApplyNotchSurface();
        Refresh();
    }

    /// <summary>상단 노치의 띠 색 선택을 표시한다.</summary>
    public void SetBandStyle(TopBandStyle style)
    {
        _bandStyle = style;
        foreach (var button in new[] { BandBlackButton, BandWallpaperButton })
            MarkSelected(button, button.Tag is TopBandStyle option && option == style);
    }

    public TopBandStyle BandStyle => _bandStyle;

    private void MarkSelected(Button button, bool selected)
    {
        button.Background = selected ? (System.Windows.Media.Brush)FindResource("Accent") : Brushes.Transparent;
        button.Foreground = selected ? Brushes.Black : (System.Windows.Media.Brush)FindResource("MutedText");
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "선택됨" : "선택 안 됨");
    }

    private void BandStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TopBandStyle style })
            BandStyleRequested?.Invoke(this, style);
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
        ApplyNotchSurface();
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
            if (_presentation == DockPresentation.TopNotch)
            {
                // 노치가 아래로 늘어나듯 패널을 위에서부터 드러낸다.
                RevealFromTop();
                return;
            }
            AnimateEntrance(ListPanel);
            if (DetailPanel.Visibility == Visibility.Visible) AnimateEntrance(DetailPanel);
        }
        else
        {
            PanelRoot.Clip = null;
            PanelRoot.CacheMode = null;
            ResetEntrance(ListPanel);
            ResetEntrance(DetailPanel);
        }
    }

    /// <summary>
    /// 상단 노치 모양. 목록 패널은 위쪽 테두리·모서리 없이 넓어진 노치 바닥에 붙고 아래쪽 모서리만 둥글다.
    /// 상세는 목록 왼쪽에 틈 없이 같은 검정으로 붙는다. 다른 디자인으로 바꾸면 기본 카드 모양으로 돌린다.
    /// </summary>
    private void ApplyNotchSurface()
    {
        if (_presentation != DockPresentation.TopNotch)
        {
            foreach (var panel in new[] { ListPanel, DetailPanel })
            {
                panel.ClearValue(Border.BackgroundProperty);
                panel.ClearValue(Border.BorderBrushProperty);
                panel.ClearValue(Border.BorderThicknessProperty);
                panel.ClearValue(Border.CornerRadiusProperty);
            }
            DetailPanel.Margin = new Thickness(0, 0, 12, 0);
            return;
        }
        bool hasDetail = _state.SelectedItem is not null;
        ListPanel.Background = NotchBlack;
        ListPanel.BorderBrush = NotchEdge;
        ListPanel.BorderThickness = new Thickness(1, 0, 1, 1);
        ListPanel.CornerRadius = new CornerRadius(0, 0, 16, hasDetail ? 0 : 16);
        DetailPanel.Background = NotchBlack;
        DetailPanel.BorderBrush = NotchEdge;
        DetailPanel.BorderThickness = new Thickness(1, 1, 0, 1);
        DetailPanel.CornerRadius = new CornerRadius(16, 0, 0, 16);
        DetailPanel.Margin = new Thickness(0);
    }

    /// <summary>노치가 넓어진 뒤 패널을 위에서 아래로 한 번 펼친다. Windows 애니메이션 효과가 꺼져 있으면 바로 보인다.</summary>
    private void RevealFromTop()
    {
        PanelRoot.Clip = null;
        if (!SystemParameters.ClientAreaAnimation) return;
        double width = double.IsNaN(Width) ? ActualWidth : Width;
        double height = double.IsNaN(Height) ? ActualHeight : Height;
        var clip = new RectangleGeometry(new Rect(0, 0, width, 0));
        PanelRoot.Clip = clip;
        // 노치 바닥이 패널 폭까지 넓어지는 동안에는 가려 두었다가, 거의 다 넓어진 뒤 그 바닥에서 이어서 내려온다.
        // 좁은 노치 아래에 넓은 패널이 먼저 보이면 끊겨 보이기 때문이다.
        // 펼치는 동안에는 패널 내용을 한 번 그린 비트맵으로 고정해 매 프레임 글자·카드를 다시 그리지 않는다.
        // 화면 배율에 맞춰 그려 흐려지지 않게 하고, 다 펼친 뒤에는 원래대로 돌린다.
        var dpi = VisualTreeHelper.GetDpi(this);
        PanelRoot.CacheMode = new BitmapCache(Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)) { SnapsToDevicePixels = true };
        var reveal = new RectAnimation(new Rect(0, 0, width, 0), new Rect(0, 0, width, height),
            TimeSpan.FromMilliseconds(TopNotchLayout.PanelRevealMilliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(TopNotchLayout.PanelRevealDelayMilliseconds),
            // 처음에 빠르게 내려오고 끝에서 길게 감속해 휴대폰 앱처럼 부드럽게 멈춘다.
            EasingFunction = new PowerEase { Power = 4, EasingMode = EasingMode.EaseOut },
        }.AtDisplayRate();
        reveal.Completed += (_, _) =>
        {
            if (PanelRoot.Clip == clip) PanelRoot.Clip = null;
            PanelRoot.CacheMode = null;
        };
        clip.BeginAnimation(RectangleGeometry.RectProperty, reveal);
    }

    /// <summary>패널이 나타날 때 한 번만 움직인다.</summary>
    private static void AnimateEntrance(FrameworkElement panel, bool fromTop = false)
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
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(
            fromTop ? TranslateTransform.YProperty : TranslateTransform.XProperty,
            new DoubleAnimation(fromTop ? -6 : 6, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void ResetEntrance(FrameworkElement panel)
    {
        panel.BeginAnimation(OpacityProperty, null);
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
        ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
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

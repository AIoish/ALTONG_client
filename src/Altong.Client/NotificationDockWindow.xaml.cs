using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Altong.Client;

/// <summary>
/// 화면 맨 위 띠 가운데의 상단 노치. 집중 중 통과한 알림을 표시등으로 알리고, 커서를 올리면 노치가 아래로 늘어나
/// 알림 패널이 된다. 노치와 패널을 한 창·한 윤곽 도형으로 그려 펼치는 동안 두 부분이 어긋나지 않는다.
/// 띠(<see cref="TopBandWindow"/>)는 미니바가 보이는 동안만 예약한다.
/// </summary>
public partial class NotificationDockWindow : Window
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Func<string>? _statusProvider;
    private readonly DispatcherTimer _reminderDismissTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _hideAfterReminder;
    private bool _reminderPending;
    private readonly DockNotificationState _notifications;
    private readonly NotificationPanel _panel;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isClosed;
    private HwndSource? _windowSource;
    private Point _pressScreenPoint;
    private double _pressNotchCenter;
    private bool _isPressed;
    private bool _isDragging;
    private TopBandWindow? _topBand;
    // 상단 띠는 바탕화면 배경색으로 시작한다. 패널 하단에서 검정으로 바꿀 수 있다.
    private TopBandStyle _topBandStyle = TopBandStyle.Wallpaper;
    private double _notchCenterOffset;
    private bool _fullscreenSuppressed;
    private DateTime _topNotchActivatedAt = DateTime.MinValue;
    private DateTime _lastAlertRippleAt = DateTime.MinValue;
    private bool? _lensAlert;
    private double? _openTarget;
    private bool _panelShown;
    private bool _detailShown;
    private double _panelHeight = TopNotchLayout.PanelMaxHeight;
    /// <summary>창 안에서 노치 가운데의 x(DIP).</summary>
    private double _centerX = TopNotchLayout.WindowWidth / 2;

    /// <summary>
    /// 패널 열림 진행값(0 닫힘 ~ 1 열림). 앞부분에서 노치 바닥이 패널 폭까지 넓어지고, 이어서 패널이 내려온다
    /// (<see cref="TopNotchLayout.NotchMorph"/>, <see cref="TopNotchLayout.PanelReveal"/>). 값이 바뀔 때마다 노치 모양을 다시 그린다.
    /// </summary>
    public static readonly DependencyProperty TopNotchOpenProgressProperty = DependencyProperty.Register(
        nameof(TopNotchOpenProgress), typeof(double), typeof(NotificationDockWindow),
        new PropertyMetadata(0d, (d, _) => ((NotificationDockWindow)d).UpdateShape()));

    public double TopNotchOpenProgress
    {
        get => (double)GetValue(TopNotchOpenProgressProperty);
        set => SetValue(TopNotchOpenProgressProperty, value);
    }

    public Func<DockRoutineStatus>? RoutineStatusProvider { get; set; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>
    /// 화면 맨 위 띠를 AppBar로 예약할지. 앱(App)만 켠다. 기본값이 꺼져 있어 테스트·도구에서 창을 띄워도
    /// 사용자의 화면 배치나 다른 창을 바꾸지 않는다.
    /// </summary>
    public bool ReserveTopBand { get; set; }
    /// <summary>상단 띠 색. 실행 중에는 유지한다.</summary>
    public TopBandStyle BandStyle => _topBandStyle;
    /// <summary>전체화면 동안 노치·패널·안내를 숨긴 상태인지.</summary>
    public bool IsFullscreenSuppressed => _fullscreenSuppressed;
    /// <summary>알림 패널 내용. 테스트와 접근성 검사에서 쓴다.</summary>
    public NotificationPanel Panel => _panel;
    public bool IsPanelShown => _panelShown;
    public bool IsReminderVisible => ReminderBubble.Visibility == Visibility.Visible;

    public event EventHandler? ShowDashboardRequested;

    public NotificationDockWindow() : this(new DockNotificationState()) { }

    public NotificationDockWindow(DockNotificationState notifications)
    {
        _notifications = notifications;
        InitializeComponent();
        _panel = new NotificationPanel(notifications);
        _panel.SetBandStyle(_topBandStyle);
        _panel.ShowDashboardRequested += Panel_ShowDashboardRequested;
        _panel.BandStyleRequested += Panel_BandStyleRequested;
        _panel.MouseEnter += Pointer_Changed;
        _panel.MouseLeave += Pointer_Changed;
        PanelHost.Content = _panel;
        SourceInitialized += Dock_SourceInitialized;
        _statusTimer.Tick += StatusTimer_Tick;
        _reminderDismissTimer.Tick += ReminderDismissTimer_Tick;
        _hoverTimer.Tick += HoverTimer_Tick;
        _leaveTimer.Tick += LeaveTimer_Tick;
        _notifications.Changed += Notifications_Changed;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        Closed += NotificationDockWindow_Closed;
        IsVisibleChanged += Dock_IsVisibleChanged;
        ApplyLayout();
        UpdateNotificationVisuals();
    }

    private void Panel_ShowDashboardRequested(object? sender, EventArgs e) => ShowDashboardRequested?.Invoke(this, e);

    private void Panel_BandStyleRequested(object? sender, TopBandStyle style) => SetTopBandStyle(style);

    public void BeginNotificationSession(string sessionId) => _notifications.BeginSession(sessionId);

    public void EndNotificationSession()
    {
        StopHoverTimers();
        _notifications.EndSession();
    }

    public void ReceiveNotification(NotificationRecord notification)
    {
        if (_isClosed) return;
        bool added = false;
        if (_panelShown && IsVisible) _panel.PreserveScrollPosition(() => added = _notifications.Receive(notification));
        else added = _notifications.Receive(notification);
        // 목록에는 필터를 통과한 알림만 들어온다. 새로 들어왔으면 노치에 선셋 일렁임을 한 번 흘려 알린다.
        if (added) PlayAlertRipple();
    }

    // ───────────────────────────── 위치·모양 ─────────────────────────────

    public void PositionOnPrimaryWorkArea() => ApplyLayout();

    /// <summary>
    /// 노치 가운데를 기준으로 창 위치·크기와 창 안 배치를 맞춘다. 창의 왼쪽 위는 노치 위치로만 정해지고,
    /// 패널이 보이는 동안에는 창을 아래로만 늘려 크기가 바뀌는 순간에도 노치가 흔들리지 않는다.
    /// </summary>
    private void ApplyLayout()
    {
        bool detail = _panelShown && _panel.HasDetail;
        double center = TopNotchLayout.NotchCenter(0, SystemParameters.PrimaryScreenWidth, _notchCenterOffset);
        _centerX = TopNotchLayout.WindowLeftExtent;
        Left = center - TopNotchLayout.WindowLeftExtent;
        Top = 0;
        Width = TopNotchLayout.WindowLeftExtent + TopNotchLayout.WindowRightExtent;
        Height = TopNotchLayout.WindowHeightFor(_panelShown, _panelHeight);
        Canvas.SetLeft(NotchFace, _centerX - TopNotchLayout.NotchSurfaceWidth / 2);
        Canvas.SetTop(NotchFace, 0);
        Canvas.SetLeft(ReminderBubble, _centerX - TopNotchLayout.ReminderWidth / 2);
        Canvas.SetTop(ReminderBubble, TopNotchLayout.ReminderTop);
        Canvas.SetLeft(PanelHost, _centerX - TopNotchLayout.PanelWidth / 2 - (detail ? TopNotchLayout.DetailWidth : 0));
        Canvas.SetTop(PanelHost, TopNotchLayout.NotchHeight);
        PanelHost.Width = _panel.PanelWidth;
        PanelHost.Height = _panelHeight;
        TopNotchClickScale.CenterX = _centerX;
        UpdateShape();
    }

    /// <summary>현재 열림 진행값에 맞춰 노치·패널 실루엣과 윤곽선, 패널이 드러난 높이를 다시 만든다.</summary>
    private void UpdateShape()
    {
        double progress = TopNotchOpenProgress;
        double morph = TopNotchLayout.NotchMorph(progress);
        double reveal = _panelShown ? TopNotchLayout.PanelReveal(progress) * _panelHeight : 0;
        var body = BuildBody(morph, reveal, filled: true);
        NotchBody.Data = body;
        TopNotchOutline.Data = BuildBody(morph, reveal, filled: false);
        TopNotchOutline.Clip = body;
        var visible = new RectangleGeometry(new Rect(0, 0, _panel.PanelWidth, Math.Max(0, reveal)));
        visible.Freeze();
        PanelHost.Clip = visible;
    }

    /// <summary>
    /// 노치와(드러난 만큼의) 패널을 하나의 도형으로 만든다. 노치 옆선은 열림 정도에 따라 S자에서 수직으로 끝나는 곡선으로 바뀌어
    /// 패널 옆면과 꺾임 없이 이어진다. 상세가 열리면 목록 왼쪽으로 넓어지고 왼쪽 위 모서리를 둥글게 한다.
    /// <paramref name="filled"/>가 false면 화면 끝에 닿는 위쪽 변을 뺀 윤곽선만 만든다.
    /// </summary>
    private Geometry BuildBody(double morph, double reveal, bool filled)
    {
        double width = TopNotchLayout.NotchSurfaceWidth;
        double x0 = _centerX - width / 2;
        double top = TopNotchLayout.NotchHeight;
        var side = TopNotchLayout.NotchLeftSide(morph);
        Point L(int i) => new(x0 + side[i].X, side[i].Y);
        Point R(int i) => new(x0 + width - side[i].X, side[i].Y);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(L(0), filled, filled);
            context.BezierTo(L(1), L(2), L(3), true, true);
            context.BezierTo(L(4), L(5), L(6), true, true);
            if (reveal < .5)
            {
                context.LineTo(R(6), true, true);
            }
            else
            {
                double listLeft = _centerX - TopNotchLayout.PanelWidth / 2;
                double listRight = _centerX + TopNotchLayout.PanelWidth / 2;
                double bottom = top + reveal;
                double radius = Math.Min(TopNotchLayout.PanelCornerRadius, reveal / 2);
                var corner = new System.Windows.Size(radius, radius);
                bool detail = _panel.HasDetail;
                double leftEdge = detail ? listLeft - TopNotchLayout.DetailWidth : listLeft;
                if (detail)
                {
                    context.LineTo(new Point(leftEdge + radius, top), true, true);
                    context.ArcTo(new Point(leftEdge, top + radius), corner, 0, false, SweepDirection.Counterclockwise, true, true);
                }
                else
                {
                    context.LineTo(new Point(listLeft, top), true, true);
                }
                context.LineTo(new Point(leftEdge, bottom - radius), true, true);
                context.ArcTo(new Point(leftEdge + radius, bottom), corner, 0, false, SweepDirection.Counterclockwise, true, true);
                context.LineTo(new Point(listRight - radius, bottom), true, true);
                context.ArcTo(new Point(listRight, bottom - radius), corner, 0, false, SweepDirection.Counterclockwise, true, true);
                context.LineTo(new Point(listRight, top), true, true);
                context.LineTo(R(6), true, true);
            }
            context.BezierTo(R(5), R(4), R(3), true, true);
            context.BezierTo(R(2), R(1), R(0), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    // ───────────────────────────── 패널 열기·닫기 ─────────────────────────────

    /// <summary>
    /// 패널 상태에 맞춰 노치를 열거나 닫는다. 열 때는 창을 패널 크기로 키운 뒤 하나의 진행값으로
    /// 노치가 넓어지고 패널이 내려오게 하고, 닫을 때는 반대로 패널이 노치 안으로 말려 올라간 뒤 창을 줄인다.
    /// </summary>
    private void UpdatePanelOpen()
    {
        bool open = _notifications.IsPanelOpen && IsVisible && !_fullscreenSuppressed;
        double target = open ? 1 : 0;
        if (open && !_panelShown) ShowPanel();
        if (_openTarget == target) return;
        _openTarget = target;
        if (!SystemParameters.ClientAreaAnimation || !IsVisible)
        {
            BeginAnimation(TopNotchOpenProgressProperty, null);
            TopNotchOpenProgress = target;
            FinishPanelAnimation();
            return;
        }
        // 진행값은 일정한 속도로 움직이고, 감속은 노치·패널 단계별 곡선(TopNotchLayout)이 맡는다.
        var animation = new DoubleAnimation(target,
            TimeSpan.FromMilliseconds(open ? TopNotchLayout.OpenMilliseconds : TopNotchLayout.CloseMilliseconds)).AtDisplayRate();
        animation.Completed += (_, _) => FinishPanelAnimation();
        BeginAnimation(TopNotchOpenProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ShowPanel()
    {
        _panelShown = true;
        _detailShown = _panel.HasDetail;
        _panelHeight = Math.Clamp(SystemParameters.WorkArea.Bottom - TopNotchLayout.NotchHeight - TopNotchLayout.PanelBottomMargin,
            120, TopNotchLayout.PanelMaxHeight);
        _panel.Refresh();
        PanelHost.Visibility = Visibility.Visible;
        // 펼치는 동안에는 패널 내용을 한 번 그린 비트맵으로 고정해 매 프레임 글자·카드를 다시 그리지 않는다.
        if (SystemParameters.ClientAreaAnimation)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _panel.CacheMode = new BitmapCache(Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)) { SnapsToDevicePixels = true };
        }
        ApplyLayout();
    }

    private void FinishPanelAnimation()
    {
        _panel.CacheMode = null;
        if (_isClosed || _openTarget != 0 || !_panelShown) return;
        _panelShown = false;
        PanelHost.Visibility = Visibility.Collapsed;
        ApplyLayout();
    }

    // ───────────────────────────── 띠·전체화면 ─────────────────────────────

    /// <summary>
    /// 미니바가 보이는 동안만 띠를 AppBar로 예약한다. 집중 모드를 끄면 띠를 해제해 화면 위쪽 공간을 돌려준다.
    /// </summary>
    private void UpdateTopBand()
    {
        bool wanted = !_isClosed && IsVisible && ReserveTopBand;
        if (wanted)
        {
            if (_topBand is not null) return;
            _topBand = new TopBandWindow(_topBandStyle);
            _topBand.FullscreenChanged += TopBand_FullscreenChanged;
            // 띠가 처음 자리 잡을 때부터 노치 아래에 두도록 기준 창을 먼저 알려 준다.
            _topBand.PlaceBelow(new WindowInteropHelper(this).Handle);
            _topBand.Show();
            RaiseAboveBand();
            return;
        }
        if (_topBand is not null)
        {
            _topBand.FullscreenChanged -= TopBand_FullscreenChanged;
            _topBand.Close();
            _topBand = null;
        }
        SetFullscreenSuppressed(false);
    }

    /// <summary>띠와 노치는 모두 맨 위 창이다. 노치를 다시 올리고 띠를 그 바로 아래에 둔다.</summary>
    private void RaiseAboveBand()
    {
        if (!IsVisible || _fullscreenSuppressed) return;
        Topmost = false;
        Topmost = true;
        _topBand?.PlaceBelow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>띠 색을 바꾼다. 실행 중에는 같은 색을 유지한다.</summary>
    public void SetTopBandStyle(TopBandStyle style)
    {
        if (_isClosed) return;
        _topBandStyle = style;
        if (_topBand is not null) _topBand.BandStyle = style;
        _panel.SetBandStyle(style);
    }

    private void TopBand_FullscreenChanged(object? sender, bool fullscreen) => SetFullscreenSuppressed(fullscreen);

    /// <summary>
    /// 전체화면 동안 노치·패널·안내를 모두 숨긴다. 알림 수집과 읽음·고정·상세 상태는 그대로 두고,
    /// 전체화면이 끝나면 열려 있던 패널과 보류한 안내를 다시 보여 준다.
    /// 창을 숨기면 패널 상태가 닫히므로 창은 투명하게만 만든다. 완전히 투명한 창의 클릭은 아래 앱으로 전달된다.
    /// </summary>
    private void SetFullscreenSuppressed(bool suppressed)
    {
        if (_fullscreenSuppressed == suppressed) return;
        _fullscreenSuppressed = suppressed;
        Opacity = suppressed ? 0 : 1;
        IsHitTestVisible = !suppressed;
        StopHoverTimers();
        if (suppressed)
        {
            if (IsReminderVisible)
            {
                _reminderDismissTimer.Stop();
                _reminderPending = true;
                ReminderBubble.Visibility = Visibility.Collapsed;
            }
            UpdatePanelOpen();
            return;
        }
        RaiseAboveBand();
        UpdatePanelOpen();
        if (!_notifications.IsPanelOpen) ShowPendingReminder();
        UpdatePointerState();
    }

    // ───────────────────────────── 안내 말풍선 ─────────────────────────────

    public void ShowRoutineReminder(string title, Func<string> statusProvider, string message)
    {
        var phase = RoutineStatusProvider?.Invoke().Phase;
        // 앱에서는 집중 종료 안내 말풍선을 띄우지 않는다. 이어서 앱이 미니바를 숨기면 노치와 띠가 바로 사라진다.
        if (phase == FocusRoutinePhase.Idle)
        {
            _reminderDismissTimer.Stop();
            _reminderPending = false;
            _hideAfterReminder = false;
            ReminderBubble.Visibility = Visibility.Collapsed;
            return;
        }
        // 집중을 켜는 순간(노치가 나타나는 순간)은 말풍선 대신 노치 안에 색 물결(일렁임)을 한 번 흘려 알린다.
        // 휴식 시작 등 이후 안내는 노치 아래 말풍선으로 보인다.
        if (RoutineStatusProvider is not null
            && (!IsVisible || Clock() - _topNotchActivatedAt < TimeSpan.FromSeconds(2)))
        {
            _reminderDismissTimer.Stop();
            _hideAfterReminder = false;
            _reminderPending = false;
            _statusProvider = statusProvider;
            ReminderBubble.Visibility = Visibility.Collapsed;
            RefreshReminderStatus();
            ApplyLayout();
            bool appearing = !IsVisible;
            if (appearing)
            {
                Show();
                PlayActivationAnimation();
            }
            _statusTimer.Start();
            return;
        }

        // 루틴 전환이 사용자가 읽거나 고정한 알림 패널을 닫지 않도록 안내를 보류한다.
        _reminderDismissTimer.Stop();
        _hideAfterReminder = false;
        _statusProvider = statusProvider;
        ReminderTitleText.Text = title;
        ReminderMessageText.Text = message;
        RefreshReminderStatus();
        _reminderPending = _notifications.IsPanelOpen || _notifications.IsDragging || _fullscreenSuppressed;
        ReminderBubble.Visibility = _reminderPending ? Visibility.Collapsed : Visibility.Visible;
        ApplyLayout();
        if (!IsVisible) Show();
        RaiseAboveBand();
        PlayActivationAnimation();
        _statusTimer.Start();
        if (!_reminderPending) _reminderDismissTimer.Start();
    }

    public void KeepVisible()
    {
        _hideAfterReminder = false;
        ApplyLayout();
        if (!IsVisible) Show();
    }

    public void HideAfterCurrentReminder()
    {
        if (IsReminderVisible)
        {
            _hideAfterReminder = true;
            return;
        }
        Hide();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e) => RefreshReminderStatus();

    private void RefreshReminderStatus()
    {
        if (_statusProvider is not null)
            ReminderStatusText.Text = _statusProvider();
    }

    private void ReminderDismissTimer_Tick(object? sender, EventArgs e) => HideReminderBubble();

    private void HideReminderBubble()
    {
        _reminderDismissTimer.Stop();
        if (!IsVisible) _statusTimer.Stop();
        _reminderPending = false;
        ReminderBubble.Visibility = Visibility.Collapsed;
        if (_hideAfterReminder)
        {
            _hideAfterReminder = false;
            Hide();
        }
    }

    /// <summary>패널을 읽는 동안 보류한 안내를 패널이 닫힌 뒤 보여 준다. 실제로 보인 때부터 표시 시간을 센다.</summary>
    private void ShowPendingReminder()
    {
        if (!_reminderPending || _notifications.IsDragging || _fullscreenSuppressed) return;
        _reminderPending = false;
        ReminderBubble.Visibility = Visibility.Visible;
        _reminderDismissTimer.Start();
    }

    private void CloseReminder_Click(object sender, RoutedEventArgs e)
    {
        HideReminderBubble();
        e.Handled = true;
    }

    // ───────────────────────────── 효과 ─────────────────────────────

    /// <summary>
    /// 켜짐 효과(일렁임). 청록·라벤더 색 물결이 노치 안을 왼쪽에서 오른쪽으로 약 3.5초에 한 번 천천히 흐르고,
    /// 끝나면 원래 검은 노치로 돌아온다. 반복하지 않으며 Windows 애니메이션 효과가 꺼져 있으면 생략한다.
    /// 휴식 시작 등 안내 말풍선을 띄울 때도 같은 효과를 쓴다.
    /// </summary>
    public void PlayActivationAnimation()
    {
        _topNotchActivatedAt = Clock();
        PlayRipple(TopNotchLayout.ActivationRipple, TopNotchLayout.ActivationRippleMilliseconds);
    }

    /// <summary>
    /// 통과한 알림이 왔을 때의 일렁임(선셋). 약 1.8초에 빠르게 한 번 흐른다. 패널을 열어 읽는 중이거나 전체화면이면 생략하고,
    /// 알림이 연달아 오면 5초 안에는 다시 재생하지 않는다.
    /// </summary>
    private void PlayAlertRipple()
    {
        var now = Clock();
        if (!TopNotchLayout.ShouldPlayAlertRipple(now, _lastAlertRippleAt, IsVisible, _notifications.IsPanelOpen, _fullscreenSuppressed))
            return;
        _lastAlertRippleAt = now;
        PlayRipple(TopNotchLayout.AlertRipple, TopNotchLayout.AlertRippleMilliseconds);
    }

    /// <summary>주어진 색으로 일렁임을 한 번 재생한다. 진행 중인 일렁임은 새 것으로 바꾼다.</summary>
    private void PlayRipple((string Left, string Middle, string Right) colors, int milliseconds)
    {
        TopNotchRipple.BeginAnimation(OpacityProperty, null);
        TopNotchRippleMove.BeginAnimation(TranslateTransform.XProperty, null);
        TopNotchRipple.Opacity = 0;
        if (!SystemParameters.ClientAreaAnimation || _fullscreenSuppressed) return;
        SetRippleColors(colors);

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        TopNotchRippleMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-1, 1, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }.AtDisplayRate());
        var fade = new DoubleAnimationUsingKeyFrames { Duration = duration };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(.15)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(.8)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        TopNotchRipple.BeginAnimation(OpacityProperty, fade.AtDisplayRate());
    }

    /// <summary>일렁임 색을 바꾼다. 양 끝은 투명, 가운데 세 색은 불투명도 약 44%다.</summary>
    private void SetRippleColors((string Left, string Middle, string Right) colors)
    {
        static Color Tint(string hex, byte alpha)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            color.A = alpha;
            return color;
        }
        RippleStopStart.Color = Tint(colors.Left, 0);
        RippleStopLeft.Color = Tint(colors.Left, 0x70);
        RippleStopMiddle.Color = Tint(colors.Middle, 0x70);
        RippleStopRight.Color = Tint(colors.Right, 0x70);
        RippleStopEnd.Color = Tint(colors.Right, 0);
    }

    private void PlayClickFeedback()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var press = new DoubleAnimation(1, .97, TimeSpan.FromMilliseconds(75))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }.AtDisplayRate();
        TopNotchClickScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        TopNotchClickScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
    }

    /// <summary>
    /// 커서가 노치 위에 있거나 알림 패널이 열린 동안 은색 윤곽선을 보인다.
    /// 나타날 때 0.12초, 사라질 때 0.16초에 걸쳐 한 번만 바뀐다.
    /// </summary>
    private void UpdateTopNotchOutline()
    {
        bool show = !_fullscreenSuppressed && (NotchBody.IsMouseOver || _notifications.IsPanelOpen);
        double target = show ? TopNotchLayout.OutlineOpacity : 0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            TopNotchOutline.BeginAnimation(OpacityProperty, null);
            TopNotchOutline.Opacity = target;
            return;
        }
        TopNotchOutline.BeginAnimation(OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(show ? 120 : 160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            }.AtDisplayRate(), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>미확인 알림이 있는 동안 렌즈를 주황으로 키우고, 모두 읽으면 은빛으로 돌린다. 상태가 바뀔 때만 움직인다.</summary>
    private void UpdateNotificationVisuals()
    {
        bool unread = _notifications.UnreadCount > 0;
        System.Windows.Automation.AutomationProperties.SetItemStatus(NotchBody,
            unread ? $"미확인 중요 알림 {_notifications.UnreadCount}개" : "미확인 알림 없음");
        if (_lensAlert == unread) return;
        bool first = _lensAlert is null;
        _lensAlert = unread;
        var (colors, scale) = TopNotchLayout.Lens(unread);
        bool animate = !first && SystemParameters.ClientAreaAnimation;
        var duration = TimeSpan.FromMilliseconds(200);
        void Paint(GradientStop stop, string hex)
        {
            var target = (Color)ColorConverter.ConvertFromString(hex);
            if (!animate)
            {
                stop.BeginAnimation(GradientStop.ColorProperty, null);
                stop.Color = target;
                return;
            }
            stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(target, duration).AtDisplayRate(),
                HandoffBehavior.SnapshotAndReplace);
        }
        void Grow(DependencyProperty property)
        {
            if (!animate)
            {
                TopNotchLensScale.BeginAnimation(property, null);
                TopNotchLensScale.SetValue(property, scale);
                return;
            }
            TopNotchLensScale.BeginAnimation(property, new DoubleAnimation(scale, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            }.AtDisplayRate(), HandoffBehavior.SnapshotAndReplace);
        }
        Paint(LensHighlight, colors.Highlight);
        Paint(LensBody, colors.Body);
        Paint(LensEdge, colors.Edge);
        Grow(ScaleTransform.ScaleXProperty);
        Grow(ScaleTransform.ScaleYProperty);
    }

    /// <summary>렌즈가 미확인 알림 상태(주황)인지. 테스트에서 쓴다.</summary>
    public bool IsLensAlert => _lensAlert == true;

    // ───────────────────────────── 포인터 ─────────────────────────────

    /// <summary>커서가 노치 부분(띠 높이 안)에 있는지. 열린 패널 부분은 포함하지 않는다.</summary>
    private bool IsPointerOverNotch =>
        NotchBody.IsMouseOver && Mouse.GetPosition(Root).Y <= TopNotchLayout.NotchHeight;

    /// <summary>커서가 노치 또는 열린 패널 위에 있는지. 한 창이라 둘 사이를 오가도 벗어난 것으로 보지 않는다.</summary>
    private bool IsPointerOverSurface => NotchBody.IsMouseOver || (_panelShown && _panel.IsMouseOver);

    private void Pointer_Changed(object sender, MouseEventArgs e) => UpdatePointerState();

    private void UpdatePointerState()
    {
        if (_isClosed) return;
        UpdateTopNotchOutline();
        if (!_notifications.IsActive || _isPressed || _notifications.IsDragging || !IsVisible || _fullscreenSuppressed)
        {
            StopHoverTimers();
            return;
        }
        bool open = _notifications.IsPanelOpen;
        if (open ? IsPointerOverSurface : IsPointerOverNotch)
        {
            _leaveTimer.Stop();
            if (!open && !_hoverTimer.IsEnabled) _hoverTimer.Start();
        }
        else
        {
            _hoverTimer.Stop();
            if (open && !_notifications.IsPinned && !_leaveTimer.IsEnabled) _leaveTimer.Start();
        }
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_isClosed || !IsVisible || !IsPointerOverNotch || _isPressed || !_notifications.IsActive
            || _fullscreenSuppressed) return;
        HideReminderBubble();
        _notifications.OpenPanel();
    }

    private void LeaveTimer_Tick(object? sender, EventArgs e)
    {
        if (_notifications.IsPinned || !_notifications.IsPanelOpen)
        {
            _leaveTimer.Stop();
            return;
        }
        // 빈 곳에서는 새 MouseLeave가 오지 않으므로 타이머로 계속 위치를 확인한다.
        if (IsPointerOverSurface) return;
        _leaveTimer.Stop();
        _notifications.ClosePanel();
    }

    private void StopHoverTimers()
    {
        _hoverTimer.Stop();
        _leaveTimer.Stop();
    }

    private void Notch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 열린 패널의 빈 가장자리는 노치가 아니므로 클릭·끌기로 쓰지 않는다.
        if (e.GetPosition(Root).Y > TopNotchLayout.NotchHeight || !NotchBody.CaptureMouse()) return;
        _pressScreenPoint = PointToScreen(e.GetPosition(this));
        _pressNotchCenter = TopNotchLayout.NotchCenter(0, SystemParameters.PrimaryScreenWidth, _notchCenterOffset);
        _isPressed = true;
        _isDragging = false;
        _hoverTimer.Stop();
        e.Handled = true;
    }

    private void Notch_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPressed)
        {
            UpdatePointerState();
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPointerInteraction();
            return;
        }
        var delta = GetPointerDelta(e);
        if (!_isDragging)
        {
            _isDragging = TopNotchLayout.IsDrag(delta.X, delta.Y,
                SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
            if (_isDragging)
            {
                StopHoverTimers();
                _notifications.BeginDrag();
            }
        }
        if (!_isDragging) return;
        MoveNotch(delta.X);
        e.Handled = true;
    }

    private void Notch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPressed) return;
        // 마지막 MouseMove가 오지 않은 상태에서 놓아도 끌기를 클릭으로 오인하지 않는다.
        var delta = GetPointerDelta(e);
        bool dragged = _isDragging || TopNotchLayout.IsDrag(delta.X, delta.Y,
            SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
        if (dragged)
        {
            if (!_notifications.IsDragging) _notifications.BeginDrag();
            MoveNotch(delta.X);
        }
        CancelPointerInteraction();
        if (!dragged)
        {
            PlayClickFeedback();
            ShowDashboardRequested?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    /// <summary>노치를 띠 안에서 좌우로 옮긴다. 옮긴 거리는 실행 중에만 유지한다.</summary>
    private void MoveNotch(double deltaX)
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double center = TopNotchLayout.NotchCenter(0, screenWidth,
            TopNotchLayout.CenterOffset(_pressNotchCenter + deltaX, 0, screenWidth));
        _notchCenterOffset = TopNotchLayout.CenterOffset(center, 0, screenWidth);
        ApplyLayout();
    }

    private Vector GetPointerDelta(MouseEventArgs e)
    {
        // 화면 픽셀로 얻은 마우스 이동량을 WPF 창 좌표(DIP)로 바꾼다.
        var deviceDelta = PointToScreen(e.GetPosition(this)) - _pressScreenPoint;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform?.Transform(deviceDelta) ?? deviceDelta;
    }

    private void Notch_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isPressed = false;
        _isDragging = false;
        _notifications.EndDrag();
        UpdatePointerState();
    }

    private void CancelPointerInteraction()
    {
        _isPressed = false;
        _isDragging = false;
        if (NotchBody.IsMouseCaptured) NotchBody.ReleaseMouseCapture();
        _notifications.EndDrag();
        UpdatePointerState();
    }

    // ───────────────────────────── 상태 변화 ─────────────────────────────

    private void Notifications_Changed(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        _panel.Refresh();
        UpdateNotificationVisuals();
        if (_notifications.IsPanelOpen && IsVisible && !_fullscreenSuppressed)
        {
            // 패널을 읽는 동안에는 안내를 보류한다.
            if (IsReminderVisible)
            {
                _reminderDismissTimer.Stop();
                _reminderPending = true;
                ReminderBubble.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ShowPendingReminder();
        }
        if (_panelShown && _panel.HasDetail != _detailShown)
        {
            _detailShown = _panel.HasDetail;
            ApplyLayout();
        }
        UpdatePanelOpen();
        UpdateTopNotchOutline();
        // 핀 해제 시 커서가 이미 바깥이라면 지연 닫기를 시작한다.
        if (_notifications.IsPanelOpen) UpdatePointerState();
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(SystemParameters.WorkArea)) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => { if (!_isClosed && !_isPressed) ApplyLayout(); });
            return;
        }
        if (!_isClosed && !_isPressed) ApplyLayout();
    }

    /// <summary>모니터 연결·해제, 해상도·주사율 변경.</summary>
    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        DisplayRefresh.Invalidate();
        if (_isClosed) return;
        _ = Dispatcher.BeginInvoke(() => { if (!_isClosed && !_isPressed) ApplyLayout(); });
    }

    private void Dock_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(Dock_WindowProc);
    }

    private static nint Dock_WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // 클릭 순간 노치가 활성 창을 가로채지 않아 작업 중인 앱과 대시보드의 활성 상태를 유지한다.
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        return 0;
    }

    private void Dock_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _statusTimer.Stop();
            StopHoverTimers();
            _notifications.ClosePanel();
        }
        else
        {
            RefreshReminderStatus();
            _statusTimer.Start();
        }
        UpdateTopBand();
        UpdatePanelOpen();
    }

    private void NotificationDockWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        UpdateTopBand();
        BeginAnimation(TopNotchOpenProgressProperty, null);
        _windowSource?.RemoveHook(Dock_WindowProc);
        _windowSource = null;
        SourceInitialized -= Dock_SourceInitialized;
        _reminderDismissTimer.Stop();
        _reminderDismissTimer.Tick -= ReminderDismissTimer_Tick;
        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimer_Tick;
        StopHoverTimers();
        _hoverTimer.Tick -= HoverTimer_Tick;
        _leaveTimer.Tick -= LeaveTimer_Tick;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _notifications.Changed -= Notifications_Changed;
        _panel.ShowDashboardRequested -= Panel_ShowDashboardRequested;
        _panel.BandStyleRequested -= Panel_BandStyleRequested;
        _panel.MouseEnter -= Pointer_Changed;
        _panel.MouseLeave -= Pointer_Changed;
        IsVisibleChanged -= Dock_IsVisibleChanged;
        Closed -= NotificationDockWindow_Closed;
    }
}

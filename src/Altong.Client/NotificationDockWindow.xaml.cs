using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Altong.Client.Data.Models;
using Altong.Client.Services;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Altong.Client;

public partial class NotificationDockWindow : Window
{
    private const double RightMargin = 16;
    private const double VerticalPositionRatio = 0.6;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };
    private Func<string>? _statusProvider;
    private readonly DispatcherTimer _reminderDismissTimer = new()
    {
        Interval = TimeSpan.FromMinutes(1),
    };
    private bool _hideAfterReminder;
    private bool _reminderPending;
    private readonly DockPositionStore _positionStore = new();
    private Point _pressScreenPoint;
    private double _pressTop;
    private double _pressLeft;
    private double _capsuleRightInset = 24;
    private bool _isPressed;
    private bool _isDragging;
    private readonly DockNotificationState _notifications;
    private NotificationFlyoutWindow? _notificationFlyout;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isClosed;
    private bool _backgroundEngaged;
    private HwndSource? _windowSource;
    private const double StealthWindowWidth = 720;
    private const double StealthWindowHeight = 280;
    private bool _stealthLayoutFrozen;
    private bool _stealthSettlePending;
    private const double StealthRestingOpacity = .85;
    private readonly StealthDockPositionStore _stealthPositionStore;
    private readonly StealthDockBehavior _stealth = new();
    private readonly DispatcherTimer _stealthTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _stealthRevealTimer = new() { Interval = StealthDockTiming.Default.RevealDwell };
    private Point _stealthAnchor;
    private Point _pressStealthAnchor;
    private Rect _stealthWorkArea;
    private string? _stealthDeviceName;
    private bool _stealthAnchorInitialized;
    private Point _stealthLocalAnchor;
    private StealthPlacement? _stealthPlaced;
    private static readonly TimeSpan StealthMorphDuration = TimeSpan.FromMilliseconds(180);
    private TopBandWindow? _topBand;
    // 상단 띠는 바탕화면 배경색으로 시작한다. 패널 하단에서 검정으로 바꿀 수 있다.
    private TopBandStyle _topBandStyle = TopBandStyle.Wallpaper;
    private double _topNotchCenterOffset;
    private bool _fullscreenSuppressed;
    private DateTime _topNotchActivatedAt = DateTime.MinValue;
    private double? _topNotchOpenTarget;

    /// <summary>상단 노치가 알림 패널 폭까지 넓어진 정도(0 닫힘 ~ 1 열림). 애니메이션 중 노치 모양을 다시 그린다.</summary>
    public static readonly DependencyProperty TopNotchOpenProgressProperty = DependencyProperty.Register(
        nameof(TopNotchOpenProgress), typeof(double), typeof(NotificationDockWindow),
        new PropertyMetadata(0d, (d, _) => ((NotificationDockWindow)d).UpdateTopNotchGeometry()));

    public double TopNotchOpenProgress
    {
        get => (double)GetValue(TopNotchOpenProgressProperty);
        set => SetValue(TopNotchOpenProgressProperty, value);
    }

    /// <summary>은신형 몸체·상태 점·내용의 목표 배치(창 안 DIP).</summary>
    private readonly record struct StealthPlacement(
        double BodyLeft, double BodyTop, double BodyWidth, double BodyHeight,
        double DotSize, double DotOpacity, double ContentOpacity);
    public DockPresentation Presentation { get; private set; }
    public Func<DockRoutineStatus>? RoutineStatusProvider { get; set; }
    /// <summary>마지막 입력 이후 경과 시간. 자리를 비운 동안에는 새 알림 표시 시간을 세지 않는다.</summary>
    public Func<TimeSpan> UserIdleProvider { get; set; } = UserInputIdle.GetIdleTime;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public StealthDockMode StealthMode => _stealth.Mode;
    /// <summary>은신형 미니바의 기준점(상태 점 중심, 화면 DIP).</summary>
    public Point StealthAnchor => _stealthAnchor;
    /// <summary>상단 노치 디자인의 띠 색. 디자인을 바꿔도 실행 중에는 유지한다.</summary>
    public TopBandStyle BandStyle => _topBandStyle;
    /// <summary>전체화면 동안 상단 노치·패널·안내를 숨긴 상태인지.</summary>
    public bool IsFullscreenSuppressed => _fullscreenSuppressed;
    private FrameworkElement ActiveSurface => Presentation switch
    {
        DockPresentation.Classic => DockSurface,
        DockPresentation.Stealth => StealthSurface,
        DockPresentation.TopNotch => TopNotchSurface,
        _ => NotchSurface,
    };

    public NotificationDockWindow() : this(new DockNotificationState()) { }

    public NotificationDockWindow(DockNotificationState notifications)
        : this(notifications, new StealthDockPositionStore()) { }

    public NotificationDockWindow(DockNotificationState notifications, StealthDockPositionStore stealthPositionStore)
    {
        _notifications = notifications;
        _stealthPositionStore = stealthPositionStore;
        InitializeComponent();
        SourceInitialized += Dock_SourceInitialized;
        _statusTimer.Tick += (_, _) => RefreshReminderStatus();
        _reminderDismissTimer.Tick += ReminderDismissTimer_Tick;
        _hoverTimer.Tick += HoverTimer_Tick;
        _leaveTimer.Tick += LeaveTimer_Tick;
        _stealthTimer.Tick += StealthTimer_Tick;
        _stealthRevealTimer.Tick += StealthRevealTimer_Tick;
        _notifications.Changed += Notifications_Changed;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        Closed += NotificationDockWindow_Closed;
        IsVisibleChanged += Dock_IsVisibleChanged;
        LocationChanged += Dock_LocationChanged;
        DpiChanged += Dock_DpiChanged;
        SetPresentation(DockPresentation.RightNotch);
    }

    public event EventHandler? ShowDashboardRequested;

    public void SetPresentation(DockPresentation presentation)
    {
        if (_isClosed || Presentation == presentation) return;
        // 표시만 바꾼다. 세션, 읽음, 상세 선택, 고정 상태는 같은 객체에 유지한다.
        CancelPointerInteraction();
        Presentation = presentation;
        bool classic = presentation == DockPresentation.Classic;
        bool stealth = presentation == DockPresentation.Stealth;
        bool top = presentation == DockPresentation.TopNotch;
        DockSurface.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        NotchSurface.Visibility = presentation == DockPresentation.RightNotch ? Visibility.Visible : Visibility.Collapsed;
        StealthSurface.Visibility = stealth ? Visibility.Visible : Visibility.Collapsed;
        TopNotchSurface.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        // 은신형은 동그라미를 넓은 투명 창의 한가운데에 두고 창을 통째로 평행 이동한다.
        // 창 안 배치를 다시 그리지 않고 위치를 바꿀 수 있어 잔상이 남지 않고, 빛·말풍선도 창 경계에 잘리지 않는다.
        // 투명한 부분은 클릭이 아래 앱으로 그대로 전달된다.
        Width = stealth ? StealthWindowWidth : top ? TopNotchLayout.WindowWidth : 360;
        Height = classic ? 136 : stealth ? StealthWindowHeight : top ? TopNotchLayout.WindowHeight : 168;
        if (top) ConfigureTopNotchReminderLayout();
        else ConfigureReminderLayout(!classic);
        // 상단 노치일 때만 화면 맨 위 띠를 예약한다. 다른 디자인으로 바꾸면 바로 해제한다.
        UpdateTopBand();
        // 전환 직후에는 어디에 있는지 알 수 있도록 잠시 펼친 뒤 은신한다.
        if (stealth) _stealth.Reveal(Clock());
        _stealthPlaced = null;
        _notificationFlyout?.SetPresentation(presentation);
        PositionOnPrimaryWorkArea();
        UpdateLayout();
        PositionNotificationFlyout();
        RefreshReminderStatus();
        UpdateNotificationVisuals();
        UpdateBackground();
        UpdateTopNotchOpen();
        UpdateTopNotchGeometry();
        UpdateTopNotchOutline();
        UpdatePointerState();
        UpdateStealthTimer();
    }

    private void ConfigureReminderLayout(bool horizontal)
    {
        Grid.SetColumnSpan(ReminderBubble, horizontal ? 2 : 1);
        ReminderBubble.Margin = horizontal ? new Thickness(8, 8, 8, 38) : new Thickness(8, 12, 2, 12);
        ReminderCard.Margin = horizontal ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 10, 0);
        ReminderArrow.Points = horizontal
            ? new PointCollection { new(0, 0), new(18, 0), new(9, 10) }
            : new PointCollection { new(0, 0), new(12, 9), new(0, 18) };
        ReminderArrow.VerticalAlignment = horizontal ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        ReminderArrow.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        // 오른쪽에 놓인 가로 미니바의 중심(창 오른쪽에서 56)에 화살표 끝을 맞춘다.
        ReminderArrow.Margin = horizontal ? new Thickness(0, 0, 38, 0) : new Thickness(0);
    }

    /// <summary>상단 노치의 안내 말풍선은 노치 바로 아래에 두고 위쪽 화살표가 노치 가운데를 가리킨다.</summary>
    private void ConfigureTopNotchReminderLayout()
    {
        Grid.SetColumnSpan(ReminderBubble, 2);
        ReminderBubble.Margin = new Thickness(8, TopNotchLayout.NotchHeight + 4, 8, 8);
        ReminderCard.Margin = new Thickness(0, 9, 0, 0);
        ReminderArrow.Points = new PointCollection { new(0, 10), new(18, 10), new(9, 0) };
        ReminderArrow.VerticalAlignment = VerticalAlignment.Top;
        ReminderArrow.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        ReminderArrow.Margin = new Thickness(0);
    }

    /// <summary>은신형 안내 말풍선은 공간이 넓은 쪽(위·아래)에 두고 화살표 끝을 기준점에 맞춘다.</summary>
    private void ConfigureStealthReminderLayout(bool above, double dotX, double dotY)
    {
        const double gap = 2;
        const double arrowWidth = 18;
        const double bubbleWidth = 344;
        const double bubbleHeight = 122;
        const double edge = 8;
        double capsuleHalf = StealthDockPositioning.HalfHeight;
        // 창은 화면 밖으로 일부 나갈 수 있으므로, 말풍선은 화면에 보이는 부분 안에서 상태 점 가까이에 둔다.
        var work = _stealthWorkArea;
        double visibleLeft = Math.Max(0, work.Left - Left);
        double visibleRight = Math.Min(Width, work.Right - Left);
        double left = Math.Clamp(dotX - bubbleWidth / 2, visibleLeft + edge,
            Math.Max(visibleLeft + edge, visibleRight - edge - bubbleWidth));
        double top = above ? dotY - capsuleHalf - gap - bubbleHeight : dotY + capsuleHalf + gap;
        Grid.SetColumnSpan(ReminderBubble, 2);
        ReminderBubble.Margin = new Thickness(left, Math.Max(0, top),
            Math.Max(0, Width - left - bubbleWidth), Math.Max(0, Height - top - bubbleHeight));
        ReminderCard.Margin = above ? new Thickness(0, 0, 0, 9) : new Thickness(0, 9, 0, 0);
        ReminderArrow.Points = above
            ? new PointCollection { new(0, 0), new(arrowWidth, 0), new(arrowWidth / 2, 10) }
            : new PointCollection { new(0, 10), new(arrowWidth, 10), new(arrowWidth / 2, 0) };
        ReminderArrow.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        ReminderArrow.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        // 말풍선의 둥근 모서리 밖으로 화살표가 나가지 않게 제한한다.
        double arrowLeft = dotX - left - arrowWidth / 2;
        ReminderArrow.Margin = new Thickness(
            Math.Clamp(arrowLeft, 12, Math.Max(12, bubbleWidth - arrowWidth - 12)), 0, 0, 0);
    }

    private void SwitchPresentationRequested(object? sender, DockPresentation presentation) => SetPresentation(presentation);

    private void Flyout_ShowDashboardRequested(object? sender, EventArgs e) => ShowDashboardRequested?.Invoke(this, e);

    private void Flyout_BandStyleRequested(object? sender, TopBandStyle style) => SetTopBandStyle(style);


    public bool IsReminderVisible => ReminderBubble.Visibility == Visibility.Visible;

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
        if (_notificationFlyout is { IsVisible: true } flyout)
            flyout.PreserveScrollPosition(() => added = _notifications.Receive(notification));
        else
            added = _notifications.Receive(notification);
        if (added) PlayNotificationPulse();
    }

    public void PositionOnPrimaryWorkArea()
    {
        if (Presentation == DockPresentation.Stealth)
        {
            PositionStealth();
            return;
        }
        if (Presentation == DockPresentation.TopNotch)
        {
            PositionTopNotch();
            return;
        }
        var workArea = SystemParameters.WorkArea;
        if (Presentation != DockPresentation.Classic)
        {
            Left = workArea.Right - Width - _capsuleRightInset;
            Top = workArea.Bottom - Height;
            return;
        }
        Left = workArea.Right - Width - RightMargin;
        Top = _positionStore.VerticalRatio is { } ratio
            ? DockPositioning.FromRatio(ratio, workArea.Top, workArea.Height, Height)
            : DockPositioning.ClampTop(
                workArea.Top + (workArea.Height * VerticalPositionRatio) - (Height / 2),
                workArea.Top, workArea.Height, Height);
    }

    /// <summary>
    /// 은신형은 모니터별 작업 영역 안에서 자유롭게 놓인다. 작업 영역·배율이 바뀌면 같은 비율 위치로 옮긴다.
    /// </summary>
    private void PositionStealth()
    {
        if (!_stealthAnchorInitialized)
        {
            var saved = _stealthPositionStore.Settings;
            var screen = FindScreen(saved?.DeviceName);
            _stealthDeviceName = screen?.DeviceName;
            _stealthWorkArea = GetWorkArea(screen);
            _stealthAnchor = saved is not null
                ? StealthDockPositioning.FromRatio(new Point(saved.RatioX, saved.RatioY), _stealthWorkArea)
                : StealthDockPositioning.DefaultAnchor(_stealthWorkArea);
            _stealthAnchorInitialized = true;
        }
        else
        {
            // 연결이 끊긴 모니터에 있었다면 주 모니터의 같은 비율 위치로 돌아온다.
            var screen = FindScreen(_stealthDeviceName);
            var workArea = GetWorkArea(screen);
            if (screen?.DeviceName != _stealthDeviceName || workArea != _stealthWorkArea)
            {
                var ratio = StealthDockPositioning.ToRatio(_stealthAnchor, _stealthWorkArea);
                _stealthDeviceName = screen?.DeviceName;
                _stealthWorkArea = workArea;
                _stealthAnchor = StealthDockPositioning.FromRatio(ratio, _stealthWorkArea);
            }
        }
        ApplyStealthLayout();
    }

    /// <summary>주 모니터 맨 위 가운데(띠 안)에 노치를 둔다. 좌우로 옮긴 거리는 실행 중에만 유지한다.</summary>
    private void PositionTopNotch()
    {
        Left = TopNotchLayout.WindowLeft(0, SystemParameters.PrimaryScreenWidth, Width, _topNotchCenterOffset);
        Top = 0;
    }

    private void MoveTopNotch(double deltaX)
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = TopNotchLayout.WindowLeft(0, screenWidth, Width,
            TopNotchLayout.CenterOffset(_pressLeft + deltaX, 0, screenWidth, Width));
        Top = 0;
        _topNotchCenterOffset = TopNotchLayout.CenterOffset(Left, 0, screenWidth, Width);
    }

    /// <summary>
    /// 상단 노치 디자인이고 미니바가 보이는 동안만 띠를 AppBar로 예약한다.
    /// 집중 모드를 끄거나 다른 디자인으로 바꾸면 띠를 해제해 화면 위쪽 공간을 돌려준다.
    /// </summary>
    private void UpdateTopBand()
    {
        bool wanted = !_isClosed && IsVisible && Presentation == DockPresentation.TopNotch;
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

    /// <summary>띠와 노치는 모두 맨 위 창이다. 노치를 다시 올려 띠 위에 그려지게 한다.</summary>
    private void RaiseAboveBand()
    {
        if (!IsVisible || _fullscreenSuppressed) return;
        Topmost = false;
        Topmost = true;
        _topBand?.PlaceBelow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>띠 색을 바꾼다. 디자인을 바꿔도 실행 중에는 같은 색을 유지한다.</summary>
    public void SetTopBandStyle(TopBandStyle style)
    {
        if (_isClosed) return;
        _topBandStyle = style;
        if (_topBand is not null) _topBand.BandStyle = style;
        _notificationFlyout?.SetBandStyle(style);
    }

    private void TopBand_FullscreenChanged(object? sender, bool fullscreen) => SetFullscreenSuppressed(fullscreen);

    /// <summary>
    /// 전체화면 동안 노치·패널·안내를 모두 숨긴다. 알림 수집과 읽음·고정·상세 상태는 그대로 두고,
    /// 전체화면이 끝나면 열려 있던 패널과 보류한 안내를 다시 보여 준다.
    /// 창을 숨기면 패널 상태가 닫히므로 창은 투명하게만 만든다. 완전히 투명한 부분의 클릭은 아래 앱으로 전달된다.
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
            _notificationFlyout?.Hide();
            if (IsReminderVisible)
            {
                _reminderDismissTimer.Stop();
                _reminderPending = true;
                ReminderBubble.Visibility = Visibility.Collapsed;
            }
            return;
        }
        RaiseAboveBand();
        UpdateTopNotchOpen();
        if (_notifications.IsPanelOpen && IsVisible && _notificationFlyout is not null)
        {
            PositionNotificationFlyout();
            _notificationFlyout.Show();
        }
        else if (_reminderPending && !_notifications.IsDragging && IsVisible)
        {
            _reminderPending = false;
            ReminderBubble.Visibility = Visibility.Visible;
            _reminderDismissTimer.Start();
        }
        UpdatePointerState();
    }

    private static System.Windows.Forms.Screen? FindScreen(string? deviceName) =>
        System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == deviceName)
        ?? System.Windows.Forms.Screen.PrimaryScreen;

    /// <summary>모니터 작업 영역(물리 픽셀)을 이 창의 DIP 좌표로 바꾼다. 주 모니터는 기존 미니바와 같은 값을 쓴다.</summary>
    private Rect GetWorkArea(System.Windows.Forms.Screen? screen)
    {
        if (screen is null || screen.Primary) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    /// <summary>
    /// 창을 작업 영역 안에 두고, 그 안에서 점·캡슐·안내의 위치를 기준점에 맞춘다.
    /// 창의 나머지 부분은 투명해 아래 앱의 클릭을 막지 않는다.
    /// </summary>
    /// <summary>
    /// 기준점이 창 한가운데에 오도록 창을 한 번에 옮기고, 그 안에서 점·캡슐·안내를 배치한다.
    /// 끄는 동안과 놓은 직후에는 창만 옮기고 안쪽 배치는 화면이 한 번 갱신된 뒤에 바꾼다(<see cref="ScheduleStealthSettle"/>).
    /// </summary>
    private void ApplyStealthLayout()
    {
        var work = _stealthWorkArea;
        var (above, _) = StealthDockPositioning.Direction(_stealthAnchor, work);
        MoveWindowTo(_stealthAnchor.X - Width / 2, _stealthAnchor.Y - Height / 2);
        double anchorX = _stealthAnchor.X - Left;
        double anchorY = _stealthAnchor.Y - Top;
        _stealthLocalAnchor = new Point(anchorX, anchorY);
        // 인식 영역은 그립 자리까지 덮으므로 캡슐과 같은 왼쪽 기준을 쓴다.
        Canvas.SetLeft(StealthDot, anchorX - StealthDockPositioning.DotCenterX);
        Canvas.SetTop(StealthDot, anchorY - StealthDot.Height / 2);
        // 이동·재배치는 즉시 반영한다. 같은 자리면 진행 중인 펼침 효과를 끊지 않는다.
        PlaceStealth(animate: false);
        // 안내 화살표는 펼친 캡슐의 상태 점을 가리킨다. 가장자리에서는 캡슐이 안쪽으로 밀린 위치다.
        var expandedDot = ExpandedStealthDot();
        ConfigureStealthReminderLayout(above, expandedDot.X, expandedDot.Y);
    }

    /// <summary>
    /// 창 이동과 안쪽 그림 변경이 같은 화면 갱신에 겹치면 이전 그림이 새 위치에 잠깐 찍혀 잔상처럼 보인다.
    /// 그래서 창 이동이 먼저 화면에 반영된 뒤(렌더링보다 낮은 우선순위) 안쪽 배치를 맞춘다.
    /// </summary>
    private void ScheduleStealthSettle()
    {
        if (!_stealthLayoutFrozen || _stealthSettlePending) return;
        _stealthSettlePending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _stealthSettlePending = false;
            _stealthLayoutFrozen = false;
            if (_isClosed || Presentation != DockPresentation.Stealth) return;
            ApplyStealthLayout();
            RefreshStealthState();
            PositionNotificationFlyout();
        });
    }

    public void ShowRoutineReminder(string title, Func<string> statusProvider, string message)
    {
        // 상단 노치에서는 집중 종료 안내 말풍선을 띄우지 않는다. 이어서 앱이 미니바를 숨기면 노치와 띠가 바로 사라진다.
        if (Presentation == DockPresentation.TopNotch
            && (RoutineStatusProvider?.Invoke() ?? DockRoutineStatus.Idle).Phase == FocusRoutinePhase.Idle)
        {
            _reminderDismissTimer.Stop();
            _reminderPending = false;
            _hideAfterReminder = false;
            ReminderBubble.Visibility = Visibility.Collapsed;
            return;
        }
        // 상단 노치에서는 집중을 켜는 순간(노치가 나타나는 순간) 말풍선을 띄우지 않고
        // 노치 안에 색 물결(일렁임)을 한 번 흘려 켜졌음을 알린다. 휴식·종료 등 이후 안내는 기존처럼 말풍선으로 보인다.
        if (Presentation == DockPresentation.TopNotch
            && (!IsVisible || Clock() - _topNotchActivatedAt < TimeSpan.FromSeconds(2)))
        {
            _reminderDismissTimer.Stop();
            _hideAfterReminder = false;
            _reminderPending = false;
            _statusProvider = statusProvider;
            ReminderBubble.Visibility = Visibility.Collapsed;
            RefreshReminderStatus();
            PositionOnPrimaryWorkArea();
            bool appearing = !IsVisible;
            if (appearing) Show();
            if (appearing) PlayActivationAnimation();
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
        // 전체화면 동안에는 안내를 보류했다가 전체화면이 끝나면 보여 준다.
        _reminderPending = _notifications.IsPanelOpen || _notifications.IsDragging || _fullscreenSuppressed;
        ReminderBubble.Visibility = _reminderPending ? Visibility.Collapsed : Visibility.Visible;
        PositionOnPrimaryWorkArea();
        if (!IsVisible) Show();
        if (!_fullscreenSuppressed)
        {
            Topmost = false;
            Topmost = true;
        }
        PlayActivationAnimation();
        _statusTimer.Start();
        if (!_reminderPending) _reminderDismissTimer.Start();
        RefreshStealthState();
    }

    public void KeepVisible()
    {
        _hideAfterReminder = false;
        PositionOnPrimaryWorkArea();
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

    private void RefreshReminderStatus()
    {
        if (_statusProvider is not null)
            ReminderStatusText.Text = _statusProvider();
        var routine = RoutineStatusProvider?.Invoke() ?? DockRoutineStatus.Idle;
        NotchSurface.ToolTip = routine.Label;
        // 상단 노치는 호버하면 바로 윤곽선과 패널로 반응하므로 집중 상태 툴팁을 띄우지 않는다.
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
        RefreshStealthState();
    }

    public void PlayActivationAnimation()
    {
        if (Presentation == DockPresentation.TopNotch)
        {
            PlayTopNotchActivation();
            return;
        }
        ActivationHalo.BeginAnimation(UIElement.OpacityProperty, null);
        ActivationGlow.BeginAnimation(UIElement.OpacityProperty, null);
        ActivationGlow.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        ActivationGlow.StrokeDashOffset = 0;

        if (!SystemParameters.ClientAreaAnimation)
        {
            ActivationHalo.Opacity = 0;
            ActivationGlow.Opacity = 0;
            return;
        }

        var orbit = new DoubleAnimation(0, -198, TimeSpan.FromMilliseconds(1050))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1050) };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        var haloPulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1050) };
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.7, KeyTime.FromPercent(0.12)));
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromPercent(0.7)));
        haloPulse.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));

        ActivationHalo.BeginAnimation(UIElement.OpacityProperty, haloPulse);
        ActivationGlow.BeginAnimation(Shape.StrokeDashOffsetProperty, orbit);
        ActivationGlow.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void DockSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ActiveSurface.CaptureMouse())
            return;

        _pressScreenPoint = PointToScreen(e.GetPosition(this));
        _pressTop = Top;
        _pressLeft = Left;
        _pressStealthAnchor = _stealthAnchor;
        _isPressed = true;
        _isDragging = false;
        _hoverTimer.Stop();
        _stealthRevealTimer.Stop();
        e.Handled = true;
    }

    private void DockSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPressed)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPointerInteraction();
            return;
        }

        var delta = GetPointerDelta(e);

        if (!_isDragging)
        {
            _isDragging = DockPositioning.IsDrag(
                delta.X, delta.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance);
            if (_isDragging)
            {
                StopHoverTimers();
                _notifications.BeginDrag();
                if (Presentation == DockPresentation.Stealth)
                {
                    // 끄는 동안에는 창만 평행 이동하고 안쪽 그림은 고정한다.
                    _stealthLayoutFrozen = true;
                    ConfineCursorForStealthDrag();
                }
            }
        }

        if (!_isDragging)
            return;

        if (Presentation == DockPresentation.Stealth)
        {
            MoveStealth(delta);
            e.Handled = true;
            return;
        }

        if (Presentation == DockPresentation.TopNotch)
        {
            MoveTopNotch(delta.X);
            e.Handled = true;
            return;
        }

        var workArea = SystemParameters.WorkArea;
        if (Presentation != DockPresentation.Classic)
        {
            MoveCapsule(delta.X, workArea);
            e.Handled = true;
            return;
        }
        Top = DockPositioning.ClampTop(
            _pressTop + delta.Y, workArea.Top, workArea.Height, Height);
        e.Handled = true;
    }

    private void DockSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPressed)
            return;

        // 마지막 MouseMove가 오지 않은 상태에서 놓아도 드래그를 클릭으로 오인하지 않는다.
        var delta = GetPointerDelta(e);
        var dragged = _isDragging || DockPositioning.IsDrag(
            delta.X, delta.Y,
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance);

        if (dragged)
        {
            if (!_notifications.IsDragging) _notifications.BeginDrag();
            var workArea = SystemParameters.WorkArea;
            if (Presentation == DockPresentation.Stealth)
            {
                MoveStealth(delta);
                FinishStealthDrag();
            }
            else if (Presentation == DockPresentation.TopNotch)
                MoveTopNotch(delta.X);
            else if (Presentation != DockPresentation.Classic)
                MoveCapsule(delta.X, workArea);
            else
            {
                Top = DockPositioning.ClampTop(
                    _pressTop + delta.Y, workArea.Top, workArea.Height, Height);
                try
                {
                    _positionStore.Save(DockPositioning.ToRatio(
                        Top, workArea.Top, workArea.Height, Height));
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("미니바 위치를 저장하지 못했습니다.");
                }
            }
        }

        CancelPointerInteraction();
        if (!dragged)
        {
            PlayClickFeedback();
            ShowDashboardRequested?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void MoveCapsule(double deltaX, Rect workArea)
    {
        Left = Math.Clamp(_pressLeft + deltaX,
            workArea.Left - (Width - NotchSurface.Width), workArea.Right - Width);
        Top = workArea.Bottom - Height;
        _capsuleRightInset = workArea.Right - Left - Width;
    }

    /// <summary>커서가 있는 모니터의 작업 영역 안에서 자유롭게 옮긴다.</summary>
    private void MoveStealth(Vector delta)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        _stealthDeviceName = screen.DeviceName;
        _stealthWorkArea = GetWorkArea(screen);
        _stealthAnchor = StealthDockPositioning.Clamp(_pressStealthAnchor + delta, _stealthWorkArea);
        // 끄는 동안에는 창 안의 배치를 바꾸지 않고 창만 한 번에 옮긴다.
        // 투명 창에서 가로·세로를 따로 바꾸거나 내용을 다시 배치하면 이전 위치에 잔상이 남을 수 있다.
        // 놓을 때 FinishStealthDrag에서 작업 영역에 맞게 다시 배치한다.
        var shift = _stealthAnchor - _pressStealthAnchor;
        MoveWindowTo(_pressLeft + shift.X, _pressTop + shift.Y);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ReleaseCursorClipNative(nint rect);

    private bool _cursorClipped;

    /// <summary>
    /// 끄는 동안 커서를 미니바가 갈 수 있는 범위에 묶는다. 동그라미가 벽에 막히면 커서도 함께 멈춰,
    /// 커서만 화면 끝으로 빠져나가 손에서 놓친 느낌이 들지 않는다.
    /// 범위는 모든 모니터 작업 영역을 합친 사각형이라 다른 모니터로 옮기는 것도 막지 않는다.
    /// 놓거나 끌기가 취소되면 바로 푼다.
    /// </summary>
    private void ConfineCursorForStealthDrag()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        // 커서와 동그라미 중심 사이 거리(물리 픽셀)를 유지한 채 동그라미 범위만큼 커서 범위를 정한다.
        double offsetX = cursor.X - _stealthAnchor.X * dpi.DpiScaleX;
        double offsetY = cursor.Y - _stealthAnchor.Y * dpi.DpiScaleY;
        var area = screens.Select(screen => screen.WorkingArea).Aggregate(System.Drawing.Rectangle.Union);
        double insetX = StealthDockPositioning.CollapsedRadius * dpi.DpiScaleX;
        double insetY = StealthDockPositioning.CollapsedRadius * dpi.DpiScaleY;
        var rect = new NativeRect
        {
            Left = (int)Math.Ceiling(area.Left + insetX + offsetX),
            Top = (int)Math.Ceiling(area.Top + insetY + offsetY),
            Right = (int)Math.Floor(area.Right - insetX + offsetX) + 1,
            Bottom = (int)Math.Floor(area.Bottom - insetY + offsetY) + 1,
        };
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return;
        _cursorClipped = ClipCursor(ref rect);
    }

    private void ReleaseCursorClip()
    {
        if (!_cursorClipped) return;
        _cursorClipped = false;
        ReleaseCursorClipNative(0);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>창의 가로·세로 위치를 한 번의 이동으로 바꾼다. WPF의 Left·Top은 이동 후 자동으로 갱신된다.</summary>
    private void MoveWindowTo(double left, double top)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (hwnd == 0 || !SetWindowPos(hwnd, 0,
                (int)Math.Round(left * dpi.DpiScaleX), (int)Math.Round(top * dpi.DpiScaleY), 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate))
        {
            Left = left;
            Top = top;
        }
    }

    private void FinishStealthDrag()
    {
        // 가장자리에 붙일 때도 창만 평행 이동하고, 안쪽 배치는 화면이 갱신된 뒤 맞춘다.
        var snapped = StealthDockPositioning.Snap(_stealthAnchor, _stealthWorkArea);
        var shift = snapped - _stealthAnchor;
        _stealthAnchor = snapped;
        MoveWindowTo(Left + shift.X, Top + shift.Y);
        _stealthLayoutFrozen = true;
        ScheduleStealthSettle();
        var ratio = StealthDockPositioning.ToRatio(_stealthAnchor, _stealthWorkArea);
        try
        {
            _stealthPositionStore.Save(new StealthDockPositionSettings(_stealthDeviceName, ratio.X, ratio.Y));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("은신형 미니바 위치를 저장하지 못했습니다.");
        }
    }

    private void PlayClickFeedback()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;

        var press = new DoubleAnimation(
            fromValue: 1,
            toValue: 0.97,
            duration: TimeSpan.FromMilliseconds(75))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        ClickScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        ClickScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
        NotchClickScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        NotchClickScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
        StealthClickScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        StealthClickScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
        TopNotchClickScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        TopNotchClickScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);
    }

    /// <summary>상단 노치에 커서가 들어오면 폭을 살짝 넓혀 곧 펼쳐진다는 것을 알린다. 한 번만 움직인다.</summary>
    private void AnimateTopNotchHover(bool over)
    {
        if (Presentation != DockPresentation.TopNotch) return;
        // 호버 반응은 윤곽선만 쓴다. 폭을 넓혔다가 패널이 열릴 때 다시 줄이면 펼침이 끊겨 보인다.
        UpdateTopNotchOutline();
    }

    /// <summary>
    /// 집중이 켜졌음을 알리는 상단 노치 효과(일렁임). 청록·라벤더 색 물결이 노치 안을 왼쪽에서 오른쪽으로 약 3.5초에 한 번 천천히 흐르고,
    /// 끝나면 원래 검은 노치로 돌아온다. 반복하지 않으며 Windows 애니메이션 효과가 꺼져 있으면 생략한다.
    /// </summary>
    private void PlayTopNotchActivation()
    {
        _topNotchActivatedAt = Clock();
        TopNotchRipple.BeginAnimation(OpacityProperty, null);
        TopNotchRippleMove.BeginAnimation(TranslateTransform.XProperty, null);
        TopNotchRipple.Opacity = 0;
        if (!SystemParameters.ClientAreaAnimation || _fullscreenSuppressed) return;

        var duration = TimeSpan.FromMilliseconds(3500);
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

    /// <summary>
    /// 알림 패널이 열리면 노치 바닥을 패널 폭까지 넓혀 패널이 노치에서 늘어난 것처럼 보이게 하고, 닫히면 원래 노치로 돌린다.
    /// 열 때 0.16초, 닫을 때 0.18초에 한 번만 바뀐다. 패널은 노치가 거의 다 넓어진 뒤 펼쳐진다(<see cref="TopNotchLayout.PanelRevealDelayMilliseconds"/>).
    /// </summary>
    private void UpdateTopNotchOpen()
    {
        bool open = Presentation == DockPresentation.TopNotch && IsVisible && !_fullscreenSuppressed
            && _notifications.IsPanelOpen;
        double target = open ? 1 : 0;
        if (_topNotchOpenTarget == target) return;
        _topNotchOpenTarget = target;
        if (open) AnimateTopNotchHover(false);
        if (!SystemParameters.ClientAreaAnimation || Presentation != DockPresentation.TopNotch)
        {
            BeginAnimation(TopNotchOpenProgressProperty, null);
            TopNotchOpenProgress = target;
            return;
        }
        BeginAnimation(TopNotchOpenProgressProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(open ? TopNotchLayout.OpenMorphMilliseconds : 180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            }.AtDisplayRate(), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>현재 열림 정도에 맞춰 노치 모양(잘라 낼 영역)과 윤곽선을 다시 만든다.</summary>
    private void UpdateTopNotchGeometry()
    {
        double progress = TopNotchOpenProgress;
        TopNotchSurface.Clip = BuildTopNotchGeometry(progress, filled: true);
        // 패널과 이어진 동안에는 노치 바닥선을 긋지 않아 윤곽선이 패널 테두리로 이어진다.
        TopNotchOutline.Data = BuildTopNotchGeometry(progress, filled: false, includeBottom: progress <= .001);
    }

    private static Geometry BuildTopNotchGeometry(double progress, bool filled, bool includeBottom = true)
    {
        var left = TopNotchLayout.NotchLeftSide(progress);
        double width = TopNotchLayout.NotchSurfaceWidth;
        Point L(int i) => new(left[i].X, left[i].Y);
        Point R(int i) => new(width - left[i].X, left[i].Y);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(L(0), filled, filled);
            context.BezierTo(L(1), L(2), L(3), true, true);
            context.BezierTo(L(4), L(5), L(6), true, true);
            if (includeBottom) context.LineTo(R(6), true, true);
            else context.BeginFigure(R(6), false, false);
            context.BezierTo(R(5), R(4), R(3), true, true);
            context.BezierTo(R(2), R(1), R(0), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 커서가 노치 위에 있거나 알림 패널이 열린 동안 노치 윤곽선을 보인다.
    /// 나타날 때 0.12초, 사라질 때 0.16초에 걸쳐 한 번만 바뀐다.
    /// </summary>
    private void UpdateTopNotchOutline()
    {
        bool show = Presentation == DockPresentation.TopNotch && !_fullscreenSuppressed
            && (TopNotchSurface.IsMouseOver || _notifications.IsPanelOpen);
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

    private Vector GetPointerDelta(MouseEventArgs e)
    {
        // 화면 픽셀로 얻은 마우스 이동량을 WPF 창 좌표(DIP)로 변환한다.
        var deviceDelta = PointToScreen(e.GetPosition(this)) - _pressScreenPoint;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform?.Transform(deviceDelta) ?? deviceDelta;
    }

    private void DockSurface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        ReleaseCursorClip();
        ScheduleStealthSettle();
        _isPressed = false;
        _isDragging = false;
        _notifications.EndDrag();
        UpdatePointerState();
    }

    private void CancelPointerInteraction()
    {
        ReleaseCursorClip();
        ScheduleStealthSettle();
        _isPressed = false;
        _isDragging = false;
        if (ActiveSurface.IsMouseCaptured)
            ActiveSurface.ReleaseMouseCapture();
        _notifications.EndDrag();
        UpdatePointerState();
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != nameof(SystemParameters.WorkArea))
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(PositionOnPrimaryWorkArea);
            return;
        }

        if (!_isPressed)
            PositionOnPrimaryWorkArea();
    }

    /// <summary>모니터 연결·해제, 해상도 변경. 주 모니터 작업 영역이 그대로여도 은신형 위치를 다시 맞춘다.</summary>
    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        DisplayRefresh.Invalidate();
        if (_isClosed) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosed && !_isPressed && Presentation == DockPresentation.Stealth)
                PositionOnPrimaryWorkArea();
        });
    }

    private void Dock_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        // 배율이 다른 모니터로 옮긴 뒤에는 새 배율로 작업 영역을 다시 계산한다.
        if (!_isPressed && Presentation == DockPresentation.Stealth)
            PositionOnPrimaryWorkArea();
    }

    private void Dock_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(Dock_WindowProc);
    }

    private static nint Dock_WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // 클릭 순간 미니바가 활성 창을 가로채지 않아 대시보드의 기존 활성 상태를 판단할 수 있다.
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE: 클릭은 전달하되 미니바는 활성화하지 않는다.
        }
        return 0;
    }

    private void NotificationDockWindow_Closed(object? sender, EventArgs e)
    {
        ReleaseCursorClip();
        _isClosed = true;
        UpdateTopBand();
        _windowSource?.RemoveHook(Dock_WindowProc);
        _windowSource = null;
        SourceInitialized -= Dock_SourceInitialized;
        _reminderDismissTimer.Stop();
        _reminderDismissTimer.Tick -= ReminderDismissTimer_Tick;
        _statusTimer.Stop();
        StopHoverTimers();
        _hoverTimer.Tick -= HoverTimer_Tick;
        _leaveTimer.Tick -= LeaveTimer_Tick;
        _stealthTimer.Stop();
        _stealthRevealTimer.Stop();
        _stealthTimer.Tick -= StealthTimer_Tick;
        _stealthRevealTimer.Tick -= StealthRevealTimer_Tick;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        DpiChanged -= Dock_DpiChanged;
        _notifications.Changed -= Notifications_Changed;
        if (_notificationFlyout is not null)
        {
            _notificationFlyout.MouseEnter -= Flyout_MouseEnter;
            _notificationFlyout.MouseLeave -= Flyout_MouseLeave;
            _notificationFlyout.SwitchPresentationRequested -= SwitchPresentationRequested;
            _notificationFlyout.ShowDashboardRequested -= Flyout_ShowDashboardRequested;
            _notificationFlyout.BandStyleRequested -= Flyout_BandStyleRequested;
            _notificationFlyout.Close();
            _notificationFlyout = null;
        }
        IsVisibleChanged -= Dock_IsVisibleChanged;
        LocationChanged -= Dock_LocationChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        Closed -= NotificationDockWindow_Closed;
    }

    private void CloseReminder_Click(object sender, RoutedEventArgs e)
    {
        HideReminderBubble();
        e.Handled = true;
    }

    private bool IsPointerOverNotifications => ActiveSurface.IsMouseOver ||
        _notificationFlyout?.IsMouseOver == true || IsPointerOverPanelBridge();

    private bool IsPointerOverPanelBridge()
    {
        if (!IsVisible || _notificationFlyout is not { IsVisible: true } flyout) return false;
        // 두 HWND 사이의 좁은 빈틈도 패널 영역으로 취급한다. 좌표는 모두 화면 픽셀이다.
        // 은신형의 표면(Canvas)은 창 전체이므로 실제 캡슐을 기준으로 삼는다.
        FrameworkElement surface = Presentation == DockPresentation.Stealth ? StealthCapsule : ActiveSurface;
        var dockTop = surface.PointToScreen(new Point());
        var dockBottom = surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight));
        var panelTop = flyout.PointToScreen(new Point(8, 8));
        var panelBottom = flyout.PointToScreen(new Point(flyout.ActualWidth - 8, flyout.ActualHeight - 8));
        if (Presentation == DockPresentation.Stealth)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            bool panelAbove = panelBottom.Y <= dockTop.Y;
            double gapTop = panelAbove ? panelBottom.Y : dockBottom.Y;
            double gapBottom = panelAbove ? dockTop.Y : panelTop.Y;
            return cursor.X >= Math.Max(dockTop.X, panelTop.X) && cursor.X <= Math.Min(dockBottom.X, panelBottom.X)
                && cursor.Y >= gapTop && cursor.Y <= gapBottom;
        }
        if (Presentation == DockPresentation.TopNotch)
        {
            // 패널은 노치 아래에 있으므로 노치 아랫변과 패널 윗변 사이가 빈틈이다.
            var cursor = System.Windows.Forms.Cursor.Position;
            return cursor.X >= Math.Max(dockTop.X, panelTop.X) && cursor.X <= Math.Min(dockBottom.X, panelBottom.X)
                && cursor.Y >= dockBottom.Y && cursor.Y <= panelTop.Y;
        }
        if (Presentation != DockPresentation.Classic)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            return cursor.X >= Math.Max(dockTop.X, panelTop.X) && cursor.X <= Math.Min(dockBottom.X, panelBottom.X)
                && cursor.Y >= panelBottom.Y && cursor.Y <= dockTop.Y;
        }
        double top = Math.Max(dockTop.Y, panelTop.Y);
        double bottom = Math.Min(dockBottom.Y, panelBottom.Y);
        if (bottom <= top || dockTop.X < panelBottom.X) return false;
        var pointer = System.Windows.Forms.Cursor.Position;
        return pointer.X >= panelBottom.X && pointer.X <= dockTop.X && pointer.Y >= top && pointer.Y <= bottom;
    }

    private void DockSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateTopNotchHover(true);
        UpdatePointerState();
    }
    private void DockSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateTopNotchHover(false);
        UpdatePointerState();
    }
    private void Flyout_MouseEnter(object sender, MouseEventArgs e) => UpdatePointerState();
    private void Flyout_MouseLeave(object sender, MouseEventArgs e) => UpdatePointerState();

    private void UpdatePointerState()
    {
        if (_isClosed) return;
        UpdateBackground();
        UpdateStealthPointer();
        if (!_notifications.IsActive || _isPressed || _notifications.IsDragging || !IsVisible || _fullscreenSuppressed)
        {
            StopHoverTimers();
            return;
        }
        // 은신형은 점이 캡슐로 펼쳐진 뒤부터 패널 대기 시간을 센다.
        bool overSurface = ActiveSurface.IsMouseOver &&
            (Presentation != DockPresentation.Stealth || _stealth.Mode == StealthDockMode.Expanded);
        // 빈틈에서는 새 MouseLeave가 오지 않으므로 닫힘 타이머로 계속 위치를 확인한다.
        if (overSurface || _notificationFlyout?.IsMouseOver == true)
        {
            _leaveTimer.Stop();
            if (!_notifications.IsPanelOpen && !_hoverTimer.IsEnabled) _hoverTimer.Start();
        }
        else
        {
            _hoverTimer.Stop();
            if (_notifications.IsPanelOpen && !_notifications.IsPinned && !_leaveTimer.IsEnabled)
                _leaveTimer.Start();
        }
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_isClosed || !IsVisible || !ActiveSurface.IsMouseOver || _isPressed || !_notifications.IsActive
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
        if (IsPointerOverNotifications) return;
        _leaveTimer.Stop();
        _notifications.ClosePanel();
    }

    private void StopHoverTimers()
    {
        _hoverTimer.Stop();
        _leaveTimer.Stop();
    }

    private void Notifications_Changed(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        UpdateNotificationVisuals();
        // 전체화면 동안에는 패널 상태만 유지하고 창은 띄우지 않는다.
        if (_notifications.IsPanelOpen && IsVisible && !_fullscreenSuppressed)
        {
            if (_notificationFlyout is null)
            {
                _notificationFlyout = new NotificationFlyoutWindow(_notifications) { Owner = this };
                _notificationFlyout.MouseEnter += Flyout_MouseEnter;
                _notificationFlyout.MouseLeave += Flyout_MouseLeave;
                _notificationFlyout.SwitchPresentationRequested += SwitchPresentationRequested;
                _notificationFlyout.ShowDashboardRequested += Flyout_ShowDashboardRequested;
                _notificationFlyout.BandStyleRequested += Flyout_BandStyleRequested;
                _notificationFlyout.SetBandStyle(_topBandStyle);
                _notificationFlyout.SetPresentation(Presentation);
            }
            _notificationFlyout.Refresh();
            RefreshReminderStatus();
            PositionNotificationFlyout();
            if (!_notificationFlyout.IsVisible) _notificationFlyout.Show();
        }
        else
        {
            _notificationFlyout?.Refresh();
            _notificationFlyout?.Hide();
            if (_reminderPending && !_notifications.IsDragging && !_fullscreenSuppressed)
            {
                _reminderPending = false;
                ReminderBubble.Visibility = Visibility.Visible;
                _reminderDismissTimer.Start();
            }
        }
        UpdateBackground();
        UpdateTopNotchOpen();
        UpdateTopNotchOutline();
        // 핀 해제 시 커서가 이미 바깥이라면 지연 닫기를 시작한다.
        if (_notifications.IsPanelOpen) UpdatePointerState();
    }

    private void PositionNotificationFlyout()
    {
        if (_notificationFlyout is null || !IsVisible) return;
        if (Presentation == DockPresentation.Stealth)
        {
            PositionStealthFlyout(_notificationFlyout);
            return;
        }
        var workArea = SystemParameters.WorkArea;
        _notificationFlyout.Height = Math.Min(480, Math.Max(1, workArea.Height - 16));
        if (Presentation == DockPresentation.TopNotch)
        {
            // 패널을 노치 바닥에 틈 없이 붙인다. 넓어진 노치 바닥과 목록 패널의 폭이 같아 한 덩어리로 이어진다.
            double panelTop = Top + TopNotchLayout.NotchHeight - TopNotchLayout.PanelOverlap;
            _notificationFlyout.Height = Math.Min(480 + TopNotchLayout.FlyoutInset,
                Math.Max(1, workArea.Bottom - panelTop + TopNotchLayout.FlyoutInset));
            _notificationFlyout.Left = TopNotchLayout.PanelFlyoutLeft(Left + Width / 2, _notificationFlyout.Width,
                workArea.Left, workArea.Right);
            _notificationFlyout.Top = panelTop - TopNotchLayout.FlyoutInset;
            return;
        }
        var dockOrigin = ActiveSurface.TranslatePoint(new Point(), this);
        if (Presentation != DockPresentation.Classic)
        {
            _notificationFlyout.Left = Math.Clamp(Left + dockOrigin.X + ActiveSurface.ActualWidth + 8 - _notificationFlyout.Width,
                workArea.Left, Math.Max(workArea.Left, workArea.Right - _notificationFlyout.Width));
            _notificationFlyout.Top = DockPositioning.ClampTop(Top + dockOrigin.Y - _notificationFlyout.Height,
                workArea.Top, workArea.Height, _notificationFlyout.Height);
            return;
        }
        // 창의 투명 여백을 고려해 실제 카드와 캡슐 사이 간격은 8 DIP로 맞춘다.
        _notificationFlyout.Left = Left + dockOrigin.X - _notificationFlyout.Width;
        _notificationFlyout.Top = DockPositioning.ClampTop(
            Top + dockOrigin.Y - 8, workArea.Top + 8, workArea.Height - 16, _notificationFlyout.Height);
    }

    /// <summary>
    /// 은신형 패널은 공간이 넓은 쪽(위·아래)으로 펼치고, 기준점이 있는 쪽 끝을 캡슐에 맞춘다.
    /// 패널 창의 투명 여백(8 DIP)이 캡슐과 카드 사이 간격이 된다.
    /// </summary>
    private void PositionStealthFlyout(NotificationFlyoutWindow flyout)
    {
        const double flyoutInset = 8;
        var work = _stealthWorkArea;
        StealthCapsule.UpdateLayout();
        var origin = StealthCapsule.TranslatePoint(new Point(), this);
        double capsuleLeft = Left + origin.X;
        double capsuleTop = Top + origin.Y;
        double capsuleRight = capsuleLeft + StealthCapsule.ActualWidth;
        double capsuleBottom = capsuleTop + StealthCapsule.ActualHeight;
        var (above, alignRight) = StealthDockPositioning.Direction(_stealthAnchor, work);
        double available = above ? capsuleTop - work.Top : work.Bottom - capsuleBottom;
        flyout.Height = Math.Min(480, Math.Max(1, available));
        double left = alignRight ? capsuleRight + flyoutInset - flyout.Width : capsuleLeft - flyoutInset;
        flyout.Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - flyout.Width));
        flyout.Top = above ? capsuleTop - flyout.Height : capsuleBottom;
    }

    private void Dock_LocationChanged(object? sender, EventArgs e)
    {
        // 끄는 동안 패널은 숨겨져 있으므로 놓은 뒤 한 번만 맞춘다.
        if (_notifications.IsDragging && Presentation == DockPresentation.Stealth) return;
        PositionNotificationFlyout();
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
            if (Presentation == DockPresentation.Stealth) _stealth.Reveal(Clock());
        }
        UpdateTopBand();
        UpdateBackground();
        UpdateStealthTimer();
        RefreshStealthState();
    }

    private void UpdateNotificationVisuals()
    {
        // 새 판정·읽음 처리는 이전 펄스를 교체한다. 애니메이션을 누적하지 않는다.
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        StatusDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        var appearance = DockNotificationAppearance.For(_notifications.HighestUrgency);
        var color = (Color)ColorConverter.ConvertFromString(appearance.Color);
        StatusDotBrush.Color = StatusDotGlow.Color = color;
        StatusDotGlow.Opacity = appearance.GlowOpacity;
        StatusDotGlow.BlurRadius = appearance.BlurRadius;
        UnreadBadge.Visibility = _notifications.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnreadBadgeText.Text = _notifications.BadgeText;
        bool unread = _notifications.UnreadCount > 0;
        bool urgent = _notifications.HighestUrgency >= 5;
        NotchStatusBrush.Color = unread ? color : (Color)ColorConverter.ConvertFromString("#455D55");
        NotchQuietDot.Visibility = urgent ? Visibility.Collapsed : Visibility.Visible;
        NotchUrgentIndicator.Visibility = urgent ? Visibility.Visible : Visibility.Collapsed;
        NotchUrgentCountText.Text = _notifications.BadgeText;
        TopNotchStatusBrush.Color = NotchStatusBrush.Color;
        TopNotchQuietDot.Visibility = NotchQuietDot.Visibility;
        TopNotchUrgentIndicator.Visibility = NotchUrgentIndicator.Visibility;
        TopNotchUrgentCountText.Text = _notifications.BadgeText;
        System.Windows.Automation.AutomationProperties.SetItemStatus(TopNotchSurface,
            _notifications.UnreadCount > 0 ? $"미확인 중요 알림 {_notifications.UnreadCount}개" : "미확인 알림 없음");
        System.Windows.Automation.AutomationProperties.SetItemStatus(DockSurface,
            _notifications.UnreadCount > 0 ? $"미확인 중요 알림 {_notifications.UnreadCount}개" : "미확인 알림 없음");
        System.Windows.Automation.AutomationProperties.SetItemStatus(NotchSurface,
            _notifications.UnreadCount > 0 ? $"미확인 중요 알림 {_notifications.UnreadCount}개" : "미확인 알림 없음");
        System.Windows.Automation.AutomationProperties.SetItemStatus(StealthSurface,
            _notifications.UnreadCount > 0 ? $"미확인 중요 알림 {_notifications.UnreadCount}개" : "미확인 알림 없음");
        RefreshStealthState();
    }

    private void UpdateStealthTimer()
    {
        bool run = !_isClosed && IsVisible && Presentation == DockPresentation.Stealth;
        if (run)
        {
            if (!_stealthTimer.IsEnabled) _stealthTimer.Start();
            return;
        }
        _stealthTimer.Stop();
        _stealthRevealTimer.Stop();
    }

    private void StealthTimer_Tick(object? sender, EventArgs e) => RefreshStealthState();

    /// <summary>접힌 점 위에 잠시 머물면 캡슐로 펼친다. 지나가는 커서로는 펼치지 않는다.</summary>
    private void UpdateStealthPointer()
    {
        if (Presentation != DockPresentation.Stealth || !IsVisible)
        {
            _stealthRevealTimer.Stop();
            return;
        }
        RefreshStealthState();
        bool waiting = StealthSurface.IsMouseOver && !_isPressed && _stealth.Mode != StealthDockMode.Expanded;
        if (!waiting) _stealthRevealTimer.Stop();
        else if (!_stealthRevealTimer.IsEnabled) _stealthRevealTimer.Start();
    }

    private void StealthRevealTimer_Tick(object? sender, EventArgs e)
    {
        _stealthRevealTimer.Stop();
        if (_isClosed || Presentation != DockPresentation.Stealth || !IsVisible || !StealthSurface.IsMouseOver) return;
        _stealth.Reveal(Clock());
        RefreshStealthState();
        UpdatePointerState();
    }

    /// <summary>은신 상태를 다시 판단해 그린다. 타이머와 상태 변화 시 호출된다.</summary>
    public void RefreshStealthState()
    {
        if (_isClosed || Presentation != DockPresentation.Stealth) return;
        bool engaged = (_stealth.Mode == StealthDockMode.Expanded && StealthSurface.IsMouseOver)
            || _isPressed || _notifications.IsPanelOpen || _notifications.IsDragging || IsReminderVisible;
        bool userActive = UserIdleProvider() < _stealth.Timing.AwayThreshold;
        var mode = _stealth.Evaluate(new StealthDockInput(engaged, _notifications.UnreadCount, userActive), Clock());
        RenderStealth(mode);
        // 새 중요 알림은 은신을 풀지 않고 빛을 한 번만 낸다.
        if (_stealth.ConsumeFlash()) PlayStealthFlash();
    }

    private void RenderStealth(StealthDockMode mode)
    {
        bool unread = _notifications.UnreadCount > 0;
        bool expanded = mode == StealthDockMode.Expanded;
        // 은신형은 평소 무채색이고, 미확인 알림이 있을 때만 신호색을 쓴다.
        StealthStatusBrush.Color = (Color)ColorConverter.ConvertFromString(
            StealthDockPalette.StatusColor(_notifications.UnreadCount, _notifications.HighestUrgency, expanded));
        StealthCountText.Text = _notifications.BadgeText;
        StealthCountText.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        StealthCapsule.IsHitTestVisible = expanded;
        StealthDot.IsHitTestVisible = !expanded;
        PlaceStealth(animate: true);
    }

    /// <summary>
    /// 현재 단계에 맞춰 몸체를 동그라미 또는 펼친 캡슐로 배치한다.
    /// 접힌 상태는 지름 12의 검은 동그라미에 상태 점 하나만 둔다(점은 지름의 약 40%).
    /// 상태 점 중심은 항상 기준점에 있어, 펼치고 접을 때 같은 물체가 늘어나고 줄어든다.
    /// 미확인 알림이 남으면 지름 14로 조금 키우고 상태 점을 선명하게 해 색뿐 아니라 크기와 진하기로도 구분한다.
    /// </summary>
    /// <summary>펼친 캡슐의 상태 점 중심(창 안 DIP). 가장자리에서는 동그라미보다 안쪽에 있다.</summary>
    private Point ExpandedStealthDot()
    {
        StealthCapsule.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var origin = StealthDockPositioning.CapsuleOrigin(_stealthAnchor, StealthCapsule.DesiredSize.Width, _stealthWorkArea);
        return new Point(origin.X - Left + StealthDockPositioning.DotCenterX, origin.Y - Top + StealthDockPositioning.HalfHeight);
    }

    private void PlaceStealth(bool animate)
    {
        if (Presentation != DockPresentation.Stealth) return;
        if (_stealthLayoutFrozen && _stealthPlaced is not null) return;
        var mode = _stealth.Mode;
        bool expanded = mode == StealthDockMode.Expanded;
        bool alert = mode == StealthDockMode.Alert;
        // 펼친 캡슐은 작업 영역 안으로 밀어 넣은 위치에 둔다. 접혀 있어도 내용 위치는 미리 맞춰 둔다.
        var expandedDot = ExpandedStealthDot();
        Canvas.SetLeft(StealthCapsule, expandedDot.X - StealthDockPositioning.DotCenterX);
        Canvas.SetTop(StealthCapsule, expandedDot.Y - StealthDockPositioning.HalfHeight);
        double anchorX = expanded ? expandedDot.X : _stealthLocalAnchor.X;
        double anchorY = expanded ? expandedDot.Y : _stealthLocalAnchor.Y;
        double width = expanded ? StealthCapsule.DesiredSize.Width : alert ? 14 : 12;
        double height = expanded ? StealthDockPositioning.HalfHeight * 2 : width;
        Canvas.SetLeft(StealthFlash, anchorX - StealthFlash.Width / 2);
        Canvas.SetTop(StealthFlash, anchorY - StealthFlash.Height / 2);
        var target = new StealthPlacement(
            BodyLeft: expanded ? anchorX - StealthDockPositioning.DotCenterX : anchorX - width / 2,
            BodyTop: anchorY - height / 2,
            BodyWidth: width,
            BodyHeight: height,
            DotSize: expanded ? 7 : alert ? 6 : 5,
            DotOpacity: expanded || alert ? 1 : StealthRestingOpacity,
            ContentOpacity: expanded ? 1 : 0);
        if (_stealthPlaced == target) return;
        // 처음 배치는 효과 없이 바로 놓는다.
        bool morph = animate && _stealthPlaced is not null && SystemParameters.ClientAreaAnimation;
        bool opening = morph && expanded && _stealthPlaced is { ContentOpacity: 0 };
        _stealthPlaced = target;

        MoveStealthPart(StealthBody, Canvas.LeftProperty, target.BodyLeft, morph);
        MoveStealthPart(StealthBody, Canvas.TopProperty, target.BodyTop, morph);
        MoveStealthPart(StealthBody, WidthProperty, target.BodyWidth, morph);
        MoveStealthPart(StealthBody, HeightProperty, target.BodyHeight, morph);
        MoveStealthPart(StealthBody, System.Windows.Shapes.Rectangle.RadiusXProperty, target.BodyHeight / 2, morph);
        MoveStealthPart(StealthBody, System.Windows.Shapes.Rectangle.RadiusYProperty, target.BodyHeight / 2, morph);
        MoveStealthPart(StealthStatusDot, Canvas.LeftProperty, anchorX - target.DotSize / 2, morph);
        MoveStealthPart(StealthStatusDot, Canvas.TopProperty, anchorY - target.DotSize / 2, morph);
        MoveStealthPart(StealthStatusDot, WidthProperty, target.DotSize, morph);
        MoveStealthPart(StealthStatusDot, HeightProperty, target.DotSize, morph);
        MoveStealthPart(StealthStatusDot, OpacityProperty, target.DotOpacity, morph);
        // 펼칠 때는 몸체가 거의 다 자란 뒤 손잡이·개수가 나타나고, 접을 때는 먼저 사라진다.
        MoveStealthPart(StealthCapsule, OpacityProperty, target.ContentOpacity, morph,
            opening ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(90),
            opening ? TimeSpan.FromMilliseconds(90) : TimeSpan.Zero);
    }

    /// <summary>
    /// 새 중요 알림 도착 시 한 번만 재생한다. 신호색 빛이 동그라미 뒤에서 크게 퍼졌다가 약 1.1초에 걸쳐 사라지고,
    /// 상태 점도 잠깐 흰빛으로 밝아졌다 돌아온다. 반복하지 않으며, Windows 애니메이션 효과가 꺼져 있으면 생략한다.
    /// </summary>
    private void PlayStealthFlash()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        StealthFlashBrush.Color = StealthStatusBrush.Color;
        var duration = TimeSpan.FromMilliseconds(1100);
        var glow = new DoubleAnimationUsingKeyFrames { Duration = duration };
        glow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(.12)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        var grow = new DoubleAnimation(1, 2.2, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        StealthFlash.BeginAnimation(OpacityProperty, glow, HandoffBehavior.SnapshotAndReplace);
        StealthFlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow, HandoffBehavior.SnapshotAndReplace);
        StealthFlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow, HandoffBehavior.SnapshotAndReplace);
        StealthStatusBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Colors.White, TimeSpan.FromMilliseconds(140))
            {
                AutoReverse = true,
                FillBehavior = FillBehavior.Stop,
            }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>상태가 바뀔 때 한 번만 움직인다. 반복·깜빡임은 없다.</summary>
    private static void MoveStealthPart(UIElement element, DependencyProperty property, double target, bool animate,
        TimeSpan? duration = null, TimeSpan? beginTime = null)
    {
        if (!animate)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, target);
            return;
        }
        element.BeginAnimation(property, new DoubleAnimation(target, duration ?? StealthMorphDuration)
        {
            BeginTime = beginTime ?? TimeSpan.Zero,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private void PlayNotificationPulse()
    {
        if (Presentation != DockPresentation.Classic || !SystemParameters.ClientAreaAnimation || _notifications.UnreadCount == 0) return;
        var pulse = new DoubleAnimation(1, 1.45, TimeSpan.FromMilliseconds(160))
        {
            AutoReverse = true, FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        StatusDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        var brighten = new ColorAnimation(StatusDotBrush.Color, Colors.White, TimeSpan.FromMilliseconds(160))
        {
            AutoReverse = true, FillBehavior = FillBehavior.Stop,
        };
        StatusDotBrush.BeginAnimation(SolidColorBrush.ColorProperty, brighten, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateBackground()
    {
        bool engaged = IsVisible && (ActiveSurface.IsMouseOver || _notifications.IsPanelOpen);
        if (_backgroundEngaged == engaged) return;
        _backgroundEngaged = engaged;
        var target = (Color)ColorConverter.ConvertFromString(engaged
            ? DockNotificationAppearance.EngagedBackground : DockNotificationAppearance.Background);
        if (SystemParameters.ClientAreaAnimation)
            DockBackground.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(160)), HandoffBehavior.SnapshotAndReplace);
        else
        {
            DockBackground.BeginAnimation(SolidColorBrush.ColorProperty, null);
            DockBackground.Color = target;
        }
    }

}

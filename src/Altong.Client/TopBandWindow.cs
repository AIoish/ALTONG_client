using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Altong.Client.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Altong.Client;

/// <summary>
/// 상단 노치 디자인에서 주 모니터 맨 위 띠를 AppBar로 등록한다.
/// 작업표시줄처럼 공간을 예약하므로 최대화한 창도 띠 아래부터 그려지고, 노치가 창을 가리지 않는다.
/// 노치는 <see cref="NotificationDockWindow"/>가 이 띠 위에 그린다.
/// </summary>
public sealed class TopBandWindow : Window
{
    private const int AbmNew = 0x00;
    private const int AbmRemove = 0x01;
    private const int AbmQueryPos = 0x02;
    private const int AbmSetPos = 0x03;
    private const int AbmActivate = 0x06;
    private const int AbmWindowPosChanged = 0x09;
    private const int AbnPosChanged = 0x01;
    private const int AbnFullScreenApp = 0x02;
    private const int AbeTop = 1;
    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmMouseActivate = 0x0021;
    private const int WmDisplayChange = 0x007E;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = -1;

    private static readonly int TaskbarCreatedMessage = (int)RegisterWindowMessage("TaskbarCreated");
    private readonly int _callbackMessage = (int)RegisterWindowMessage("Altong.TopBand.AppBarMessage");
    private readonly SolidColorBrush _brush = new(Colors.Black);
    private readonly TopBandWindowGuard _guard = new();
    private HwndSource? _source;
    private nint _hwnd;
    private bool _registered;
    private bool _positioning;
    private TopBandStyle _bandStyle;
    /// <summary>띠 바로 위에 둘 창(노치). 띠가 노치를 덮지 않도록 z-순서를 항상 그 아래로 맞춘다.</summary>
    private nint _keepBelow;

    /// <summary>전체화면 앱이 열리거나(true) 닫힐 때(false) 알린다. UI 스레드에서 발생한다.</summary>
    public event EventHandler<bool>? FullscreenChanged;

    public bool IsFullscreenActive { get; private set; }
    public bool IsRegistered => _registered;

    public TopBandWindow(TopBandStyle style)
    {
        _bandStyle = style;
        Title = "Altong 상단 띠";
        WindowStyle = WindowStyle.None;
        // 바탕화면 띠가 실제 바탕화면을 비쳐 보이도록 투명 창으로 만든다.
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = _brush;
        // 등록 전 첫 위치. 등록 뒤 Windows가 정해 준 영역으로 다시 옮긴다.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = TopNotchLayout.BandHeight;
        ApplyColor();
        SourceInitialized += OnSourceInitialized;
        DpiChanged += OnDpiChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public TopBandStyle BandStyle
    {
        get => _bandStyle;
        set
        {
            _bandStyle = value;
            ApplyColor();
        }
    }

    /// <summary>
    /// 띠를 칠한다. 바탕화면이면 거의 투명하게 칠해 그 아래 실제 바탕화면이 그대로 보인다.
    /// 띠 영역에는 다른 창이 들어오지 못하므로 비쳐 보이는 것은 바탕화면뿐이고, 바탕화면을 바꿔도 따로 맞출 필요가 없다.
    /// </summary>
    private void ApplyColor() =>
        _brush.Color = (Color)ColorConverter.ConvertFromString(TopNotchLayout.BandFill(_bandStyle));

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // Alt+Tab 목록에 나오지 않고, 눌러도 작업 중인 창의 활성 상태를 가져오지 않는다.
        SetWindowLong(_hwnd, GwlExStyle, GetWindowLong(_hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        Register();
    }

    private void Register()
    {
        if (_registered || _hwnd == 0) return;
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(AbmNew, ref data) != 0;
        if (!_registered)
        {
            // 공간을 예약하지 못한 띠는 최대화한 창의 위쪽을 가린다. 이때는 띠를 숨기고 노치만 남긴다.
            Trace.TraceWarning("상단 띠를 AppBar로 등록하지 못해 띠를 숨깁니다.");
            _ = Dispatcher.BeginInvoke(() => Visibility = Visibility.Hidden);
            return;
        }
        UpdatePosition();
    }

    private void Unregister()
    {
        _guard.Stop();
        if (!_registered || _hwnd == 0) return;
        _registered = false;
        var data = NewData();
        SHAppBarMessage(AbmRemove, ref data);
    }

    /// <summary>주 모니터 맨 위에 띠 높이만큼 예약하고 창을 그 영역에 맞춘다. 좌표는 물리 픽셀이다.</summary>
    private void UpdatePosition()
    {
        if (!_registered || _positioning || _hwnd == 0) return;
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        _positioning = true;
        try
        {
            var bounds = screen.Bounds;
            int height = TopNotchLayout.BandHeightPixels(VisualTreeHelper.GetDpi(this).DpiScaleY);
            var data = NewData();
            data.uEdge = AbeTop;
            data.rc = new NativeRect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Top + height };
            SHAppBarMessage(AbmQueryPos, ref data);
            // 작업표시줄을 위에 둔 경우 Windows가 그 아래로 밀어 준다. 높이는 띠 높이로 유지한다.
            data.rc.Bottom = data.rc.Top + height;
            SHAppBarMessage(AbmSetPos, ref data);
            // 맨 위로 올리지 않고 노치 바로 아래에 둔다. 셸이 위치를 다시 알릴 때마다 띠가 노치를 덮던 문제를 막는다.
            SetWindowPos(_hwnd, _keepBelow != 0 ? _keepBelow : HwndTopmost, data.rc.Left, data.rc.Top,
                data.rc.Right - data.rc.Left, data.rc.Bottom - data.rc.Top, SwpNoActivate);
            // 띠가 자리를 잡은 뒤 다른 창이 띠를 넘지 못하게 한다. 이미 걸친 창은 바로 아래로 내린다.
            _guard.Start(data.rc.Bottom, VisualTreeHelper.GetDpi(this).DpiScaleY);
        }
        finally
        {
            _positioning = false;
        }
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e) => UpdatePosition();

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (message == _callbackMessage)
        {
            switch ((int)wParam)
            {
                case AbnPosChanged:
                    UpdatePosition();
                    break;
                case AbnFullScreenApp:
                    SetFullscreen(lParam != 0);
                    break;
            }
            handled = true;
            return 0;
        }
        if (message == TaskbarCreatedMessage)
        {
            // 탐색기가 다시 시작되면 등록이 사라지므로 다시 등록한다.
            _guard.Stop();
            _registered = false;
            Register();
            return 0;
        }
        if (!_registered) return 0;
        if (message == WmActivate)
        {
            var data = NewData();
            SHAppBarMessage(AbmActivate, ref data);
        }
        else if (message == WmWindowPosChanged && !_positioning)
        {
            var data = NewData();
            SHAppBarMessage(AbmWindowPosChanged, ref data);
        }
        else if (message == WmDisplayChange)
        {
            _ = Dispatcher.BeginInvoke(UpdatePosition);
        }
        return 0;
    }

    /// <summary>
    /// 전체화면 동안 띠를 숨긴다. 예약한 공간은 유지해 전체화면이 끝난 뒤 창 배치가 흔들리지 않는다.
    /// </summary>
    private void SetFullscreen(bool opened)
    {
        // 바탕화면 보기·화면 캡처·시작 메뉴처럼 잠깐 화면을 덮는 셸 화면은 전체화면 앱으로 보지 않는다.
        if (opened && (TopNotchLayout.IsShellWindowClass(ForegroundClassName())
                       || TopNotchLayout.IsSystemOverlayProcess(ForegroundProcessName()))) opened = false;
        if (IsFullscreenActive == opened) return;
        IsFullscreenActive = opened;
        if (opened)
        {
            Topmost = false;
            Visibility = Visibility.Hidden;
        }
        else if (_registered)
        {
            Visibility = Visibility.Visible;
            Topmost = true;
            PlaceBelow(_keepBelow);
        }
        FullscreenChanged?.Invoke(this, opened);
    }

    /// <summary>
    /// 띠를 주어진 창(노치) 바로 아래 z-순서에 둔다. 두 창 모두 맨 위 창이므로 띠는 다른 창 위에 있으면서 노치를 덮지 않는다.
    /// </summary>
    public void PlaceBelow(nint hwnd)
    {
        _keepBelow = hwnd;
        if (_hwnd == 0 || hwnd == 0) return;
        SetWindowPos(_hwnd, hwnd, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ForegroundClassName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : null;
    }

    private AppBarData NewData() => new()
    {
        cbSize = Marshal.SizeOf<AppBarData>(),
        hWnd = _hwnd,
    };

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => Unregister();

    private void OnClosed(object? sender, EventArgs e)
    {
        Unregister();
        _guard.Dispose();
        _source?.RemoveHook(WndProc);
        _source = null;
        SourceInitialized -= OnSourceInitialized;
        DpiChanged -= OnDpiChanged;
        Closing -= OnClosing;
        Closed -= OnClosed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public nint hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public NativeRect rc;
        public nint lParam;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(int message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}

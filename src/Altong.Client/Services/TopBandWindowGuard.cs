using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Altong.Client.Services;

/// <summary>
/// 상단 띠를 다른 앱 창이 넘지 못하게 한다. 노치 디자인에서 띠가 등록된 동안만 동작한다.
/// <list type="number">
/// <item>띠가 생길 때 띠에 걸친 창을 띠 바로 아래로 내린다.</item>
/// <item>창의 제목 표시줄이나 위쪽 테두리를 잡고 끄는 동안 커서를 띠 아래에 가둬, 창과 커서가 띠 선에서 함께 멈춘다.</item>
/// <item>커서를 가둘 수 없는 이동(키보드 이동, 앱이 직접 옮기는 경우)은 이동이 끝나거나 창이 앞으로 나올 때 띠 아래로 내린다.</item>
/// </list>
/// 최대화·최소화·전체화면 창, 도구 창, 맨 위 고정 창, 보조 모니터 창, 알통 자신의 창은 건드리지 않는다.
/// 관리자 권한으로 실행된 창은 Windows 보안 정책상 옮길 수 없다.
/// 모든 호출과 이벤트는 UI 스레드에서 처리한다.
/// </summary>
internal sealed class TopBandWindowGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeStart = 0x000A;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint WmNcHitTest = 0x0084;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmSwapButton = 23;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpAsyncWindowPos = 0x4000;
    /// <summary>창이 끌기 판정에 응답하지 않을 때 제목 표시줄로 볼 윗부분 높이(물리 픽셀, 100% 배율 기준).</summary>
    private const int CaptionFallbackPixels = 48;

    private readonly WinEventDelegate _callback;
    private readonly DispatcherTimer _clipWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly uint _processId = (uint)Environment.ProcessId;
    private nint _moveSizeHook;
    private nint _foregroundHook;
    private int _bandBottom;
    private double _dpiScale = 1;
    private bool _cursorClipped;

    public TopBandWindowGuard()
    {
        // 훅이 살아 있는 동안 대리자가 수집되지 않도록 필드에 둔다.
        _callback = OnWinEvent;
        _clipWatch.Tick += ClipWatch_Tick;
    }

    public bool IsRunning => _moveSizeHook != 0;

    /// <summary>띠 아랫선(물리 픽셀)을 정하고 감시를 시작한다. 이미 걸친 창은 바로 아래로 내린다.</summary>
    public void Start(int bandBottom, double dpiScale)
    {
        _bandBottom = bandBottom;
        _dpiScale = Math.Max(1, dpiScale);
        if (!IsRunning)
        {
            const uint flags = WinEventOutOfContext | WinEventSkipOwnProcess;
            _moveSizeHook = SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, 0, _callback, 0, 0, flags);
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _callback, 0, 0, flags);
            if (_moveSizeHook == 0) Trace.TraceWarning("창 이동 감시를 등록하지 못해 띠 침범 막기를 쓰지 않습니다.");
        }
        PushAllBelowBand();
    }

    public void Stop()
    {
        ReleaseCursorClip();
        if (_moveSizeHook != 0) UnhookWinEvent(_moveSizeHook);
        if (_foregroundHook != 0) UnhookWinEvent(_foregroundHook);
        _moveSizeHook = 0;
        _foregroundHook = 0;
    }

    public void Dispose()
    {
        Stop();
        _clipWatch.Tick -= ClipWatch_Tick;
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || idObject != ObjIdWindow || !IsRunning) return;
        try
        {
            switch (eventType)
            {
                case EventSystemMoveSizeStart:
                    BeginDrag(hwnd);
                    break;
                case EventSystemMoveSizeEnd:
                    ReleaseCursorClip();
                    PushBelowBand(hwnd);
                    break;
                case EventSystemForeground:
                    PushBelowBand(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 네이티브 콜백 밖으로 예외가 나가면 앱이 종료된다. 다른 앱 창을 다루다 실패해도 알통 동작은 유지한다.
            Trace.TraceWarning($"띠 침범 막기 처리 실패: {ex.GetType().Name}");
        }
    }

    /// <summary>제목 표시줄·위쪽 테두리를 잡았을 때만 커서를 띠 아래에 가둔다. 아래쪽·옆 테두리 크기 조절은 막지 않는다.</summary>
    private void BeginDrag(nint hwnd)
    {
        ReleaseCursorClip();
        if (!IsEligible(hwnd) || !GetCursorPos(out var cursor) || !TryGetVisibleBounds(hwnd, out var visible)) return;
        int fallback = (int)Math.Round(CaptionFallbackPixels * _dpiScale);
        if (!TopNotchLayout.ConstrainsTopEdge(HitTest(hwnd, cursor), cursor.Y, visible.Top, fallback)) return;
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int right = left + GetSystemMetrics(SmCxVirtualScreen);
        int bottom = top + GetSystemMetrics(SmCyVirtualScreen);
        // 주 모니터 위쪽에 다른 모니터가 있으면 가두지 않는다. 그쪽으로 옮기는 것까지 막게 되기 때문이다.
        if (top < 0) return;
        var rect = new NativeRect
        {
            Left = left,
            Top = Math.Max(top, TopNotchLayout.DragCursorMinY(cursor.Y, _bandBottom)),
            Right = right,
            Bottom = bottom,
        };
        if (rect.Bottom <= rect.Top) return;
        _cursorClipped = ClipCursor(ref rect);
        if (_cursorClipped) _clipWatch.Start();
    }

    /// <summary>끝 이벤트를 놓쳐도 마우스 버튼을 떼면 바로 커서를 풀어 준다.</summary>
    private void ClipWatch_Tick(object? sender, EventArgs e)
    {
        int button = GetSystemMetrics(SmSwapButton) != 0 ? VkRButton : VkLButton;
        if ((GetAsyncKeyState(button) & 0x8000) == 0) ReleaseCursorClip();
    }

    private void ReleaseCursorClip()
    {
        _clipWatch.Stop();
        if (!_cursorClipped) return;
        _cursorClipped = false;
        ClipCursorNative(0);
    }

    private void PushAllBelowBand()
    {
        var windows = new List<nint>();
        EnumWindowsProc collect = (hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        };
        EnumWindows(collect, 0);
        GC.KeepAlive(collect);
        foreach (var hwnd in windows) PushBelowBand(hwnd);
    }

    private void PushBelowBand(nint hwnd)
    {
        if (!IsEligible(hwnd) || !GetWindowRect(hwnd, out var frame) || !TryGetVisibleBounds(hwnd, out var visible)) return;
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        var target = TopNotchLayout.PushBelowBand(ToPixel(frame), visible, _bandBottom, screen.WorkingArea.Bottom);
        if (target is not { } moved) return;
        uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos;
        if (moved.Height == frame.Bottom - frame.Top) flags |= SwpNoSize;
        SetWindowPos(hwnd, 0, moved.Left, moved.Top, moved.Width, moved.Height, flags);
    }

    private bool IsEligible(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == _processId) return false;
        if (!TopNotchLayout.IsMovableAppWindow(GetWindowLong(hwnd, GwlStyle), GetWindowLong(hwnd, GwlExStyle))) return false;
        if (IsCloaked(hwnd) || TopNotchLayout.IsShellWindowClass(ClassName(hwnd))) return false;
        var primary = MonitorFromPoint(default, MonitorDefaultToPrimary);
        if (MonitorFromWindow(hwnd, MonitorDefaultToNull) != primary) return false;
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null || !TryGetVisibleBounds(hwnd, out var visible)) return false;
        var bounds = screen.Bounds;
        return !TopNotchLayout.CoversScreen(visible, new PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
    }

    /// <summary>창 그림자·보이지 않는 크기 조절 테두리를 뺀 실제로 보이는 창 영역.</summary>
    private static bool TryGetVisibleBounds(nint hwnd, out PixelRect bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0
            && !GetWindowRect(hwnd, out rect))
        {
            bounds = default;
            return false;
        }
        bounds = ToPixel(rect);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    /// <summary>다른 가상 데스크톱에 있거나 시스템이 감춘 창.</summary>
    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static int HitTest(nint hwnd, NativePoint cursor)
    {
        int position = (cursor.Y << 16) | (cursor.X & 0xFFFF);
        return SendMessageTimeout(hwnd, WmNcHitTest, 0, position, SmtoAbortIfHung, 100, out nint result) == 0
            ? 0
            : (int)result;
    }

    private static string? ClassName(nint hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : null;
    }

    private static PixelRect ToPixel(NativeRect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursorNative(nint rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}

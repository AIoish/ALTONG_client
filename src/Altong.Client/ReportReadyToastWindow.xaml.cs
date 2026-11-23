using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Altong.Client;

/// <summary>
/// AI 요약이 끝났음을 알리는 트레이 말풍선. 작업 영역 오른쪽 아래에서 올라오고,
/// 포커스를 가져가지 않으며, 일정 시간이 지나면 저절로 닫힌다(커서를 올리면 기다린다).
/// 언제 띄울지는 리포트 창이 정하고, 이 창은 보여 주기와 버튼 입력만 맡는다.
/// </summary>
public partial class ReportReadyToastWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    // 말풍선은 한 번에 하나만 띄운다. 같은 자리에 겹쳐 쌓이지 않게 새 말풍선이 이전 것을 닫는다.
    [ThreadStatic] private static ReportReadyToastWindow? s_current;

    private readonly DispatcherTimer _autoClose = new();
    private HwndSource? _source;
    private bool _closing;

    /// <summary>[리포트 열기]를 눌렀다. 말풍선은 스스로 닫힌다.</summary>
    public event EventHandler? OpenRequested;

    public TimeSpan AutoCloseAfter { get; set; } = TimeSpan.FromSeconds(8);

    public ReportReadyToastWindow()
    {
        InitializeComponent();
        _autoClose.Tick += (_, _) => Dismiss();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        // 읽는 중에는 닫히지 않도록 커서가 올라와 있는 동안 남은 시간을 멈춘다.
        Card.MouseEnter += (_, _) => PauseCountdown();
        Card.MouseLeave += (_, _) => { if (IsVisible && !_closing) StartCountdown(); };
    }

    /// <summary>요약 결과에 맞는 문구로 말풍선을 띄운다.</summary>
    public void Present(bool succeeded)
    {
        if (_closing) return;
        if (s_current is { } previous && !ReferenceEquals(previous, this)) previous.Dismiss();
        s_current = this;
        TitleText.Text = succeeded ? "AI 요약이 끝났어요" : "AI 요약을 만들지 못했어요";
        MessageText.Text = succeeded
            ? "집중 리포트에서 요약과 일정을 확인해 보세요."
            : "리포트에서 다시 시도할 수 있어요.";
        IconGlyph.Text = succeeded ? "" : "";
        IconGlyph.Foreground = Brush(succeeded ? "BlackSuccess" : "BlackError");
        IconBadge.Background = Brush(succeeded ? "BlackSuccessSurface" : "BlackSurface");
        IconBadge.BorderBrush = Brush(succeeded ? "BlackSuccessRule" : "BlackError");
        System.Windows.Automation.AutomationProperties.SetName(Card, $"{TitleText.Text}. {MessageText.Text}");

        Root.BeginAnimation(OpacityProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        if (!IsVisible) Show();
        UpdateLayout();
        PositionNearTray();
        // 노치 띠 같은 다른 맨 위 창보다 앞에 둔다. ShowActivated=False라 포커스는 그대로다.
        Topmost = false;
        Topmost = true;

        if (SystemParameters.ClientAreaAnimation)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            SlideTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }
        else
        {
            Root.Opacity = 1;
            SlideTransform.Y = 0;
        }
        StartCountdown();
    }

    /// <summary>말풍선을 닫는다. 한 번 쓰고 버리는 창이다.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _autoClose.Stop();
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>작업 표시줄 알림 영역과 가까운 작업 영역 오른쪽 아래에 둔다. 바깥 여백은 그림자 자리다.</summary>
    internal void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 160;
        Left = Math.Round(area.Right - width);
        Top = Math.Round(area.Bottom - height);
    }

    private void StartCountdown()
    {
        _autoClose.Stop();
        if (AutoCloseAfter <= TimeSpan.Zero) return;
        _autoClose.Interval = AutoCloseAfter;
        _autoClose.Start();
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = 1;
        if (SystemParameters.ClientAreaAnimation)
            CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, AutoCloseAfter));
    }

    private void PauseCountdown()
    {
        _autoClose.Stop();
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = 1;
    }

    private System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        // 사라지는 중에 한 번 더 눌러도 리포트를 두 번 열지 않는다.
        if (_closing) return;
        OpenRequested?.Invoke(this, EventArgs.Empty);
        Dismiss();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Alt+Tab 목록에 나오지 않고, 보이거나 눌려도 활성 창이 되지 않게 한다.
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);
    }

    private static nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WM_MOUSEACTIVATE) return 0;
        handled = true;
        return MA_NOACTIVATE;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _autoClose.Stop();
        if (ReferenceEquals(s_current, this)) s_current = null;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}

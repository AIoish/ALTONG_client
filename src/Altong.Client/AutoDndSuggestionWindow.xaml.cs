using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Altong.Client;

/// <summary>제안 카드에 보일 문구.</summary>
/// <param name="HelpText">거절 버튼을 고르면 어떻게 되는지 화면 읽기 프로그램에 알려 줄 설명.</param>
public sealed record AutoDndPrompt(string Title, string Message, string AcceptText, string DeclineText, string HelpText)
{
    /// <summary>작업을 감지해 집중 모드 진입을 제안한다.</summary>
    public static AutoDndPrompt Entry { get; } = new(
        "집중 모드를 시작할까요?",
        "작업에 몰입하고 있는 것 같아요.\n켜면 중요한 알림만 보여 드려요.",
        "집중 시작", "나중에", "한동안 다시 제안하지 않습니다.");

    /// <summary>설정한 집중 시간이 끝나 종료를 제안한다. 아무것도 누르지 않으면 집중을 이어 간다.</summary>
    public static AutoDndPrompt TimerFinished { get; } = new(
        "설정한 집중 시간이 끝났어요",
        "집중 모드를 끌까요?\n더 하려면 그대로 두셔도 돼요.",
        "집중 종료", "계속하기", "집중 모드를 그대로 유지합니다.");
}

/// <summary>
/// AutoDND 제안 카드. 화면 상단 가운데에 내려오고, 포커스를 가져가지 않아 타이핑이 끊기지 않는다.
/// 언제 띄우고 닫을지는 앱(<see cref="Services.Focus.AutoDndController"/>, 집중 타이머)이 정하고,
/// 이 창은 보여 주기와 버튼 입력만 맡는다.
/// </summary>
public partial class AutoDndSuggestionWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const double TopMargin = 10;

    private HwndSource? _source;
    private bool _closing;

    public event EventHandler? Accepted;
    public event EventHandler? Declined;

    public AutoDndSuggestionWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    /// <summary>지금 보이고 있거나 마지막으로 보인 제안 문구.</summary>
    public AutoDndPrompt Prompt { get; private set; } = AutoDndPrompt.Entry;

    /// <summary>제안 카드를 보여 준다. <paramref name="timeout"/>은 남은 시간 막대에만 쓴다.</summary>
    public void Present(TimeSpan timeout, AutoDndPrompt? prompt = null)
    {
        if (_closing) return;
        Prompt = prompt ?? AutoDndPrompt.Entry;
        TitleText.Text = Prompt.Title;
        MessageText.Text = Prompt.Message;
        AcceptButton.Content = Prompt.AcceptText;
        DeclineButton.Content = Prompt.DeclineText;
        System.Windows.Automation.AutomationProperties.SetName(Card, Prompt.Title);
        System.Windows.Automation.AutomationProperties.SetName(AcceptButton, Prompt.AcceptText);
        System.Windows.Automation.AutomationProperties.SetName(DeclineButton, Prompt.DeclineText);
        System.Windows.Automation.AutomationProperties.SetHelpText(DeclineButton, Prompt.HelpText);
        Root.BeginAnimation(OpacityProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        if (!IsVisible) Show();
        PositionAtTopCenter();
        // 다른 맨 위 창(노치 띠 등)보다 앞에 오도록 다시 올린다. ShowActivated=False라 포커스는 그대로다.
        Topmost = false;
        Topmost = true;

        bool animate = SystemParameters.ClientAreaAnimation;
        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            SlideTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        else
        {
            Root.Opacity = 1;
            SlideTransform.Y = 0;
        }

        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CountdownScale.ScaleX = 1;
        if (animate && timeout > TimeSpan.Zero)
            CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, timeout));
    }

    /// <summary>카드를 닫는다. 창은 다시 쓰기 위해 숨기기만 한다.</summary>
    public void Dismiss()
    {
        if (!IsVisible) return;
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            Hide();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            // 사라지는 사이에 다시 Present된 경우에는 숨기지 않는다.
            if (Root.Opacity == 0) Hide();
        };
        Root.BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, TimeSpan.FromMilliseconds(140)));
    }

    /// <summary>
    /// 애니메이션 없이 바로 숨긴다. [집중 시작] 직후 집중 모드를 켜는 동안 UI 스레드가 몇 초 막혀
    /// 사라지는 애니메이션이 멈춘 채 화면에 남는 것을 막는다.
    /// </summary>
    public void HideImmediately()
    {
        Root.BeginAnimation(OpacityProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        CountdownScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Root.Opacity = 0;
        if (IsVisible) Hide();
    }

    public void CloseForShutdown()
    {
        _closing = true;
        Close();
    }

    private void PositionAtTopCenter()
    {
        var area = SystemParameters.WorkArea;
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = Math.Round((screenWidth - ActualWidthOrWidth()) / 2);
        Top = Math.Max(0, area.Top) + TopMargin;
    }

    private double ActualWidthOrWidth() => ActualWidth > 0 ? ActualWidth : Width;

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Declined?.Invoke(this, EventArgs.Empty);
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
        _source?.RemoveHook(WindowProc);
        _source = null;
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}

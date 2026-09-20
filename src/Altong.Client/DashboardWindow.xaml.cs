using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Altong.Client;

/// <summary>
/// 집중 세션 상태와 세션 종료 리포트를 표시할 대시보드 화면 뼈대.
/// 실제 데이터는 공통 계약과 저장 계층이 확정된 뒤 연결한다.
/// </summary>
public partial class DashboardWindow : Window
{
    private readonly HashSet<FrameworkElement> _motionSurfaces = new();
    private bool _ambientMotionRunning;
    private bool _motionSettingsSubscribed;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public DashboardWindow()
    {
        InitializeComponent();
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        AppVersionText.Text = $"ALTONG · {typeof(DashboardWindow).Assembly.GetName().Version}";
        SourceInitialized += (_, _) => FitToWorkArea();
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
            UpdateMotion();
        };
        Loaded += (_, _) =>
        {
            _clockTimer.Start();
            if (!_motionSettingsSubscribed)
            {
                SystemParameters.StaticPropertyChanged += MotionSettingsChanged;
                _motionSettingsSubscribed = true;
            }
            UpdateMotion();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateClock(); _clockTimer.Start(); }
            else _clockTimer.Stop();
            UpdateMotion();
        };
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            if (_motionSettingsSubscribed)
                SystemParameters.StaticPropertyChanged -= MotionSettingsChanged;
            _motionSettingsSubscribed = false;
            StopMotion();
            _motionSurfaces.Clear();
        };
    }

    // Only presentation concerns belong here; session/DB/AI contracts remain unchanged.
    private void UpdateClock()
    {
        DashboardClockText.Text = DateTime.Now.ToString("HH:mm");
        DashboardDateText.Text = DateTime.Now.ToString("M월 d일 dddd");
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e) => DashboardTabs.SelectedIndex = 2;

    private void DashboardPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer viewer ||
            viewer.Content is not System.Windows.Controls.Grid grid) return;
        // Fit normal desktop sizes. Preserve access with page scrolling only on very small work areas.
        grid.Height = viewer.ActualWidth < 740 ? grid.Children.Count * 282 : Math.Max(320, viewer.ActualHeight);
        ArrangeDashboardPage(grid);
    }

    private void DashboardPageGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Grid grid) ArrangeDashboardPage(grid);
    }

    private static void ArrangeDashboardPage(System.Windows.Controls.Grid grid)
    {
        if (grid.ActualWidth <= 0 || grid.Children.Count == 0) return;
        bool narrow = grid.ActualWidth < 740;
        string kind = grid.Tag?.ToString() ?? "";
        double[] columns = kind == "Overview" ? new[] { 1.75, 1.12, 1.08 } :
            kind == "Report" ? new[] { 1.5, 1.0, 0.0 } : new[] { 1.0, 1.0, 1.0 };
        if (kind == "Settings") columns = new[] { 1.0, 1.0, 0.0 };
        for (int c = 0; c < 3; c++)
            grid.ColumnDefinitions[c].Width = new GridLength(narrow ? (c == 0 ? 1 : 0) : columns[c], GridUnitType.Star);
        grid.RowDefinitions.Clear();
        if (narrow)
        {
            for (int i = 0; i < grid.Children.Count; i++)
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(kind == "Overview" ? 1.15 : 1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(kind == "Settings" ? .42 : 1, GridUnitType.Star) });
        }
        for (int i = 0; i < grid.Children.Count; i++)
        {
            if (grid.Children[i] is not FrameworkElement card) continue;
            int row = 0, column = 0, span = 1, rowSpan = 1;
            if (narrow) row = i;
            else if (kind == "Overview") { row = i < 3 ? 0 : 1; column = i < 3 ? i : i - 3; span = i == 4 ? 2 : 1; }
            else if (kind == "Notifications") { row = i < 2 ? 0 : 1; column = i < 2 ? i : i - 2; span = i == 1 ? 2 : 1; }
            else if (kind == "Report") { column = i; rowSpan = 2; }
            else { row = i < 2 ? 0 : 1; column = i < 2 ? i : 0; span = i == 2 ? 2 : 1; }
            System.Windows.Controls.Grid.SetRow(card, row);
            System.Windows.Controls.Grid.SetColumn(card, column);
            System.Windows.Controls.Grid.SetColumnSpan(card, span);
            System.Windows.Controls.Grid.SetRowSpan(card, rowSpan);
            card.Width = double.NaN;
            card.Height = narrow ? 270 : double.NaN;
            card.Margin = new Thickness(0, 0, !narrow && column + span < (kind == "Report" || kind == "Settings" ? 2 : 3) ? 14 : 0, rowSpan == 2 ? 0 : 12);
        }
    }

    private void FitToWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var available = transform.Transform(new Vector(area.Width, area.Height));
        double width = Math.Max(1, available.X - 24);
        double height = Math.Max(1, available.Y - 24);
        MinWidth = Math.Min(900, width);
        MinHeight = Math.Min(600, height);
        Width = Math.Min(1100, width);
        Height = Math.Min(720, height);
    }

    private void DashboardTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, DashboardTabs))
            return;

        UpdateMotion();
        if (DashboardTabs.SelectedContent is UIElement selectedContent && MotionAllowed)
            FadeIn(selectedContent, 200);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private bool MotionAllowed => IsLoaded && IsVisible &&
        WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;

    private void MotionSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.Invoke(UpdateMotion);
    }

    private void UpdateMotion()
    {
        if (!MotionAllowed || DashboardTabs?.SelectedIndex != 0)
        {
            StopMotion();
            return;
        }
        if (_ambientMotionRunning)
            return;

        _ambientMotionRunning = true;
        var rotation = new RotateTransform();
        SessionOrbit.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(32))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
        SessionGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(.18, .5, TimeSpan.FromSeconds(3.2))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void FadeIn(UIElement element, int milliseconds)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void Surface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateSurface(sender, -2);
    }

    private void Surface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateSurface(sender, 0);
    }

    private void AnimateSurface(object sender, double offset)
    {
        if (!MotionAllowed || sender is not FrameworkElement surface ||
            Equals(surface.Tag, "CaptionControl") || !surface.IsEnabled)
            return;
        if (surface.RenderTransform is not TranslateTransform translation)
        {
            translation = new TranslateTransform();
            surface.RenderTransform = translation;
        }
        _motionSurfaces.Add(surface);
        // Set the resting value before animating; replace clocks on rapid pointer movement.
        double current = translation.Y;
        translation.Y = offset;
        translation.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(current, offset, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void Details_Expanded(object sender, RoutedEventArgs e)
    {
        if (MotionAllowed && sender is System.Windows.Controls.Expander expander &&
            expander.Template.FindName("ExpandedContent", expander) is UIElement content)
            FadeIn(content, 220);
    }

    private void StopMotion()
    {
        _ambientMotionRunning = false;
        if (SessionOrbit?.RenderTransform is RotateTransform rotation)
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SessionGlow?.BeginAnimation(OpacityProperty, null);
        DashboardContent?.BeginAnimation(OpacityProperty, null);
        foreach (var surface in _motionSurfaces)
        {
            if (surface.RenderTransform is TranslateTransform translation)
            {
                translation.BeginAnimation(TranslateTransform.YProperty, null);
                translation.Y = 0;
            }
        }
    }
}

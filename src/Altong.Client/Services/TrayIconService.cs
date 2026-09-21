using System.Drawing;
using System.Windows.Forms;

namespace Altong.Client.Services;

/// <summary>
/// 메인 창이 숨겨진 동안 앱을 다시 열거나 종료할 수 있는 시스템 트레이 UI.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _focusModeMenuItem;
    private Action? _openDashboard;
    private bool _isDisposed;

    public TrayIconService(
        Action openApplication,
        Action toggleFocusMode,
        Action exitApplication)
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("Altong 열기", null, (_, _) => openApplication());
        _contextMenu.Items.Add(new ToolStripSeparator());

        _focusModeMenuItem = new ToolStripMenuItem("집중 모드 시작");
        _focusModeMenuItem.Click += (_, _) => toggleFocusMode();
        _contextMenu.Items.Add(_focusModeMenuItem);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Altong 종료", null, (_, _) => exitApplication());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = SystemIcons.Application,
            Text = "Altong",
            Visible = true,
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.DoubleClick += (_, _) => openApplication();
    }

    /// <summary>
    /// 트레이 아이콘을 왼쪽 클릭했을 때 열 대시보드 동작을 연결한다.
    /// 기존 생성자 계약은 유지하여 다른 호출부와의 호환성을 보존한다.
    /// </summary>
    public void SetDashboardAction(Action openDashboard)
    {
        _openDashboard = openDashboard ?? throw new ArgumentNullException(nameof(openDashboard));
    }

    /// <summary>
    /// 집중 시간과 차단 알림 수가 준비되면 트레이 hover 툴팁을 갱신한다.
    /// </summary>
    public void UpdateSessionSummary(TimeSpan elapsed, int blockedNotificationCount)
    {
        int totalMinutes = Math.Max(0, (int)elapsed.TotalMinutes);
        int safeBlockedCount = Math.Max(0, blockedNotificationCount);
        _notifyIcon.Text = $"Altong - 집중 {totalMinutes}분 | 차단 {safeBlockedCount}건";
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _openDashboard?.Invoke();
        }
    }

    public void UpdateFocusModeState(bool isEnabled)
    {
        _focusModeMenuItem.Text = isEnabled
            ? "집중 모드 종료"
            : "집중 모드 시작";

        _notifyIcon.Text = isEnabled
            ? "Altong - 집중 모드 진행 중"
            : "Altong";
    }

    public void UpdateRoutineStatus(string status)
    {
        _notifyIcon.Text = $"Altong - {status}";
    }

    public void ShowRoutineReminder(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}

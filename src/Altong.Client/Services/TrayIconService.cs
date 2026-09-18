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
        _notifyIcon.DoubleClick += (_, _) => openApplication();
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

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}

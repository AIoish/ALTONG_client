using System.Runtime.InteropServices;

namespace Altong.Client.Services.Win32;

/// <summary>마지막 키보드·마우스 입력 이후 지난 시간을 알려 준다 (단위 테스트 시 Fake 주입용).</summary>
public interface IUserIdleDetector
{
    TimeSpan GetIdleTime();
}

/// <summary>전체화면 앱·프레젠테이션처럼 화면 위에 안내를 띄우면 안 되는 상태인지 알려 준다.</summary>
public interface IFullscreenDetector
{
    bool IsFullscreenActive();
}

/// <summary>Win32 <c>GetLastInputInfo</c>로 현재 로그인 세션의 입력 유휴 시간을 잰다.</summary>
public sealed class Win32UserIdleDetector : IUserIdleDetector
{
    public TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // 두 값 모두 부팅 후 밀리초(49.7일마다 0으로 돌아감)라 부호 없는 뺄셈으로 넘침을 흡수한다.
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}

/// <summary>
/// Shell의 <c>SHQueryUserNotificationState</c>로 전체화면 앱, D3D 전체화면, 프레젠테이션 모드를 감지한다.
/// Windows가 "지금은 알림 UI를 띄우지 말라"고 판단하는 기준과 같다.
/// </summary>
public sealed class Win32FullscreenDetector : IFullscreenDetector
{
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    public bool IsFullscreenActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return false;
            return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);
}

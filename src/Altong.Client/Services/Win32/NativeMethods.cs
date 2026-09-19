using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Altong.Client.Services.Win32;

/// <summary>
/// OS 활성 창 정보를 캡처하기 위한 Win32 User32.dll P/Invoke 선언부.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}

/// <summary>
/// OS로부터 조회한 활성 창 정보 DTO.
/// </summary>
public record WindowInfo(
    nint Handle,
    int ProcessId,
    string ProcessName,
    string WindowTitle);

/// <summary>
/// 활성 창 정보 조회 공급자 인터페이스 (단위 테스트 시 Mocking/Fake 주입용).
/// </summary>
public interface IWindowInfoProvider
{
    WindowInfo? GetForegroundWindowInfo();
}

/// <summary>
/// 실제 Windows 환경에서 활성 창 정보를 안전하게 조회하는 기본 구현체.
/// </summary>
public sealed class LiveWindowInfoProvider : IWindowInfoProvider
{
    public WindowInfo? GetForegroundWindowInfo()
    {
        nint handle = NativeMethods.GetForegroundWindow();
        if (handle == nint.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        if (processId == 0)
        {
            return null;
        }

        string processName = ResolveProcessName((int)processId);
        string windowTitle = ResolveWindowTitle(handle);

        return new WindowInfo(handle, (int)processId, processName, windowTitle);
    }

    private static string ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;

            if (string.IsNullOrWhiteSpace(name))
            {
                return "Unknown.exe";
            }

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe";
        }
        catch
        {
            return "Unknown.exe";
        }
    }

    private static string ResolveWindowTitle(nint handle)
    {
        try
        {
            int length = NativeMethods.GetWindowTextLength(handle);
            if (length <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(length + 1);
            if (NativeMethods.GetWindowText(handle, builder, builder.Capacity) > 0)
            {
                string rawTitle = builder.ToString();
                try
                {
                    return Uri.UnescapeDataString(rawTitle);
                }
                catch
                {
                    return rawTitle;
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Altong.Client.Services.Win32;

internal static class WindowAttention
{
    private const uint FlashTray = 0x00000002;

    public static void FlashTaskbar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashTray,
            Count = 3,
            Timeout = 0
        };
        _ = FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}

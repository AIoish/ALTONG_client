using System.Runtime.InteropServices;

namespace Altong.Client.Services.AI.Briefing;

/// <summary>
/// 요약 모델을 GPU(Vulkan)로 돌릴지 정한다.
/// GPU로 돌리면 요약이 약 2배 빠르지만, 내장 그래픽은 일반 메모리를 같이 써서 요약하는 동안 메모리를 약 1.2GB 더 쓴다.
/// 그래서 메모리가 적은 PC는 CPU만 쓴다. GPU 장치가 없으면 GPU를 켜도 CPU로 돈다.
/// </summary>
public static class BriefingGpuPolicy
{
    public const string OverrideVariable = "ALTONG_BRIEFING_GPU";
    public const long MinimumMemoryBytes = 12L * 1024 * 1024 * 1024;
    /// <summary>모든 층을 GPU로 올린다 (모델 층 수보다 크면 전부).</summary>
    public const int AllLayers = 999;

    /// <param name="totalMemoryBytes">PC의 실제 메모리 크기.</param>
    /// <param name="overrideValue">환경 변수 ALTONG_BRIEFING_GPU. "1"이면 켜고 "0"이면 끈다. 없으면 메모리 크기로 정한다.</param>
    public static bool ShouldUseGpu(long totalMemoryBytes, string? overrideValue) => overrideValue?.Trim() switch
    {
        "1" => true,
        "0" => false,
        _ => totalMemoryBytes >= MinimumMemoryBytes,
    };

    /// <summary>이 PC의 실제 메모리 크기. 알 수 없으면 0 (CPU만 쓰게 된다).</summary>
    public static long TotalPhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

using System.Runtime.InteropServices;

namespace Altong.Client.Services;

/// <summary>은신형 미니바의 표시 단계. 완전히 사라지지 않고 작은 점으로 남는다.</summary>
public enum StealthDockMode
{
    /// <summary>미확인 알림 없음. 흐린 작은 점만 남긴다.</summary>
    Resting,
    /// <summary>호버·패널·안내 중. 그립과 개수가 있는 캡슐로 펼친다.</summary>
    Expanded,
    /// <summary>미확인 알림이 남아 있음. 선명한 신호색의 조금 큰 점으로 남긴다.</summary>
    Alert,
}

/// <summary>
/// 은신 동작 시간. 결정 필요: 팀 합의 전 임시 기본값이며 한곳에서 조정한다.
/// </summary>
public sealed record StealthDockTiming(
    TimeSpan IdleDelay,
    TimeSpan RevealDwell,
    TimeSpan AwayThreshold)
{
    public static StealthDockTiming Default { get; } = new(
        IdleDelay: TimeSpan.FromSeconds(1),
        RevealDwell: TimeSpan.FromMilliseconds(250),
        AwayThreshold: TimeSpan.FromSeconds(30));
}

/// <param name="IsEngaged">커서가 펼친 캡슐 위에 있거나, 누르는 중, 패널·안내 표시 중, 드래그 중이다.</param>
/// <param name="UnreadCount">이번 세션의 미확인 중요 알림 개수.</param>
/// <param name="IsUserActive">최근 키보드·마우스 입력이 있어 사용자가 자리에 있다고 본다.</param>
public readonly record struct StealthDockInput(bool IsEngaged, int UnreadCount, bool IsUserActive);

/// <summary>
/// 은신형 미니바의 상태 판단. UI와 분리해 시간 흐름을 직접 넣어 검증한다.
/// 새 중요 알림은 은신을 풀지 않고 동그라미에서 빛을 한 번 낸다(<see cref="ConsumeFlash"/>).
/// 자리를 비운 동안 도착했다면 돌아와 입력을 시작할 때 한 번 낸다.
/// 미확인 알림이 남아 있으면 흐린 점이 아니라 선명한 신호색 점으로 남는다.
/// </summary>
public sealed class StealthDockBehavior
{
    private readonly StealthDockTiming _timing;
    private DateTime? _collapseAt;
    private int _lastUnreadCount;
    private bool _flashRequested;
    private bool _flashWhenBack;

    public StealthDockBehavior(StealthDockTiming? timing = null) => _timing = timing ?? StealthDockTiming.Default;

    public StealthDockTiming Timing => _timing;
    public StealthDockMode Mode { get; private set; } = StealthDockMode.Resting;

    /// <summary>호버 대기 끝, 디자인 전환 등으로 잠시 펼친다. 이후 유휴 시간이 지나면 접힌다.</summary>
    public void Reveal(DateTime now)
    {
        _collapseAt = now + _timing.IdleDelay;
        Mode = StealthDockMode.Expanded;
    }

    /// <summary>빛을 낼 차례면 true를 한 번만 돌려준다.</summary>
    public bool ConsumeFlash()
    {
        bool flash = _flashRequested;
        _flashRequested = false;
        return flash;
    }

    public StealthDockMode Evaluate(StealthDockInput input, DateTime now)
    {
        // 미확인 개수는 새 알림을 받을 때만 늘어난다.
        if (input.UnreadCount > _lastUnreadCount)
        {
            if (input.IsUserActive) _flashRequested = true;
            else _flashWhenBack = true;
        }
        _lastUnreadCount = input.UnreadCount;
        if (input.UnreadCount == 0) _flashWhenBack = false;
        if (_flashWhenBack && input.IsUserActive)
        {
            _flashWhenBack = false;
            _flashRequested = true;
        }

        if (input.IsEngaged)
        {
            _collapseAt = now + _timing.IdleDelay;
            return Mode = StealthDockMode.Expanded;
        }

        if (_collapseAt is { } until && now < until)
            return Mode = StealthDockMode.Expanded;

        _collapseAt = null;
        return Mode = input.UnreadCount > 0 ? StealthDockMode.Alert : StealthDockMode.Resting;
    }
}

/// <summary>
/// 은신형 전용 색. 평소에는 차가운 라벤더, 미확인 알림이 있을 때만 따뜻한 신호색(호박·산호)을 쓴다.
/// 색의 온도가 바뀌는 것 자체가 알림이 되도록, 따뜻한 색은 미확인 알림에만 쓴다.
/// 가로·기존 미니바의 공통 색(DockNotificationAppearance)은 바꾸지 않는다.
/// </summary>
public static class StealthDockPalette
{
    /// <summary>접힌 동그라미의 평소 상태 점. 흐림은 투명도로 따로 준다.</summary>
    public const string Rest = "#A99BFF";
    /// <summary>펼친 캡슐에서 미확인 알림이 없을 때의 상태 점.</summary>
    public const string Quiet = "#7F74BF";
    /// <summary>미확인 알림(긴급도 4 이하).</summary>
    public const string Signal = "#F2B45E";
    /// <summary>미확인 긴급 알림(긴급도 5).</summary>
    public const string UrgentSignal = "#FF8B6B";

    public static string StatusColor(int unreadCount, int highestUnreadUrgency, bool expanded) =>
        unreadCount > 0 ? (highestUnreadUrgency >= 5 ? UrgentSignal : Signal)
        : expanded ? Quiet : Rest;
}

/// <summary>마지막 키보드·마우스 입력 이후 경과 시간. 입력 내용은 읽지 않는다.</summary>
public static class UserInputIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // 두 값 모두 부팅 후 밀리초이며 약 49일마다 함께 순환한다.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }
}

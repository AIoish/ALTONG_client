using Altong.Client.Services.Notifications;

namespace Altong.Client.Tests.Mocks;

public sealed class FakeKakaoAudioOperator : IKakaoAudioOperator
{
    public bool IsMuted { get; private set; }
    public int MuteCallCount { get; private set; }
    public int UnmuteCallCount { get; private set; }

    public void Mute()
    {
        IsMuted = true;
        MuteCallCount++;
    }

    public void Unmute()
    {
        IsMuted = false;
        UnmuteCallCount++;
    }

    public void Dispose()
    {
        Unmute();
    }
}

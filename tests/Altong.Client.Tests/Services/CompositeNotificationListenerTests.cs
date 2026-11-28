using Altong.Client.Models;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class CompositeNotificationListenerTests
{
    [TestMethod]
    public async Task CompositeListener_AggregatesNotificationsFromMultipleSources()
    {
        using var fakeWinRt = new FakeNotificationListener();
        using var fakeKakao = new FakeNotificationListener();
        using var composite = new CompositeNotificationListener(fakeWinRt, fakeKakao);

        var receivedNotifications = new List<RawNotification>();
        composite.NotificationReceived += (_, noti) => receivedNotifications.Add(noti);

        bool started = await composite.StartAsync();
        Assert.IsTrue(started);
        Assert.IsTrue(composite.IsRunning);

        // WinRT 소스에서 알림 발생
        var winRtNoti = new RawNotification("win_1", "Slack", "김철수", "회의", "참석 요청", DateTime.UtcNow);
        fakeWinRt.EmitNotification(winRtNoti);

        // 카카오톡 소스에서 알림 발생
        var kakaoNoti = new RawNotification("kakao_1", "KakaoTalk.exe", "이영희", "점심", "밥 먹자", DateTime.UtcNow);
        fakeKakao.EmitNotification(kakaoNoti);

        Assert.AreEqual(2, receivedNotifications.Count);
        Assert.AreEqual("Slack", receivedNotifications[0].AppName);
        Assert.AreEqual("KakaoTalk.exe", receivedNotifications[1].AppName);
    }

    [TestMethod]
    public async Task CompositeListener_StopAndDispose_PropagatesToChildren()
    {
        var fake1 = new FakeNotificationListener();
        var fake2 = new FakeNotificationListener();
        var composite = new CompositeNotificationListener(fake1, fake2);

        await composite.StartAsync();
        Assert.IsTrue(fake1.IsRunning);
        Assert.IsTrue(fake2.IsRunning);

        composite.Stop();
        Assert.IsFalse(fake1.IsRunning);
        Assert.IsFalse(fake2.IsRunning);

        composite.Dispose();
    }
}

using Altong.Client.Models;
using Altong.Client.Services.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationListenerTests
{
    [TestMethod]
    public void ParseNotification_StandardTwoLines_ExtractsTitleAndBody()
    {
        uint id = 101;
        string app = "KakaoTalk";
        string[] lines = ["김철수", "오늘 회의 몇 시인가요?"];
        var creationTime = DateTimeOffset.UtcNow;

        RawNotification raw = WinRtNotificationListener.ParseNotification(id, app, lines, creationTime);

        Assert.AreEqual("KakaoTalk", raw.AppName);
        Assert.AreEqual("김철수", raw.Title);
        Assert.AreEqual("오늘 회의 몇 시인가요?", raw.Body);
        Assert.IsTrue(raw.Id.StartsWith($"win_{id}_"));
    }

    [TestMethod]
    public void ParseNotification_MockGeneratorAttribution_SplitsAppNameAndSender()
    {
        // Altong.MockGenerator는 AttributionText에 "{앱이름} · {발신자}"를 전달함
        uint id = 102;
        string app = "Altong.MockGenerator";
        string[] lines =
        [
            "[긴급] 서버 배포 오류",
            "102번 서버 에러 긴급 핫픽스 요청",
            "Slack · 김철수 팀장"
        ];
        var creationTime = DateTimeOffset.UtcNow;

        RawNotification raw = WinRtNotificationListener.ParseNotification(id, app, lines, creationTime);

        Assert.AreEqual("Slack", raw.AppName, "MockGenerator Attribution에서 실제 발신 앱을 추출해야 합니다.");
        Assert.AreEqual("김철수 팀장", raw.Sender, "MockGenerator Attribution에서 실제 발신자를 추출해야 합니다.");
        Assert.AreEqual("[긴급] 서버 배포 오류", raw.Title);
        Assert.AreEqual("102번 서버 에러 긴급 핫픽스 요청", raw.Body);
    }

    [TestMethod]
    public void ParseNotification_SingleLine_TitleOnly()
    {
        uint id = 103;
        string app = "System";
        string[] lines = ["업데이트가 완료되었습니다."];
        var creationTime = DateTimeOffset.UtcNow;

        RawNotification raw = WinRtNotificationListener.ParseNotification(id, app, lines, creationTime);

        Assert.AreEqual("System", raw.AppName);
        Assert.AreEqual("업데이트가 완료되었습니다.", raw.Title);
        Assert.AreEqual(string.Empty, raw.Body);
    }

    [TestMethod]
    public void FakeNotificationListener_EmitNotification_RaisesEvent()
    {
        using var fake = new FakeNotificationListener();
        RawNotification? received = null;
        fake.NotificationReceived += (_, noti) => received = noti;

        // 실행 전에는 이벤트 무시
        fake.EmitNotification(new RawNotification("id1", "Slack", "Sender", "T1", "B1", DateTime.UtcNow));
        Assert.IsNull(received);

        // 가동 후 수신
        fake.StartAsync().Wait();
        var expected = new RawNotification("id2", "Slack", "김팀장", "서버 장애", "확인 필요", DateTime.UtcNow);
        fake.EmitNotification(expected);

        Assert.IsNotNull(received);
        Assert.AreEqual("id2", received.Id);
        Assert.AreEqual("Slack", received.AppName);
        Assert.AreEqual("김팀장", received.Sender);
        Assert.AreEqual("서버 장애", received.Title);
    }
}

using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class KakaoNotificationInterceptorTests
{
    [TestMethod]
    public void HandleWindowShowEvent_NotNotificationWindow_Ignored()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        nint regularWindowHwnd = 12345;
        interceptor.HandleWindowShowEvent(regularWindowHwnd);

        Assert.IsNull(received, "알림 창이 아닌 윈도우는 이벤트를 무시해야 합니다.");
        Assert.AreEqual(0, fakeOp.HiddenWindows.Count);
    }

    [TestMethod]
    public void HandleWindowShowEvent_FocusModeOff_DoesNotHideWindow_EmitsNotification()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 99999;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = false;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);

        Assert.IsNotNull(received);
        Assert.AreEqual("KakaoTalk.exe", received.AppName);
        Assert.AreEqual("팀원A", received.Sender);
        Assert.AreEqual("오늘 회의 언제 시작하나요?", received.Body);
        Assert.AreEqual(0, fakeOp.HiddenWindows.Count, "집중 모드가 꺼져 있을 때는 창을 숨기지 않아야 합니다.");
    }

    [TestMethod]
    public void HandleWindowShowEvent_FocusModeOn_StealthHidesImmediately_EmitsNotification()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 88888;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);

        Assert.IsNotNull(received);
        Assert.IsTrue(fakeOp.HiddenWindows.Contains(notiHwnd), "집중 모드 활성화 시 알림 창을 0ms 스텔스 숨김 처리해야 합니다.");
    }

    [TestMethod]
    public void OnNotificationProcessed_PassedNotification_RestoresWindow()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 77777;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);
        Assert.IsNotNull(received);

        // AI 판정 결과: 통과(IsPassed = true)
        var record = new NotificationRecord(
            Id: received.Id,
            AppName: received.AppName,
            Sender: received.Sender,
            Title: received.Title,
            Body: received.Body,
            ReceivedAt: received.Timestamp,
            IsPassed: true,
            UrgencyScore: 5,
            RelevanceScore: 5,
            Category: "업무",
            AiSummaryReason: "긴급 업무 알림");

        interceptor.OnNotificationProcessed(record);

        Assert.IsTrue(fakeOp.ShownWindows.Contains(notiHwnd), "AI 통과 판정을 받은 알림 창은 화면에 다시 표시되어야 합니다.");
        Assert.AreEqual(0, fakeOp.ClosedWindows.Count);
    }

    [TestMethod]
    public void OnNotificationProcessed_BlockedNotification_ClosesWindow()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 66666;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);
        Assert.IsNotNull(received);

        // AI 판정 결과: 차단(IsPassed = false)
        var record = new NotificationRecord(
            Id: received.Id,
            AppName: received.AppName,
            Sender: received.Sender,
            Title: received.Title,
            Body: received.Body,
            ReceivedAt: received.Timestamp,
            IsPassed: false,
            UrgencyScore: 1,
            RelevanceScore: 1,
            Category: "잡담",
            AiSummaryReason: "단순 잡담 알림");

        interceptor.OnNotificationProcessed(record);

        Assert.IsTrue(fakeOp.ClosedWindows.Contains(notiHwnd), "AI 차단 판정을 받은 알림 창은 조용히 소멸(WM_CLOSE)되어야 합니다.");
        Assert.AreEqual(0, fakeOp.ShownWindows.Count);
    }
}

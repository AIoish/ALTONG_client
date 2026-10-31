using Altong.Client.Models;
using Altong.Client.Services.Notifications;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class RuleBasedFilterEngineTests
{
    private readonly RuleBasedFilterEngine _engine = new();

    [TestMethod]
    public async Task EvaluateAsync_UrgentKeyword_ReturnsPassedWithHighUrgency()
    {
        var noti = new RawNotification(
            "noti_01", "Slack", "김팀장", "[긴급] DB 서버 다운 발생", "즉시 확인 부탁드립니다.", DateTime.UtcNow);
        var context = new CurrentContext("Code.exe", "Controller.cs", DateTime.UtcNow);

        FilterResult result = await _engine.EvaluateAsync(noti, context);

        Assert.IsTrue(result.IsPassed, "긴급 키워드가 포함된 알림은 통과되어야 합니다.");
        Assert.AreEqual(5, result.UrgencyScore);
        Assert.AreEqual("긴급 업무", result.Category);
    }

    [TestMethod]
    public async Task EvaluateAsync_ContextRelevant_ReturnsPassedWithRelevanceScore()
    {
        var noti = new RawNotification(
            "noti_02", "GitHub", "bot", "PR #42 review requested", "Merge ready", DateTime.UtcNow);
        var context = new CurrentContext("Code.exe", "Controller.cs", DateTime.UtcNow);

        FilterResult result = await _engine.EvaluateAsync(noti, context);

        Assert.IsTrue(result.IsPassed, "코딩 작업 중 GitHub 알림은 업무 연관으로 통과되어야 합니다.");
        Assert.IsTrue(result.RelevanceScore >= 4);
        Assert.AreEqual("업무 연관", result.Category);
    }

    [TestMethod]
    public async Task EvaluateAsync_LowPriorityKeyword_ReturnsBlocked()
    {
        var noti = new RawNotification(
            "noti_03", "Coupang", "", "[광고] 오늘만 치킨 50% 할인 쿠폰", "주문하기", DateTime.UtcNow);
        var context = new CurrentContext("Code.exe", "Controller.cs", DateTime.UtcNow);

        FilterResult result = await _engine.EvaluateAsync(noti, context);

        Assert.IsFalse(result.IsPassed, "광고/할인 알림은 차단되어야 합니다.");
        Assert.AreEqual(1, result.UrgencyScore);
        Assert.AreEqual("일반/잡담", result.Category);
    }

    [TestMethod]
    public async Task EvaluateAsync_NormalNotification_ReturnsBlockedInFocusMode()
    {
        var noti = new RawNotification(
            "noti_04", "KakaoTalk", "동기", "오늘 날씨 좋다", "산책 갈래?", DateTime.UtcNow);
        var context = new CurrentContext("Word.exe", "보고서.docx", DateTime.UtcNow);

        FilterResult result = await _engine.EvaluateAsync(noti, context);

        Assert.IsFalse(result.IsPassed, "평범한 잡담 알림은 집중 모드에서 차단되어야 합니다.");
        Assert.AreEqual("일반 알림", result.Category);
    }
}

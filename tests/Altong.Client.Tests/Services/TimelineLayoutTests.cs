using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class TimelineLayoutTests
{
    [TestMethod]
    public void Stack_KeepsTimesWhenApart_PushesOverlapsDown()
    {
        var tops = TimelineLayout.Stack([(10, 30), (100, 30), (110, 30)], 400);
        Assert.AreEqual(10, tops[0]);
        Assert.AreEqual(100, tops[1]);
        Assert.AreEqual(133, tops[2], "겹치면 바로 아래로 밀어야 합니다.");
    }

    [TestMethod]
    public void Stack_NearBottom_PullsUp_SoNothingOverlapsOrLeavesCanvas()
    {
        // 저녁 늦게 짧은 집중 세 번: 그대로 밀면 바닥을 넘는다.
        var blocks = new (double Top, double Height)[] { (380, 30), (385, 30), (390, 30) };
        var tops = TimelineLayout.Stack(blocks, 400);
        for (int i = 0; i < tops.Length; i++)
        {
            Assert.IsTrue(tops[i] >= 0 && tops[i] + blocks[i].Height <= 400, "캔버스 안에 있어야 합니다.");
            if (i > 0) Assert.IsTrue(tops[i] >= tops[i - 1] + blocks[i - 1].Height, "블록끼리 겹치면 안 됩니다.");
        }
    }
}

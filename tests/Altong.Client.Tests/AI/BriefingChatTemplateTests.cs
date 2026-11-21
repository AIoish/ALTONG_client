using Altong.Client.Services.AI.Briefing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.AI;

[TestClass]
public sealed class BriefingChatTemplateTests
{
    [DataTestMethod]
    [DataRow("gemma4", BriefingChatTemplate.Gemma4)]
    [DataRow("Gemma4", BriefingChatTemplate.Gemma4)]
    [DataRow("qwen3", BriefingChatTemplate.QwenChatMl)]
    [DataRow("gemma3", BriefingChatTemplate.QwenChatMl)]
    [DataRow(null, BriefingChatTemplate.QwenChatMl)]
    public void TemplateForArchitecture_PicksGemma4OnlyForGemma4(string? architecture, BriefingChatTemplate expected)
    {
        Assert.AreEqual(expected, BriefingPromptBuilder.TemplateForArchitecture(architecture));
    }

    [TestMethod]
    public void Gemma4_FollowsOfficialTurnFormatWithoutThinking()
    {
        // Gemma 4 공식 템플릿: <bos><|turn>system\n{trim}<turn|>\n<|turn>user\n{trim}<turn|>\n<|turn>model\n
        Assert.AreEqual("<|turn>system\n지시문<turn|>\n",
            BriefingPromptBuilder.RenderPrefix(BriefingChatTemplate.Gemma4, "지시문\n"));
        Assert.AreEqual("<|turn>user\n알림 묶음<turn|>\n<|turn>model\n",
            BriefingPromptBuilder.RenderSuffix(BriefingChatTemplate.Gemma4, "알림 묶음"));
        Assert.IsTrue(BriefingPromptBuilder.AddBos(BriefingChatTemplate.Gemma4));
        CollectionAssert.AreEqual(new[] { "<turn|>", "<|turn>" },
            BriefingPromptBuilder.StopMarkers(BriefingChatTemplate.Gemma4).ToArray());
        StringAssert.DoesNotMatch(BriefingPromptBuilder.RenderPrefix(BriefingChatTemplate.Gemma4, "x"),
            new System.Text.RegularExpressions.Regex("<\\|think\\|>"), "사고 모드를 켜지 않는다");
    }

    [TestMethod]
    public void QwenChatMl_KeepsExistingFormat()
    {
        const string sys = "지시문";
        const string user = "알림 묶음";
        Assert.AreEqual(BriefingPromptBuilder.RenderChatMlPrefix(sys),
            BriefingPromptBuilder.RenderPrefix(BriefingChatTemplate.QwenChatMl, sys));
        Assert.AreEqual(BriefingPromptBuilder.RenderChatMlSuffix(user),
            BriefingPromptBuilder.RenderSuffix(BriefingChatTemplate.QwenChatMl, user));
        Assert.IsFalse(BriefingPromptBuilder.AddBos(BriefingChatTemplate.QwenChatMl));
        CollectionAssert.Contains(BriefingPromptBuilder.StopMarkers(BriefingChatTemplate.QwenChatMl).ToArray(), "<|im_end|>");
    }

    [DataTestMethod]
    [DataRow(16L, null, true, DisplayName = "메모리 16GB는 GPU")]
    [DataRow(8L, null, false, DisplayName = "메모리 8GB는 CPU")]
    [DataRow(32L, "0", false, DisplayName = "환경 변수 0이면 끔")]
    [DataRow(4L, "1", true, DisplayName = "환경 변수 1이면 켬")]
    [DataRow(16L, "auto", true, DisplayName = "모르는 값은 메모리로 판단")]
    public void GpuPolicy_UsesGpuOnlyWithEnoughMemoryUnlessOverridden(long memoryGb, string? overrideValue, bool expected)
    {
        Assert.AreEqual(expected, BriefingGpuPolicy.ShouldUseGpu(memoryGb * 1024 * 1024 * 1024, overrideValue));
    }

    [TestMethod]
    public void GpuPolicy_ReadsPhysicalMemory()
    {
        Assert.IsTrue(BriefingGpuPolicy.TotalPhysicalMemoryBytes() > 1024L * 1024 * 1024);
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Altong.Client.Services.AI.Briefing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class BriefingPromptParityTests
{
    private sealed record FixtureNotification(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body);

    private sealed record FixtureInput(
        [property: JsonPropertyName("app_name")] string AppName,
        [property: JsonPropertyName("sender")] string Sender,
        [property: JsonPropertyName("notifications")] IReadOnlyList<FixtureNotification> Notifications);

    private sealed record BriefingFixture(
        [property: JsonPropertyName("case_id")] string CaseId,
        [property: JsonPropertyName("input")] FixtureInput Input,
        [property: JsonPropertyName("max_summary_lines")] int MaxSummaryLines,
        [property: JsonPropertyName("expected_system_prompt")] string ExpectedSystemPrompt,
        [property: JsonPropertyName("expected_user_prompt")] string ExpectedUserPrompt,
        [property: JsonPropertyName("expected_category")] string ExpectedCategory);

    private static IReadOnlyList<BriefingFixture> LoadFixtures()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "briefing_prompt_fixtures.json");
        if (!File.Exists(fixturePath))
        {
            fixturePath = Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "Fixtures", "briefing_prompt_fixtures.json");
        }

        Assert.IsTrue(File.Exists(fixturePath), $"Fixture file not found at {fixturePath}");
        var json = File.ReadAllText(fixturePath);
        var fixtures = JsonSerializer.Deserialize<List<BriefingFixture>>(json);
        Assert.IsNotNull(fixtures);
        return fixtures;
    }

    [TestMethod]
    public void SystemPrompt_MatchesExpected_ByteForByte()
    {
        var fixtures = LoadFixtures();
        Assert.IsTrue(fixtures.Count > 0);

        var actualSystemPrompt = BriefingPromptBuilder.BuildSystemPrompt();

        foreach (var fixture in fixtures)
        {
            Assert.AreEqual(fixture.ExpectedSystemPrompt, actualSystemPrompt, $"System prompt mismatch for case {fixture.CaseId}");
        }
    }

    [TestMethod]
    public void UserPrompts_MatchAll38EvaluationCases_ByteForByte()
    {
        var fixtures = LoadFixtures();
        Assert.AreEqual(38, fixtures.Count);

        foreach (var fixture in fixtures)
        {
            var promptItems = fixture.Input.Notifications.Select(n =>
                new NotificationPromptItem(
                    Timestamp: n.Timestamp,
                    Title: n.Title,
                    Body: n.Body
                )).ToList();

            var actualUserPrompt = BriefingPromptBuilder.BuildUserPrompt(
                appName: fixture.Input.AppName,
                sender: fixture.Input.Sender,
                notifications: promptItems,
                maxSummaryLines: fixture.MaxSummaryLines
            );

            Assert.AreEqual(
                fixture.ExpectedUserPrompt,
                actualUserPrompt,
                $"User prompt mismatch for case_id '{fixture.CaseId}'");
        }
    }

    [TestMethod]
    [DataRow("Slack", "slack")]
    [DataRow("Teams.exe", "teams")]
    [DataRow("KakaoTalk.exe", "kakaotalk")]
    [DataRow("Microsoft Teams.exe", "microsoft teams")]
    [DataRow("  Discord   ", "discord")]
    [DataRow("", "")]
    public void NormalizeAppIdentity_HandlesVariations(string input, string expected)
    {
        var actual = BriefingPromptBuilder.NormalizeAppIdentity(input);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void RenderChatMl_FormatsValidQwenChatMlStructure()
    {
        var sys = BriefingPromptBuilder.BuildSystemPrompt();
        var user = "사용자 메시지";
        var rendered = BriefingPromptBuilder.RenderChatMl(sys, user);

        StringAssert.StartsWith(rendered, "<|im_start|>system\n");
        StringAssert.Contains(rendered, "<|im_end|>\n<|im_start|>user\n");
        StringAssert.EndsWith(rendered, "<|im_end|>\n<|im_start|>assistant\n");
    }
}

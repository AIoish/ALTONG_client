using System.IO;
using System.Text.Json;
using Altong.Filtering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.AI;

[TestClass]
public sealed class RelevanceScaleTests
{
    private static JsonElement Manifest(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public void Positions_ReadsThreeLevelMapFromManifest()
    {
        // filtering-v3: 관련도 3단계를 5칸 출력의 1·2·5번째 칸에 둔다.
        var positions = RelevanceScale.Positions(Manifest("""
            {"relevance_classes_trained":3,"relevance_score_to_app":{"1":1,"2":2,"3":5}}
            """));

        CollectionAssert.AreEqual(new[] { 0, 1, 4 }, positions);
        Assert.AreEqual(3, RelevanceScale.PassScore(positions.Length));
    }

    [TestMethod]
    public void Positions_WithoutMap_KeepsLegacyFiveLevels()
    {
        var positions = RelevanceScale.Positions(Manifest("""{"model_sha256":"x"}"""));

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, positions);
        Assert.AreEqual(4, RelevanceScale.PassScore(positions.Length), "예전 5단계 모델은 관련도 4 이상이면 통과");
    }

    [DataTestMethod]
    [DataRow("""{"relevance_score_to_app":{"1":1,"3":5}}""", DisplayName = "단계가 빠짐")]
    [DataRow("""{"relevance_score_to_app":{"1":2,"2":1,"3":5}}""", DisplayName = "칸 순서가 거꾸로")]
    [DataRow("""{"relevance_score_to_app":{"1":1,"2":2,"3":6}}""", DisplayName = "칸 범위 밖")]
    [DataRow("""{"relevance_classes_trained":2,"relevance_score_to_app":{"1":1,"2":2,"3":5}}""", DisplayName = "학습 단계 수와 다름")]
    [DataRow("""{"relevance_score_to_app":[1,2,5]}""", DisplayName = "객체가 아님")]
    public void Positions_RejectsBrokenMap(string json)
    {
        Assert.ThrowsException<InvalidDataException>(() => RelevanceScale.Positions(Manifest(json)));
    }

    [TestMethod]
    public void Decode_OnlyChoosesTrainedSlots()
    {
        int[] positions = [0, 1, 4];
        // 학습하지 않은 3·4번째 칸이 가장 커도 고르지 않는다.
        float[] logits = [0.1f, 0.2f, 9f, 9f, 0.3f];

        Assert.AreEqual(3, RelevanceScale.Decode(logits, positions));
        Assert.AreEqual(2, RelevanceScale.Decode([0.1f, 0.5f, 9f, 9f, 0.3f], positions));
        Assert.AreEqual(1, RelevanceScale.Decode([0.5f, 0.5f, 0f, 0f, 0.5f], positions), "동점이면 낮은 단계");
    }

    [TestMethod]
    public void Decode_LegacyPositions_MatchesPlainArgmax()
    {
        Assert.AreEqual(4, RelevanceScale.Decode([0f, 1f, 2f, 3f, 2.5f], RelevanceScale.LegacyPositions));
    }

    [TestMethod]
    public void LevelOfAppScore_MapsReleaseScoresToLevels()
    {
        int[] positions = [0, 1, 4];

        Assert.AreEqual(1, RelevanceScale.LevelOfAppScore(1, positions));
        Assert.AreEqual(2, RelevanceScale.LevelOfAppScore(2, positions));
        Assert.AreEqual(3, RelevanceScale.LevelOfAppScore(5, positions));
        Assert.IsNull(RelevanceScale.LevelOfAppScore(4, positions));
    }
}

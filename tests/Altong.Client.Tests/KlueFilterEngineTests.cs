using System.Security.Cryptography;
using System.Text.Json;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Altong.Filtering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class KlueFilterEngineTests
{
    private sealed class FakeWindowTracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext { get; set; } = CurrentContext.Empty;
#pragma warning disable CS0067
        public event EventHandler<CurrentContext>? ContextChanged;
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;
#pragma warning restore CS0067
        public void Start() { }
        public void Stop() { }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Dispose() { }
    }

    [TestMethod]
    public async Task EvaluateAsync_ManifestHashMismatch_FailsOpenSafely()
    {
        // Arrange: Create a temporary directory with mismatched manifest to trigger InvalidDataException in KlueFilter
        string tempDir = Path.Combine(Path.GetTempPath(), "altong_test_mismatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string modelFile = Path.Combine(tempDir, "model.onnx");
            string tokenizerFile = Path.Combine(tempDir, "tokenizer.json");
            string manifestFile = Path.Combine(tempDir, "manifest.json");

            File.WriteAllText(modelFile, "fake model bytes");
            File.WriteAllText(tokenizerFile, "fake tokenizer bytes");

            var manifestObj = new
            {
                model_sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                tokenizer_sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
            };
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifestObj));

            // Verify that KlueFilter directly throws InvalidDataException for this setup
            Assert.ThrowsException<InvalidDataException>(() => new KlueFilter(modelFile, tokenizerFile));

            // Act: KlueFilterEngine should catch InvalidDataException and fail open
            using var engine = new KlueFilterEngine(modelFile, tokenizerFile);
            await engine.InitializeAsync();

            var notification = new RawNotification(
                "test-id", "KakaoTalk", "동료", "회의 공지", "10분 후 회의실 A", DateTime.UtcNow);
            var context = new CurrentContext("Slack.exe", "일반", DateTime.UtcNow);

            var result = await engine.EvaluateAsync(notification, context);

            // Assert: Must fail-open (pass notification) with diagnostic failure reason
            Assert.IsTrue(result.IsPassed, "모델 해시 불일치 시 알림 차단 없이 Fail-open(통과) 처리되어야 합니다.");
            Assert.AreEqual(0, result.UrgencyScore);
            Assert.AreEqual(0, result.RelevanceScore);
            Assert.AreEqual("판정 실패", result.Category);
            StringAssert.Contains(result.AiSummaryReason, "AI 평가 실패로 통과 (model_unavailable)");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task EvaluateAsync_MissingModelFile_FailsOpenSafely()
    {
        string nonExistentModel = Path.Combine(Path.GetTempPath(), "missing_model_" + Guid.NewGuid().ToString("N") + ".onnx");
        string nonExistentTokenizer = Path.Combine(Path.GetTempPath(), "missing_tok_" + Guid.NewGuid().ToString("N") + ".json");

        using var engine = new KlueFilterEngine(nonExistentModel, nonExistentTokenizer);
        await engine.InitializeAsync();

        var notification = new RawNotification(
            "test-id-2", "Slack", "매니저", "긴급 점검", "서버 긴급 점검 예정", DateTime.UtcNow);
        var context = new CurrentContext("Code.exe", "main.cs", DateTime.UtcNow);

        var result = await engine.EvaluateAsync(notification, context);

        Assert.IsTrue(result.IsPassed, "파일 부재 시에도 Fail-open(통과) 처리되어야 합니다.");
        Assert.AreEqual(0, result.UrgencyScore);
        Assert.AreEqual("판정 실패", result.Category);
    }

    [TestMethod]
    public async Task EvaluateAsync_CorruptedJsonManifestType_ThrowsInvalidOperationException_FailsOpenSafely()
    {
        // Arrange: manifest with non-string property causes JsonElement.GetString() to throw InvalidOperationException
        string tempDir = Path.Combine(Path.GetTempPath(), "altong_corrupt_type_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string modelFile = Path.Combine(tempDir, "model.onnx");
            string tokenizerFile = Path.Combine(tempDir, "tokenizer.json");
            string manifestFile = Path.Combine(tempDir, "manifest.json");

            File.WriteAllText(modelFile, "fake model bytes");
            File.WriteAllText(tokenizerFile, "fake tokenizer bytes");

            var manifestObj = new
            {
                model_sha256 = 123456789,
                tokenizer_sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
            };
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifestObj));

            // Verify that KlueFilter throws InvalidOperationException for numeric sha256 property
            Assert.ThrowsException<InvalidOperationException>(() => new KlueFilter(modelFile, tokenizerFile));

            // Act: KlueFilterEngine must catch InvalidOperationException and fail open safely
            using var engine = new KlueFilterEngine(modelFile, tokenizerFile);
            await engine.InitializeAsync();

            var notification = new RawNotification(
                "test-corrupt-type", "Slack", "시스템", "점검 안내", "정기 점검 안내", DateTime.UtcNow);
            var context = new CurrentContext("Explorer.exe", "탐색기", DateTime.UtcNow);

            var result = await engine.EvaluateAsync(notification, context);

            // Assert: Must fail open with diagnostic failure reason and safe disposal
            Assert.IsTrue(result.IsPassed, "잘못된 JSON 타입 예외 발생 시 Fail-open으로 통과되어야 합니다.");
            Assert.AreEqual("판정 실패", result.Category);
            Assert.AreEqual(0, result.UrgencyScore);
            StringAssert.Contains(result.AiSummaryReason, "model_unavailable");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task NotificationPipeline_WhenKlueEngineManifestMismatch_FailsOpenAndPersistsRecord()
    {
        // Arrange: End-to-end integration test with NotificationPipelineCoordinator
        string tempDir = Path.Combine(Path.GetTempPath(), "altong_pipeline_mismatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string modelFile = Path.Combine(tempDir, "model.onnx");
            string tokenizerFile = Path.Combine(tempDir, "tokenizer.json");
            string manifestFile = Path.Combine(tempDir, "manifest.json");

            File.WriteAllText(modelFile, "fake model bytes");
            File.WriteAllText(tokenizerFile, "fake tokenizer bytes");

            var manifestObj = new
            {
                model_sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                tokenizer_sha256 = "1111111111111111111111111111111111111111111111111111111111111111"
            };
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifestObj));

            using var database = SqliteDatabase.CreateInMemory();
            database.Initialize();
            var repo = new SqliteNotificationRepository(database);
            using var fakeListener = new FakeNotificationListener();
            var fakeTracker = new FakeWindowTracker();

            using var engine = new KlueFilterEngine(modelFile, tokenizerFile);
            await engine.InitializeAsync();

            bool isFocusMode = true;
            string sessionId = "test-session-hash-mismatch";

            using var coordinator = new NotificationPipelineCoordinator(
                fakeListener,
                fakeTracker,
                repo,
                engine,
                () => isFocusMode,
                () => sessionId);

            await coordinator.StartAsync();

            var rawNoti = new RawNotification(
                "noti-hash-mismatch-01", "Slack", "동료", "회의 알림", "잠시 회의 가능하신가요?", DateTime.UtcNow);

            var tcs = new TaskCompletionSource<NotificationRecord>();
            coordinator.NotificationProcessed += (_, r) => tcs.TrySetResult(r);

            fakeListener.EmitNotification(rawNoti);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(3000));
            Assert.AreEqual(tcs.Task, completed, "알림 파이프라인 처리가 시간 내에 완료되어야 합니다.");

            var record = await tcs.Task;
            Assert.AreEqual("noti-hash-mismatch-01", record.Id);
            Assert.IsTrue(record.IsPassed == true, "매니페스트 해시 불일치 시 Fail-open으로 통과되어야 합니다.");
            Assert.AreEqual("판정 실패", record.Category);

            // DB 영속성 검증
            var fromDb = await repo.GetByIdAsync("noti-hash-mismatch-01");
            Assert.IsNotNull(fromDb, "Fail-open 처리된 알림이 DB에 정상 영속화되어야 합니다.");
            Assert.IsTrue(fromDb.IsPassed == true);
            Assert.AreEqual(sessionId, fromDb.SessionId);

            // 세션 드레인 검증
            await coordinator.DrainSessionAsync(sessionId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void RealModelAndTokenizer_HashesMatchManifest()
    {
        string baseDir = AppContext.BaseDirectory;
        string modelDir = Path.Combine(baseDir, "models", "filtering", "klue-base-safety-v1");
        string manifestPath = Path.Combine(modelDir, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            modelDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "models", "filtering", "klue-base-safety-v1"));
            manifestPath = Path.Combine(modelDir, "manifest.json");
        }

        Assert.IsTrue(File.Exists(manifestPath), $"manifest.json not found at {manifestPath}");

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string expectedModelSha = manifestDoc.RootElement.GetProperty("model_sha256").GetString()!;
        string expectedTokenizerSha = manifestDoc.RootElement.GetProperty("tokenizer_sha256").GetString()!;

        string modelPath = Path.Combine(modelDir, "model-fp32.onnx");
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        // tokenizer.json is tracked in git and must always exist with correct LF hash
        Assert.IsTrue(File.Exists(tokenizerPath), $"tokenizer.json not found at {tokenizerPath}");
        using (var tokenizerStream = File.OpenRead(tokenizerPath))
        {
            string actualTokenizerSha = Convert.ToHexString(SHA256.HashData(tokenizerStream)).ToLowerInvariant();
            Assert.AreEqual(expectedTokenizerSha.ToLowerInvariant(), actualTokenizerSha, "tokenizer.json 해시가 manifest와 일치해야 합니다 (LF 줄바꿈 보존).");
        }

        // model-fp32.onnx is an untracked binary (in .gitignore). If present, verify its SHA256.
        if (File.Exists(modelPath))
        {
            using var modelStream = File.OpenRead(modelPath);
            string actualModelSha = Convert.ToHexString(SHA256.HashData(modelStream)).ToLowerInvariant();
            Assert.AreEqual(expectedModelSha.ToLowerInvariant(), actualModelSha, "model-fp32.onnx 해시가 manifest와 일치해야 합니다.");
        }
    }

    [TestMethod]
    public async Task RealKlueFilterEngine_LoadsAndEvaluatesSuccessfully()
    {
        string baseDir = AppContext.BaseDirectory;
        string modelDir = Path.Combine(baseDir, "models", "filtering", "klue-base-safety-v1");
        string modelPath = Path.Combine(modelDir, "model-fp32.onnx");
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            modelDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "models", "filtering", "klue-base-safety-v1"));
            modelPath = Path.Combine(modelDir, "model-fp32.onnx");
            tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        }

        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            Assert.Inconclusive("Real model or tokenizer file not found.");
            return;
        }

        using var engine = new KlueFilterEngine(modelPath, tokenizerPath);
        await engine.InitializeAsync();

        var notification = new RawNotification(
            "live-test-01", "KakaoTalk", "김팀장", "긴급 알림", "[긴급] 서버 장애 발생 확인 바람", DateTime.UtcNow);
        var context = new CurrentContext("Code.exe", "Controller.cs", DateTime.UtcNow);

        var result = await engine.EvaluateAsync(notification, context);

        Assert.AreNotEqual("판정 실패", result.Category, "정상 모델 로드 시 판정 실패가 아니어야 합니다.");
        Assert.IsTrue(result.UrgencyScore >= 1, "긴급도 점수가 정상 산출되어야 합니다.");
    }

    [TestMethod]
    public async Task RealKlueFilterEngine_WithRecentWindows_EvaluatesSuccessfully()
    {
        string baseDir = AppContext.BaseDirectory;
        string modelDir = Path.Combine(baseDir, "models", "filtering", "klue-base-safety-v1");
        string modelPath = Path.Combine(modelDir, "model-fp32.onnx");
        string tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            modelDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "models", "filtering", "klue-base-safety-v1"));
            modelPath = Path.Combine(modelDir, "model-fp32.onnx");
            tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        }

        if (!File.Exists(modelPath) || !File.Exists(tokenizerPath))
        {
            Assert.Inconclusive("Real model or tokenizer file not found.");
            return;
        }

        using var engine = new KlueFilterEngine(modelPath, tokenizerPath);
        await engine.InitializeAsync();

        var notification = new RawNotification(
            "live-test-rw", "Slack", "동료", "코드 리뷰", "PR #39 리뷰 부탁드립니다", DateTime.UtcNow);
        var recentWindows = new List<RecentWindowInfo>
        {
            new("Code.exe", "Controller.cs - Visual Studio Code"),
            new("chrome.exe", "GitHub Pull Requests")
        };
        var context = new CurrentContext(
            "Code.exe",
            "Controller.cs - Visual Studio Code",
            DateTime.UtcNow,
            DurationSeconds: 120,
            RecentProcesses: new[] { "Code.exe", "chrome.exe" },
            RecentWindows: recentWindows);

        var result = await engine.EvaluateAsync(notification, context);

        Assert.AreNotEqual("판정 실패", result.Category, "RecentWindows가 포함된 맥락에서도 정상 평가되어야 합니다.");
        Assert.IsTrue(result.RelevanceScore >= 1, "관련도 점수가 정상 산출되어야 합니다.");
    }
}

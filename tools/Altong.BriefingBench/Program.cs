using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Altong.Client.Data.Models;
using Altong.Client.Services.AI.Briefing;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

// 요약 모델(GGUF)을 앱과 같은 조건(LLamaSharp, 앱 프롬프트, 앱 묶기·나누기, 앱 응답 검사)으로 평가한다.
// 사용법: dotnet run --project tools/Altong.BriefingBench -- <model.gguf> [<model2.gguf> ...] [--template app|gemma4] [--cases <cases.json>] [--out <dir>]
//   --template app    (기본) 앱의 LlamaBriefingEngine을 그대로 사용한다 (대화 형식은 모델 정보로 자동 선택, CPU).
//   --template gemma4 바탕 모델 비교용. 앱과 같은 지시문·입력을 Gemma 4 대화 형식으로만 바꿔 넣는다.
// 모델 경로는 저장소 루트 기준 상대 경로 또는 한글이 없는 절대 경로를 쓴다. 한글 경로는 llama.cpp가 열지 못할 수 있다.
// 메모리는 프로세스 최대 사용량이라, 모델별로 정확히 재려면 모델 하나씩 따로 실행한다.

Console.OutputEncoding = Encoding.UTF8;
var repo = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException("저장소 루트(Altong.Client.sln)를 찾지 못했습니다.");
var models = new List<string>();
string casesPath = Path.Combine(AppContext.BaseDirectory, "cases.json");
string outDir = Path.Combine(repo, "tools", "Altong.BriefingBench", "results");
string template = "app";
bool gpu = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--cases") casesPath = Path.GetFullPath(args[++i]);
    else if (args[i] == "--out") outDir = Path.GetFullPath(args[++i]);
    else if (args[i] == "--template") template = args[++i];
    else if (args[i] == "--gpu") gpu = true;
    else models.Add(args[i]);
}
if (gpu && template == "app")
{
    Console.WriteLine("--gpu는 --template gemma4에서만 쓸 수 있습니다. 앱 엔진은 GPU 층 수가 0으로 고정돼 있습니다.");
    return 2;
}
// 네이티브 라이브러리는 첫 LLama 호출 전에 고른다. --gpu면 Vulkan, 아니면 CPU 전용.
NativeLibraryConfig.All.WithCuda(false).WithVulkan(gpu).WithAutoFallback(true);
if (models.Count == 0 || template is not ("app" or "gemma4"))
{
    Console.WriteLine("사용법: dotnet run --project tools/Altong.BriefingBench -- models/briefing/<모델>.gguf [다른 모델 ...] [--template app|gemma4] [--cases <파일>] [--out <폴더>]");
    return 2;
}

var cases = JsonSerializer.Deserialize<List<BenchCase>>(File.ReadAllText(casesPath, Encoding.UTF8),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
Directory.CreateDirectory(outDir);
// 앱(LlamaBriefingEngine)과 같은 우회: 작업 폴더를 저장소 루트로 두면 한글 경로 대신 ASCII 상대 경로로 모델을 연다.
Directory.SetCurrentDirectory(repo);

var referenceNow = new DateTime(2026, 11, 17, 18, 0, 0); // 화요일. 케이스의 "내일·이번 주" 등은 이 시각 기준.
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var summaries = new List<ModelSummary>();
var report = new StringBuilder();

foreach (var modelArg in models)
{
    var modelPath = Path.GetFullPath(modelArg, repo);
    var modelName = Path.GetFileNameWithoutExtension(modelPath) + (template == "app" ? "" : $" [{template}{(gpu ? ", gpu" : "")}]");
    if (!File.Exists(modelPath)) { Console.WriteLine($"모델 파일이 없습니다: {modelPath}"); return 1; }

    var openWatch = Stopwatch.StartNew();
    using ISummarizer summarizer = template == "app"
        ? await AppSummarizer.OpenAsync(modelPath)
        : await Gemma4Summarizer.OpenAsync(modelArg, gpu);
    openWatch.Stop();
    Console.WriteLine($"\n[{modelName}] 모델 준비(로딩+지시문) {openWatch.ElapsedMilliseconds:N0} ms");

    var results = new List<CaseResult>();
    foreach (var benchCase in cases)
    {
        var records = benchCase.Msgs.Select((m, i) => new NotificationRecord(
            Id: $"{benchCase.Id}-{i}", AppName: m.App, Sender: m.Sender, Title: m.Title ?? m.Sender ?? m.App,
            Body: m.Body, ReceivedAt: referenceNow.AddMinutes(-90 + m.Min), IsPassed: false, SessionId: "bench")).ToList();

        var groups = new List<GroupResult>();
        // LlamaReportSummaryProvider와 같은 묶기·나누기·호출 순서.
        foreach (var group in BriefingGrouping.Group(records))
        foreach (var chunk in BriefingGrouping.SplitEvenly(group, BriefingGrouping.MaxNotificationsPerChunk,
                     c => summarizer.Fits(LlamaReportSummaryProvider.NormalizeDisplayAppName(c[0].AppName),
                         BriefingGrouping.ModelSender(c[0]), c)))
        {
            var appName = LlamaReportSummaryProvider.NormalizeDisplayAppName(chunk[0].AppName);
            var watch = Stopwatch.StartNew();
            var (output, raw) = await summarizer.SummarizeAsync(appName, BriefingGrouping.ModelSender(chunk[0]), chunk);
            watch.Stop();
            groups.Add(new GroupResult(chunk.Count, watch.ElapsedMilliseconds, output?.PrimaryCategory, output?.SummaryLines,
                output is null ? raw : null));
        }

        var failures = Judge(benchCase, groups);
        results.Add(new CaseResult(benchCase.Id, benchCase.Expect, groups, failures));
        var text = string.Join(" / ", groups.Select(g => g.Lines is null ? "(형식 실패→대체 요약)" : string.Join(" ", g.Lines)));
        Console.WriteLine($"  {(failures.Count == 0 ? "PASS" : "FAIL")} {benchCase.Id} {groups.Sum(g => g.Ms):N0} ms | {text}");
        foreach (var failure in failures) Console.WriteLine($"       - {failure}");
    }

    var groupCount = results.Sum(r => r.Groups.Count);
    using var self = Process.GetCurrentProcess();
    var summary = new ModelSummary(modelName, openWatch.ElapsedMilliseconds, results.Count(r => r.Failures.Count == 0), results.Count,
        results.Sum(r => r.Groups.Count(g => g.Lines is null)), groupCount,
        groupCount == 0 ? 0 : results.Sum(r => r.Groups.Sum(g => g.Ms)) / groupCount,
        self.PeakWorkingSet64 / (1024 * 1024));
    summaries.Add(summary);
    await File.WriteAllTextAsync(Path.Combine(outDir, $"{stamp}-{Sanitize(modelName)}.json"),
        JsonSerializer.Serialize(new { summary, results }, jsonOptions), new UTF8Encoding(false));
    AppendMarkdown(report, summary, results);
}

Console.WriteLine("\n=== 요약 ===");
foreach (var s in summaries)
    Console.WriteLine($"{s.Model}: 통과 {s.Passed}/{s.Cases}, 형식 실패 {s.FormatFailures}/{s.Groups}그룹, 모델 준비 {s.OpenMs:N0} ms, 그룹당 평균 {s.AvgGroupMs:N0} ms, 최대 메모리 {s.PeakMemoryMb:N0} MB");
var reportPath = Path.Combine(outDir, $"{stamp}-report.md");
await File.WriteAllTextAsync(reportPath, report.ToString(), new UTF8Encoding(false));
Console.WriteLine($"보고서: {reportPath}");
return 0;

// 자동 판정은 최소 기준이다. 통과해도 결과 문장은 사람이 읽고 확인한다.
static List<string> Judge(BenchCase c, IReadOnlyList<GroupResult> groups)
{
    var failures = new List<string>();
    if (groups.Any(g => g.Lines is null)) failures.Add("응답 형식 실패(앱에서는 대체 요약 표시)");
    var text = string.Join(" ", groups.Where(g => g.Lines is not null).SelectMany(g => g.Lines!));
    foreach (var word in c.MustInclude ?? [])
        if (!text.Contains(word, StringComparison.Ordinal)) failures.Add($"'{word}' 누락");
    foreach (var word in c.MustNotInclude ?? [])
        if (text.Contains(word, StringComparison.Ordinal)) failures.Add($"'{word}' 포함(금지)");
    if (c.Categories is { Count: > 0 } allowed)
        foreach (var g in groups.Where(g => g.Category is not null && !allowed.Contains(g.Category!)))
            failures.Add($"분류 '{g.Category}' (허용: {string.Join(", ", allowed)})");
    return failures;
}

static void AppendMarkdown(StringBuilder md, ModelSummary s, IReadOnlyList<CaseResult> results)
{
    md.AppendLine($"## {s.Model}").AppendLine();
    md.AppendLine($"- 통과 {s.Passed}/{s.Cases}, 형식 실패 {s.FormatFailures}/{s.Groups}그룹");
    md.AppendLine($"- 모델 준비 {s.OpenMs:N0} ms, 그룹당 평균 {s.AvgGroupMs:N0} ms, 최대 메모리 {s.PeakMemoryMb:N0} MB").AppendLine();
    md.AppendLine("| 케이스 | 판정 | 기대 | 모델 출력 | 분류 | 시간(ms) |").AppendLine("|---|---|---|---|---|---|");
    foreach (var r in results)
    {
        var output = string.Join(" / ", r.Groups.Select(g => g.Lines is null ? $"(형식 실패: {g.RawOnFailure})" : string.Join(" ", g.Lines)));
        var verdict = r.Failures.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", r.Failures);
        md.AppendLine($"| {r.Id} | {Cell(verdict)} | {Cell(r.Expect)} | {Cell(output)} | {string.Join(", ", r.Groups.Select(g => g.Category ?? "-"))} | {r.Groups.Sum(g => g.Ms):N0} |");
    }
    md.AppendLine();
}

static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '[' or ']' ? '_' : c));

static string? FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Altong.Client.sln"))) return dir.FullName;
    return null;
}

interface ISummarizer : IDisposable
{
    bool Fits(string appName, string sender, IReadOnlyList<NotificationRecord> notifications);
    Task<(BriefingRawOutput? Output, string? Raw)> SummarizeAsync(string appName, string sender, IReadOnlyList<NotificationRecord> notifications);
}

/// <summary>앱의 LlamaBriefingEngine·BriefingSession을 그대로 쓴다.</summary>
sealed class AppSummarizer(LlamaBriefingEngine engine, BriefingSession session) : ISummarizer
{
    public static async Task<ISummarizer> OpenAsync(string modelPath)
    {
        var engine = new LlamaBriefingEngine(modelPath);
        var session = await engine.OpenSessionAsync()
            ?? throw new InvalidOperationException($"모델을 불러오지 못했습니다: {modelPath}");
        return new AppSummarizer(engine, session);
    }

    public bool Fits(string appName, string sender, IReadOnlyList<NotificationRecord> notifications) =>
        session.Fits(appName, sender, notifications);

    public async Task<(BriefingRawOutput?, string?)> SummarizeAsync(string appName, string sender, IReadOnlyList<NotificationRecord> notifications) =>
        (await session.SummarizeAsync(appName, sender, notifications, maxSummaryLines: 3), null);

    public void Dispose() { session.Dispose(); engine.Dispose(); }
}

/// <summary>
/// 바탕 모델 비교용. 앱 BriefingSession과 같은 방식(지시문 한 번 처리 후 Fork, 그리디, 256토큰, 앱 응답 검사)이며
/// 대화 형식만 Gemma 4(&lt;|turn&gt;role ... &lt;turn|&gt;, 사고 모드 끔)로 바꾼다.
/// </summary>
sealed class Gemma4Summarizer : ISummarizer
{
    private readonly LLamaWeights _weights;
    private readonly BatchedExecutor _executor;
    private readonly Conversation _root;
    private readonly int _prefixCount;
    private readonly HashSet<LLamaToken> _stopTokens = [];

    private Gemma4Summarizer(LLamaWeights weights, BatchedExecutor executor, Conversation root, int prefixCount)
    {
        _weights = weights; _executor = executor; _root = root; _prefixCount = prefixCount;
        foreach (var marker in new[] { "<turn|>", "<|turn>" })
            _stopTokens.UnionWith(executor.Context.Tokenize(marker, false, true));
    }

    public static async Task<ISummarizer> OpenAsync(string modelPath, bool gpu)
    {
        var parameters = new ModelParams(modelPath) { ContextSize = LlamaBriefingEngine.ContextSize, GpuLayerCount = gpu ? 999 : 0 };
        var watch = Stopwatch.StartNew();
        var weights = LLamaWeights.LoadFromFile(parameters);
        var loadMs = watch.ElapsedMilliseconds;
        var executor = new BatchedExecutor(weights, parameters);
        var prefix = executor.Context.Tokenize($"<|turn>system\n{BriefingPromptBuilder.SystemPrompt.Trim()}<turn|>\n", true, true);
        var root = executor.Create();
        watch.Restart();
        root.Prompt(prefix);
        await Drain(executor);
        Console.WriteLine($"  (모델 읽기 {loadMs:N0} ms, 지시문 {prefix.Length}토큰 처리 {watch.ElapsedMilliseconds:N0} ms)");
        return new Gemma4Summarizer(weights, executor, root, prefix.Length);
    }

    public bool Fits(string appName, string sender, IReadOnlyList<NotificationRecord> notifications) =>
        _prefixCount + Suffix(appName, sender, notifications).Length + LlamaBriefingEngine.MaxNewTokens <= LlamaBriefingEngine.ContextSize;

    public async Task<(BriefingRawOutput?, string?)> SummarizeAsync(string appName, string sender, IReadOnlyList<NotificationRecord> notifications)
    {
        var suffix = Suffix(appName, sender, notifications);
        if (_prefixCount + suffix.Length + LlamaBriefingEngine.MaxNewTokens > LlamaBriefingEngine.ContextSize) return (null, "(문맥 초과)");
        using var conversation = _root.Fork();
        conversation.Prompt(suffix);
        await Drain(_executor);
        using var sampler = new DefaultSamplingPipeline { Temperature = 0.0f, RepeatPenalty = 1.0f };
        var decoder = new StreamingTokenDecoder(_executor.Context);
        for (int i = 0; i < LlamaBriefingEngine.MaxNewTokens; i++)
        {
            var token = sampler.Sample(_executor.Context.NativeHandle, conversation.GetSampleIndex());
            if (_stopTokens.Contains(token) || token.IsEndOfGeneration(_executor.Model.Vocab)) break;
            sampler.Accept(token);
            decoder.Add(token);
            conversation.Prompt(token);
            await Drain(_executor);
        }
        var raw = decoder.Read();
        return (LlamaBriefingEngine.ParseResponse(raw, 3), raw);
    }

    // 앱 BriefingSession.TokenizeSuffix와 같은 입력 (수신 시각 UTC, 제목, 본문).
    private LLamaToken[] Suffix(string appName, string sender, IReadOnlyList<NotificationRecord> notifications)
    {
        var items = notifications.OrderBy(n => n.ReceivedAt)
            .Select(n => new NotificationPromptItem(n.ReceivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), n.Title ?? "", n.Body ?? ""))
            .ToList();
        var user = BriefingPromptBuilder.BuildUserPrompt(appName, sender, items, 3);
        return _executor.Context.Tokenize($"<|turn>user\n{user.Trim()}<turn|>\n<|turn>model\n", false, true);
    }

    private static async Task Drain(BatchedExecutor executor)
    {
        while (executor.BatchedTokenCount > 0) await executor.Infer();
    }

    public void Dispose() { _root.Dispose(); _executor.Dispose(); _weights.Dispose(); }
}

record BenchMessage(string App, string? Sender, string? Title, string Body, int Min);
record BenchCase(string Id, string Expect, List<BenchMessage> Msgs,
    List<string>? MustInclude, List<string>? MustNotInclude, List<string>? Categories);
record GroupResult(int Count, long Ms, string? Category, IReadOnlyList<string>? Lines, string? RawOnFailure);
record CaseResult(string Id, string Expect, IReadOnlyList<GroupResult> Groups, IReadOnlyList<string> Failures);
record ModelSummary(string Model, long OpenMs, int Passed, int Cases, int FormatFailures, int Groups, long AvgGroupMs, long PeakMemoryMb);

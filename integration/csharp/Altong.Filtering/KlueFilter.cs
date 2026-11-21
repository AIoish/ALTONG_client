// Purpose: Reuse one native ONNX session with bounded work and fail-open results.
// Created: 2026-10-10
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Altong.Filtering;

/// <summary>
/// 필터 판정. 긴급도는 1~5, 관련도는 1~<see cref="RelevanceLevels"/>(모델이 학습한 단계 수, 지금 모델은 3)이다.
/// 점수 null은 평가하지 못했다는 뜻이다.
/// </summary>
public sealed record FilterDecision(int? UrgencyScore, int? RelevanceScore, bool ModelPassed, bool IsPassed, string Status, string? Error = null,
    int RelevanceLevels = RelevanceScale.LegacyLevels);
public sealed class KlueFilter : IDisposable
{
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _gate = new(1,1);
    private readonly TimeSpan _timeout;
    private readonly int[] _relevancePositions = RelevanceScale.LegacyPositions;
    private int _pending;
    private bool _disposed;
    public KlueTokenizer Tokenizer { get; }
    /// <summary>관련도 단계 수. manifest의 relevance_score_to_app에서 정하며, 없으면 예전 모델처럼 5단계다.</summary>
    public int RelevanceLevels => _relevancePositions.Length;
    /// <summary>관련도가 이 값 이상이면 지금 작업과 관련 있다고 본다.</summary>
    public int RelevancePassScore => RelevanceScale.PassScore(RelevanceLevels);
    public KlueFilter(string modelPath, string tokenizerPath, TimeSpan? timeout = null)
    {
        string manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modelPath))!, "manifest.json");
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var pair in new[]{(modelPath,"model_sha256"),(tokenizerPath,"tokenizer_sha256")})
            {
                using var stream = File.OpenRead(pair.Item1);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.RootElement.GetProperty(pair.Item2).GetString(),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Model/tokenizer manifest hash mismatch.");
            }
            _relevancePositions = RelevanceScale.Positions(manifest.RootElement);
        }
        Tokenizer = new(tokenizerPath); _timeout = timeout ?? TimeSpan.FromSeconds(2);
        using var options = new SessionOptions { IntraOpNumThreads = 4, InterOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        _session = new InferenceSession(modelPath, options);
        if (!_session.InputMetadata.Keys.Order().SequenceEqual(new[] {"attention_mask","input_ids"}) || !_session.OutputMetadata.ContainsKey("logits") ||
            _session.InputMetadata.Values.Any(m => m.ElementType != typeof(long) || m.Dimensions.Length != 2))
        { _session.Dispose(); throw new ArgumentException("Unexpected ONNX input/output names."); }
    }
    public async Task<FilterDecision> EvaluateAsync(NotificationInput n, ContextInput c, bool shadow = false, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _pending) > 8) { Interlocked.Decrement(ref _pending); return Failure("queue_full"); }
        bool entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeout);
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                int u = RelevanceScale.Decode(Run(ModelInputBuilder.Build(n,c,false),0,deadline.Token), RelevanceScale.LegacyPositions);
                int r = RelevanceScale.Decode(Run(ModelInputBuilder.Build(n,c,true),1,deadline.Token), _relevancePositions);
                bool pass = u >= 4 || r >= RelevancePassScore;
                return new FilterDecision(u,r,pass,shadow || pass,shadow ? "shadow" : "ok",null,RelevanceLevels);
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or OnnxRuntimeException or ArgumentException or ObjectDisposedException)
        { return Failure(deadline.IsCancellationRequested ? "timeout_or_cancelled" : "inference_error",ex.Message); }
        finally { if (entered) _gate.Release(); Interlocked.Decrement(ref _pending); }
    }
    /// <summary>한 head의 5칸 logits를 돌려준다.</summary>
    private float[] Run(string text, int head, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var ids = Tokenizer.Encode(text);
        var shape = new[]{1,ids.Length}; var mask = Enumerable.Repeat(1L,ids.Length).ToArray();
        using var runOptions = new RunOptions(); using var registration = ct.Register(() => runOptions.Terminate = true);
        var inputs = new[]{NamedOnnxValue.CreateFromTensor("input_ids",new DenseTensor<long>(ids,shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask",new DenseTensor<long>(mask,shape))};
        using var outputs = _session.Run(inputs,new[]{"logits"},runOptions);
        ct.ThrowIfCancellationRequested(); var logits = outputs.Single().AsTensor<float>();
        if (!logits.Dimensions.SequenceEqual(new[]{1,2,5})) throw new ArgumentException("Unexpected logits shape.");
        var row = new float[5];
        for (int k=0;k<5;k++)
        {
            if (!float.IsFinite(logits[0,head,k])) throw new ArgumentException("Non-finite model logits.");
            row[k] = logits[0,head,k];
        }
        return row;
    }
    public static FilterDecision Failure(string status, string? error=null) => new(null,null,false,true,status,error);
    // Dispose after intake stops. Active requests are drained before freeing native memory.
    public void Dispose() { _gate.Wait(); try { if (_disposed) return; _disposed=true;_session.Dispose(); } finally { _gate.Release(); } }
}

/// <summary>
/// 관련도 단계와 모델 출력 칸의 대응.
/// 모델은 출력이 5칸이지만 관련도를 3단계로만 학습할 수 있다. 그때 manifest의 relevance_score_to_app이
/// 단계별로 쓰는 칸을 알려 준다(예: {"1":1,"2":2,"3":5}는 1단계→1번째, 2단계→2번째, 3단계→5번째 칸).
/// 앱은 그 칸들만 보고 1~3단계로 읽는다. 학습하지 않은 칸은 고르지 않는다.
/// </summary>
public static class RelevanceScale
{
    /// <summary>대응표가 없는 예전 모델: 5칸이 그대로 1~5점.</summary>
    public const int LegacyLevels = 5;
    public static readonly int[] LegacyPositions = [0, 1, 2, 3, 4];

    /// <summary>관련 있다고 보는 최소 점수. 5단계는 4점(예전 기준), 그보다 적은 단계는 가장 높은 단계.</summary>
    public static int PassScore(int levels) => levels >= LegacyLevels ? 4 : levels;

    /// <summary>manifest에서 단계별 출력 칸 위치(0부터)를 읽는다. 대응표가 없으면 5단계 그대로.</summary>
    public static int[] Positions(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("relevance_score_to_app", out var map)) return LegacyPositions;
        if (map.ValueKind != JsonValueKind.Object) throw new InvalidDataException("relevance_score_to_app must be an object.");
        var pairs = map.EnumerateObject()
            .Select(p => (Level: int.Parse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture), Score: p.Value.GetInt32()))
            .OrderBy(p => p.Level).ToArray();
        // 단계는 1부터 빠짐없이 이어지고, 칸은 1~5 안에서 단계가 오를수록 뒤로 가야 한다.
        bool valid = pairs.Length >= 2 && pairs.Select((p, i) => p.Level == i + 1 && p.Score is >= 1 and <= 5
            && (i == 0 || p.Score > pairs[i - 1].Score)).All(ok => ok);
        if (manifest.TryGetProperty("relevance_classes_trained", out var trained) && trained.GetInt32() != pairs.Length) valid = false;
        if (!valid) throw new InvalidDataException("relevance_score_to_app does not describe ordered relevance levels.");
        return pairs.Select(p => p.Score - 1).ToArray();
    }

    /// <summary>지정한 칸 가운데 logit이 가장 큰 칸의 단계(1부터)를 고른다. 같으면 낮은 단계.</summary>
    public static int Decode(IReadOnlyList<float> logits, IReadOnlyList<int> positions)
    {
        int best = 0;
        for (int level = 1; level < positions.Count; level++)
            if (logits[positions[level]] > logits[positions[best]]) best = level;
        return best + 1;
    }

    /// <summary>모델 칸 번호(1~5)로 기록된 점수를 단계로 바꾼다. 대응표에 없는 칸이면 null.</summary>
    public static int? LevelOfAppScore(int appScore, IReadOnlyList<int> positions)
    {
        for (int level = 0; level < positions.Count; level++)
            if (positions[level] == appScore - 1) return level + 1;
        return null;
    }
}

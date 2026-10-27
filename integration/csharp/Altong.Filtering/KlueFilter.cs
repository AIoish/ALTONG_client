// Purpose: Reuse one native ONNX session with bounded work and fail-open results.
// Created: 2026-10-10
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Security.Cryptography;
using System.Text.Json;

namespace Altong.Filtering;

public sealed record FilterDecision(int? UrgencyScore, int? RelevanceScore, bool ModelPassed, bool IsPassed, string Status, string? Error = null);
public sealed class KlueFilter : IDisposable
{
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _gate = new(1,1);
    private readonly TimeSpan _timeout;
    private int _pending;
    private bool _disposed;
    public KlueTokenizer Tokenizer { get; }
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
                int u = Run(ModelInputBuilder.Build(n,c,false),0,deadline.Token);
                int r = Run(ModelInputBuilder.Build(n,c,true),1,deadline.Token);
                bool pass = u >= 4 || r >= 4;
                return new FilterDecision(u,r,pass,shadow || pass,shadow ? "shadow" : "ok");
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or OnnxRuntimeException or ArgumentException or ObjectDisposedException)
        { return Failure(deadline.IsCancellationRequested ? "timeout_or_cancelled" : "inference_error",ex.Message); }
        finally { if (entered) _gate.Release(); Interlocked.Decrement(ref _pending); }
    }
    private int Run(string text, int head, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var ids = Tokenizer.Encode(text);
        var shape = new[]{1,ids.Length}; var mask = Enumerable.Repeat(1L,ids.Length).ToArray();
        using var runOptions = new RunOptions(); using var registration = ct.Register(() => runOptions.Terminate = true);
        var inputs = new[]{NamedOnnxValue.CreateFromTensor("input_ids",new DenseTensor<long>(ids,shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask",new DenseTensor<long>(mask,shape))};
        using var outputs = _session.Run(inputs,new[]{"logits"},runOptions);
        ct.ThrowIfCancellationRequested(); var logits = outputs.Single().AsTensor<float>();
        if (!logits.Dimensions.SequenceEqual(new[]{1,2,5})) throw new ArgumentException("Unexpected logits shape.");
        int best = 0;
        for (int k=0;k<5;k++)
        {
            if (!float.IsFinite(logits[0,head,k])) throw new ArgumentException("Non-finite model logits.");
            if (logits[0,head,k]>logits[0,head,best]) best=k;
        }
        return best+1;
    }
    public static FilterDecision Failure(string status, string? error=null) => new(null,null,false,true,status,error);
    // Dispose after intake stops. Active requests are drained before freeing native memory.
    public void Dispose() { _gate.Wait(); try { if (_disposed) return; _disposed=true;_session.Dispose(); } finally { _gate.Release(); } }
}

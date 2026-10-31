// Purpose: Adapt the unmodified ALTONG_client develop IFilterEngine contract.
// Created: 2026-10-10
using System.Globalization;
using Altong.Client.Models;
using Altong.Filtering;

namespace Altong.Client.Services.Notifications;

public sealed class KlueFilterEngine : IFilterEngine, IDisposable
{
    private readonly Task<KlueFilter?> _initialization;
    private string? _loadError;
    private readonly bool _shadow;
    private readonly Action<FilterDecision>? _observe;
    public KlueFilterEngine(string modelPath,string tokenizerPath,bool shadow=false,Action<FilterDecision>? observe=null)
    {
        _shadow=shadow;_observe=observe;
        _initialization=Task.Run<KlueFilter?>(()=>
        {
            try { return new KlueFilter(modelPath,tokenizerPath); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException or FormatException or ArgumentException or System.Text.Json.JsonException or
                NotSupportedException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or DllNotFoundException or BadImageFormatException or
                UnauthorizedAccessException or KeyNotFoundException or TypeInitializationException)
            { _loadError=ex.Message; return null; }
        });
    }
    public Task InitializeAsync()=>_initialization;
    public async Task<FilterResult> EvaluateAsync(RawNotification n,CurrentContext c)
    {
        if (n.Timestamp.Kind==DateTimeKind.Unspecified)
            return ToResult(n.Id,KlueFilter.Failure("invalid_timestamp","Timestamp must carry UTC/local kind."));
        var input=new NotificationInput(n.AppName ?? "",n.Sender,n.Title ?? "",n.Body ?? "",n.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture));
        var recentWindows = c.RecentWindows?.Select(w => new RecentWindow(w.AppName, w.WindowTitle)).ToArray() ?? [];
        var context = new ContextInput(c.ActiveProcess, c.WindowTitle, c.RecentProcesses, recentWindows);
        FilterDecision decision;
        try
        {
            var filter=await _initialization.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            decision=filter is null ? KlueFilter.Failure("model_unavailable",_loadError) : await filter.EvaluateAsync(input,context,_shadow).ConfigureAwait(false);
        }
        catch (TimeoutException) { decision=KlueFilter.Failure("model_loading"); }
        catch (Exception ex) { decision=KlueFilter.Failure("model_unavailable", _loadError ?? ex.Message); }
        try { _observe?.Invoke(decision); } catch { /* Diagnostic observer cannot suppress notifications. */ }
        return ToResult(n.Id,decision);
    }
    private static FilterResult ToResult(string id,FilterDecision d) => new(id,d.IsPassed,
        d.UrgencyScore ?? 0,d.RelevanceScore ?? 0,d.UrgencyScore is null ? "판정 실패" : "미분류",
        d.UrgencyScore is null ? $"AI 평가 실패로 통과 ({d.Status}); 점수 0은 미평가 표시" :
            $"긴급도 {d.UrgencyScore}, 관련도 {d.RelevanceScore}; 모델 {(d.ModelPassed ? "통과" : "차단")}{(d.Status=="shadow" ? "; 시험 모드로 실제 통과" : "")}");
    public void Dispose()
    {
        try { _initialization.GetAwaiter().GetResult()?.Dispose(); }
        catch { /* Diagnostic/load failures must not throw on disposal. */ }
    }
}

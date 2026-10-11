// Purpose: Fail on tokenizer/input/score differences or broken fail-open behavior.
// Created: 2026-10-10
using System.Diagnostics;
using System.Text.Json;
using Altong.Filtering;

if (args.Length!=3) throw new ArgumentException("Usage: Verification <model.onnx> <tokenizer.json> <fixtures.json>");
using var doc=JsonDocument.Parse(File.ReadAllText(args[2]));
using var filter=new KlueFilter(args[0],args[1],TimeSpan.FromSeconds(10));
int texts=0,cases=0;var times=new List<double>();
foreach(var f in doc.RootElement.GetProperty("tokenizer_cases").EnumerateArray())
{
    var actual=filter.Tokenizer.Encode(f.GetProperty("text").GetString()!);
    if (!actual.SequenceEqual(f.GetProperty("ids").EnumerateArray().Select(v=>v.GetInt64())))
        throw new Exception("Tokenizer parity failed: "+f.GetProperty("text").GetString());
    texts++;
}
foreach(var f in doc.RootElement.GetProperty("cases").EnumerateArray())
{
    var n=f.GetProperty("notification");var c=f.GetProperty("context");
    var input=new NotificationInput(n.GetProperty("app_name").GetString()!,n.GetProperty("sender").GetString(),
        n.GetProperty("title").GetString()!,n.GetProperty("body").GetString()!,n.GetProperty("timestamp").GetString()!);
    var context=new ContextInput(c.GetProperty("active_process").GetString()!,c.GetProperty("window_title").GetString()!,
        (c.TryGetProperty("recent_processes",out var processes) ? processes.EnumerateArray().ToArray() : []).Select(x=>x.GetString()!).ToArray(),
        (c.TryGetProperty("recent_windows",out var windows) ? windows.EnumerateArray().ToArray() : []).Select(x=>new RecentWindow(x.GetProperty("app_name").GetString()!,x.GetProperty("window_title").GetString()!)).ToArray());
    foreach(bool relevance in new[]{false,true})
    {
        string field=relevance ? "relevance" : "urgency";
        string text=ModelInputBuilder.Build(input,context,relevance);
        if(text!=f.GetProperty(field+"_text").GetString())throw new Exception("JSON parity failed: "+cases);
        if(!filter.Tokenizer.Encode(text).SequenceEqual(f.GetProperty(field+"_ids").EnumerateArray().Select(x=>x.GetInt64())))throw new Exception("Input token parity failed: "+cases);
    }
    var watch=Stopwatch.StartNew();var result=await filter.EvaluateAsync(input,context,shadow:false);watch.Stop();times.Add(watch.Elapsed.TotalMilliseconds);
    var expected=f.GetProperty("prediction");
    if(result.Status!="ok" || result.UrgencyScore!=expected.GetProperty("urgency_score").GetInt32() || result.RelevanceScore!=expected.GetProperty("relevance_score").GetInt32())
        throw new Exception("Score parity failed: "+cases+" "+JsonSerializer.Serialize(result));
    var shadow=await filter.EvaluateAsync(input,context,shadow:true);
    if(!shadow.IsPassed || shadow.ModelPassed!=result.ModelPassed)throw new Exception("Shadow policy failed");
    cases++;
}
var plain=new NotificationInput("카카오톡","지인","새 메시지","집에 불났어","2026-10-10T05:00:00Z");
var empty=new ContextInput("","");
var defaultBlock=await filter.EvaluateAsync(plain with {Body="고양이 사진 보냈어"},empty);
if(defaultBlock.IsPassed || defaultBlock.ModelPassed || defaultBlock.Status!="ok")throw new Exception("Default mode must apply model blocking");
var oversized=await filter.EvaluateAsync(plain with {Body=string.Join(" ",Enumerable.Repeat("집에 불났어",600))},empty,false);
if(!oversized.IsPassed || oversized.UrgencyScore is not null || oversized.Status!="inference_error")throw new Exception("Oversized input must fail open with unknown scores");
using(var cancel=new CancellationTokenSource())
{
    cancel.Cancel();var result=await filter.EvaluateAsync(plain,empty,false,cancel.Token);
    if(!result.IsPassed || result.UrgencyScore is not null)throw new Exception("Cancellation fail-open failed");
}
var burst=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>filter.EvaluateAsync(plain,empty,false)));
if(!burst.Any(r=>r.Status=="queue_full") || burst.Any(r=>!r.IsPassed))throw new Exception("Bounded queue fail-open failed");
#if CLIENT_CONTRACTS
using(var adapter=new Altong.Client.Services.Notifications.KlueFilterEngine(args[0],args[1],observe:_=>throw new Exception("observer test")))
{
    await adapter.InitializeAsync();
    var notification=new Altong.Client.Models.RawNotification("live","카카오톡","지인","새 메시지","집에 불났어",new DateTime(2026,10,10,5,0,0,DateTimeKind.Utc));
    var result=await adapter.EvaluateAsync(notification,new("","",DateTime.UtcNow));
    if(!result.IsPassed || result.UrgencyScore!=5 || result.RelevanceScore!=1)throw new Exception("Actual client adapter score failed");
    var blocked=await adapter.EvaluateAsync(notification with {Body="고양이 사진 보냈어"},new("","",DateTime.UtcNow));
    if(blocked.IsPassed || blocked.UrgencyScore>=4 || blocked.RelevanceScore>=4)throw new Exception("Adapter default must apply model blocking");
    var invalid=await adapter.EvaluateAsync(notification with {Timestamp=DateTime.SpecifyKind(notification.Timestamp,DateTimeKind.Unspecified)},new("","",DateTime.UtcNow));
    if(!invalid.IsPassed || invalid.UrgencyScore!=0)throw new Exception("Timestamp failure must pass with unknown score");
}
using(var adapter=new Altong.Client.Services.Notifications.KlueFilterEngine(args[0]+".missing",args[1]))
{
    var result=await adapter.EvaluateAsync(new("x","카카오톡","지인","새 메시지","집에 불났어",DateTime.UtcNow),new("","",DateTime.UtcNow));
    if(!result.IsPassed || result.UrgencyScore!=0 || result.Category!="판정 실패")throw new Exception("Adapter missing-model fail-open failed");
}
#endif
times.Sort();
Console.WriteLine(JsonSerializer.Serialize(new {tokenizer_cases=texts,model_cases=cases,input_token_score_parity=true,default_applies_filter=true,shadow=true,oversized_fail_open=true,cancellation_fail_open=true,bounded_queue=true,
    client_contracts_compiled=
#if CLIENT_CONTRACTS
    true,
#else
    false,
#endif
    mean_ms=times.Average(),p95_ms=times[(int)Math.Floor((times.Count-1)*.95)],timing_scope="JSON, tokenization, two CPU calls; excludes queue and load; synthetic cases, not app end-to-end"}));

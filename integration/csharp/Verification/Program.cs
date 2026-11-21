// Purpose: Fail on tokenizer/input/score differences or broken fail-open behavior.
// Created: 2026-10-10
using System.Diagnostics;
using System.Text.Json;
using Altong.Filtering;

// fixtures.json: ALTONG_ai 필터 릴리즈의 verification-fixtures.json (filtering-v3 형식).
// fixtures[].state(notification, context)와 urgency/relevance별 input_text, input_ids, app_score(모델 출력 칸 1~5)를 담는다.
if (args.Length!=3) throw new ArgumentException("Usage: Verification <model.onnx> <tokenizer.json> <verification-fixtures.json>");
using var doc=JsonDocument.Parse(File.ReadAllText(args[2]));
using var filter=new KlueFilter(args[0],args[1],TimeSpan.FromSeconds(10));
string manifestPath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!,"manifest.json");
int[] positions;
using (var manifest=JsonDocument.Parse(File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : "{}")) positions=RelevanceScale.Positions(manifest.RootElement);
int cases=0;var times=new List<double>();
foreach(var f in doc.RootElement.GetProperty("fixtures").EnumerateArray())
{
    var state=f.GetProperty("state");var n=state.GetProperty("notification");var c=state.GetProperty("context");
    var input=new NotificationInput(S(n,"app_name"),S(n,"sender"),S(n,"title"),S(n,"body"),S(n,"timestamp"));
    // 앱 어댑터(KlueFilterEngine.ToModelContext)와 같게: 현재 창과 같은 항목을 빼고 최근 창 3개, 그 앱 이름을 같은 순서로 recent_processes.
    var recent=(c.TryGetProperty("recent_windows",out var windows) ? windows.EnumerateArray().Select(x=>new RecentWindow(S(x,"app_name"),S(x,"window_title"))) : Enumerable.Empty<RecentWindow>())
        .Where(w=>w.AppName!=S(c,"active_process") || w.WindowTitle!=S(c,"window_title")).Take(3).ToArray();
    var context=new ContextInput(S(c,"active_process"),S(c,"window_title"),recent.Select(w=>w.AppName).ToArray(),recent);
    foreach(bool relevance in new[]{false,true})
    {
        var expectedInput=f.GetProperty(relevance ? "relevance" : "urgency");
        string text=ModelInputBuilder.Build(input,context,relevance);
        if(text!=expectedInput.GetProperty("input_text").GetString())throw new Exception("JSON parity failed: "+f.GetProperty("id").GetString());
        if(!filter.Tokenizer.Encode(text).SequenceEqual(expectedInput.GetProperty("input_ids").EnumerateArray().Select(x=>x.GetInt64())))throw new Exception("Input token parity failed: "+f.GetProperty("id").GetString());
    }
    var watch=Stopwatch.StartNew();var result=await filter.EvaluateAsync(input,context,shadow:false);watch.Stop();times.Add(watch.Elapsed.TotalMilliseconds);
    int expectedUrgency=f.GetProperty("urgency").GetProperty("app_score").GetInt32();
    int? expectedRelevance=RelevanceScale.LevelOfAppScore(f.GetProperty("relevance").GetProperty("app_score").GetInt32(),positions);
    if(result.Status!="ok" || result.UrgencyScore!=expectedUrgency || result.RelevanceScore!=expectedRelevance)
        throw new Exception("Score parity failed: "+f.GetProperty("id").GetString()+" "+JsonSerializer.Serialize(result));
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
    if(blocked.IsPassed || blocked.UrgencyScore>=4 || blocked.RelevanceScore>=filter.RelevancePassScore)throw new Exception("Adapter default must apply model blocking");
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
Console.WriteLine(JsonSerializer.Serialize(new {model_cases=cases,relevance_levels=filter.RelevanceLevels,relevance_pass_score=filter.RelevancePassScore,input_token_score_parity=true,default_applies_filter=true,shadow=true,oversized_fail_open=true,cancellation_fail_open=true,bounded_queue=true,
    client_contracts_compiled=
#if CLIENT_CONTRACTS
    true,
#else
    false,
#endif
    mean_ms=times.Average(),p95_ms=times[(int)Math.Floor((times.Count-1)*.95)],timing_scope="JSON, tokenization, two CPU calls; excludes queue and load; synthetic cases, not app end-to-end"}));

static string S(JsonElement e,string name)=>e.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.String ? v.GetString()! : "";

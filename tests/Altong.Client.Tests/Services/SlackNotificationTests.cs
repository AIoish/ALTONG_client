using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SlackNotificationTests
{
    private static readonly SlackIdentity Identity = new("T_TEST", "U_SELF");

    internal static string Envelope(string id = "envelope-1", string ts = "1794910000.123456",
        Action<Dictionary<string, object?>>? change = null, string team = "T_TEST")
    {
        var message = new Dictionary<string, object?>
        {
            ["type"] = "message", ["channel_type"] = "im", ["channel"] = "D_TEST", ["user"] = "U_OTHER",
            ["text"] = "가상 회의 &amp; 일정 확인", ["ts"] = ts
        };
        change?.Invoke(message);
        return JsonSerializer.Serialize(new
        {
            type = "events_api", envelope_id = id,
            payload = new { type = "event_callback", team_id = team, event_id = "Ev_" + id, @event = message }
        });
    }

    [TestMethod]
    public async Task ConnectionSettings_PausePreservesCredentials_AndReconnectReceivesNewDm()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        await File.WriteAllTextAsync(path, "synthetic encrypted credentials placeholder");
        try
        {
            using var socket = new FakeSocket();
            using var resumedSocket = new FakeSocket();
            var sockets = new Queue<FakeSocket>([socket, resumedSocket]);
            using var listener = new SlackSocketModeListener(new FakeApi
            {
                Account = new("T_TEST", "U_SELF", "가상 워크스페이스", "가상 계정")
            }, () => sockets.Dequeue());
            using var controller = new SlackConnectionController(listener, path);
            var received = new ConcurrentQueue<RawNotification>();
            controller.NotificationReceived += (_, notification) => received.Enqueue(notification);
            Assert.IsTrue(await controller.ConnectAsync());
            await UntilAsync(() => controller.Current.Phase == SlackConnectionPhase.Connected);
            Assert.AreEqual("가상 계정", controller.Current.Account);
            Assert.AreEqual("가상 워크스페이스", controller.Current.Workspace);
            await controller.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(listener.IsRunning);
            Assert.AreEqual("synthetic encrypted credentials placeholder", await File.ReadAllTextAsync(path));
            Assert.IsTrue(File.Exists(path + ".paused"));
            Assert.AreEqual(SlackConnectionPhase.Paused, controller.Current.Phase);
            Assert.AreEqual("", controller.Current.Account, "Disconnect must clear account details from the card.");
            Assert.IsFalse(await controller.StartAsync(), "Pipeline restart must respect the saved pause.");
            Assert.IsTrue(await controller.ConnectAsync());
            await UntilAsync(() => controller.IsRunning);
            Assert.IsFalse(File.Exists(path + ".paused"));
            resumedSocket.Push(Envelope());
            await UntilAsync(() => received.Count == 1);
            Assert.AreEqual("가상 회의 & 일정 확인", received.Single().Body);
        }
        finally { File.Delete(path); File.Delete(path + ".paused"); }
    }

    [TestMethod]
    public async Task ConnectionSettings_PauseSurvivesNewController_AndNormalShutdownDoesNotPause()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        try
        {
            using (var original = new SlackConnectionController(path))
                await original.DisconnectAsync();
            using var socket = new FakeSocket();
            var api = new FakeApi();
            using var listener = new SlackSocketModeListener(api, () => socket);
            using var restarted = new SlackConnectionController(listener, path);
            Assert.AreEqual(SlackConnectionPhase.Paused, restarted.Current.Phase);
            Assert.IsFalse(await restarted.StartAsync());
            Assert.AreEqual(0, api.OpenCalls);
            Assert.IsTrue(await restarted.ConnectAsync());
            await UntilAsync(() => restarted.IsRunning);
            restarted.Stop();
            await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(File.Exists(path + ".paused"), "App shutdown must allow normal autostart next time.");
        }
        finally { File.Delete(path + ".paused"); }
    }

    [TestMethod]
    public async Task ConnectionSettings_ConcurrentDisconnectAndReconnectSerializeWithoutLosingCredentials()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        await File.WriteAllTextAsync(path, "synthetic credentials placeholder");
        try
        {
            using var first = new FakeSocket();
            using var second = new FakeSocket();
            var sockets = new Queue<FakeSocket>([first, second]);
            using var listener = new SlackSocketModeListener(new FakeApi(), () => sockets.Dequeue());
            using var controller = new SlackConnectionController(listener, path);
            await controller.ConnectAsync();
            await UntilAsync(() => controller.IsRunning);
            var disconnect = controller.DisconnectAsync();
            var reconnect = controller.ConnectAsync();
            await Task.WhenAll(disconnect, reconnect).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(await reconnect);
            await UntilAsync(() => controller.IsRunning);
            Assert.AreEqual("synthetic credentials placeholder", await File.ReadAllTextAsync(path));
            Assert.IsFalse(File.Exists(path + ".paused"));
        }
        finally { File.Delete(path); File.Delete(path + ".paused"); }
    }

    [TestMethod]
    public async Task ConnectionSettings_CredentialsAddedAfterStartupReachExistingPipeline_WithPendingReceipt()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        var lookup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var socket = new FakeSocket();
            using var controller = new SlackConnectionController(path,
                _ => new SlackSocketModeListener(new FakeApi { Name = _ => lookup.Task }, () => socket));
            using var merger = new SlackNotificationMerger(new FakeNotificationListener(), controller);
            var receipt = new TaskCompletionSource<PendingNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            merger.NotificationPending += (_, pending) => receipt.TrySetResult(pending);
            Assert.IsTrue(await merger.StartAsync(), "Windows receipt must work without Slack credentials.");
            Assert.IsFalse(await controller.ConnectAsync());
            Assert.AreEqual(SlackConnectionPhase.NotConnected, controller.Current.Phase);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new SlackConnectionSettings("xapp-fake", "xoxp-fake"));
            try { await File.WriteAllBytesAsync(path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Assert.IsTrue(await controller.ConnectAsync());
            await UntilAsync(() => controller.IsRunning);
            socket.Push(Envelope());
            var pending = await receipt.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prepared = pending.PrepareAsync();
            Assert.IsFalse(prepared.IsCompleted, "Receipt must reach the pipeline before asynchronous profile lookup.");
            lookup.SetResult("가상 팀원");
            Assert.AreEqual("가상 팀원", (await prepared.WaitAsync(TimeSpan.FromSeconds(5)))!.Sender);
        }
        finally { lookup.TrySetResult("가상 팀원"); File.Delete(path); }
    }

    [TestMethod]
    public async Task ConnectionSettings_InvalidCredentialsCannotResumeOrEraseSavedPause()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        try
        {
            using var controller = new SlackConnectionController(path,
                _ => throw new AssertFailedException("Invalid credentials must never create a receiver."));
            await controller.DisconnectAsync();
            Assert.IsFalse(await controller.ConnectAsync());
            await File.WriteAllTextAsync(path, "not an encrypted credentials file");
            Assert.IsFalse(await controller.ConnectAsync());
            Assert.IsTrue(File.Exists(path + ".paused"));
            Assert.AreEqual(SlackConnectionPhase.Paused, controller.Current.Phase);
            Assert.AreEqual("not an encrypted credentials file", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); File.Delete(path + ".paused"); }
    }

    [TestMethod]
    public async Task ConnectionSettings_FailedPausePersistenceDoesNotReportSuccessOrStopReceiver()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        try
        {
            using var socket = new FakeSocket();
            using var listener = new SlackSocketModeListener(new FakeApi(), () => socket);
            using var controller = new SlackConnectionController(listener, path);
            await controller.ConnectAsync();
            await UntilAsync(() => controller.IsRunning);
            Directory.CreateDirectory(path + ".paused");
            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => controller.DisconnectAsync());
            Assert.IsTrue(controller.IsRunning);
            Assert.AreEqual(SlackConnectionPhase.Connected, controller.Current.Phase);
        }
        finally { Directory.Delete(path + ".paused"); }
    }

    [TestMethod]
    public void IncomingDm_DecodesOnlySlackEscapes_AndCanonicalizesTimestampPrecision()
    {
        const string text = "<코드> &copy; &#65; &lt;\n이모지 🥭";
        using var first = JsonDocument.Parse(Envelope(ts: "1794910000.1", change: message =>
            message["text"] = "&lt;코드&gt; &amp;copy; &amp;#65; &amp;lt;\n이모지 🥭"));
        using var retry = JsonDocument.Parse(Envelope(ts: "1794910000.100000"));
        var dm = SlackDmEvent.Parse(first.RootElement.GetProperty("payload"), Identity)!;
        Assert.AreEqual(text, dm.Body);
        Assert.AreEqual(SlackDmEvent.Parse(retry.RootElement.GetProperty("payload"), Identity)!.Id, dm.Id);
    }

    [DataTestMethod]
    [DataRow("U_SELF", false, true)]
    [DataRow("U_OTHER", false, false)]
    [DataRow("U_SELF", true, false)]
    public void SlackConnect_RequiresExplicitLocalUserAuthorizationForDifferentOuterTeam(string user, bool bot, bool accepted)
    {
        using var original = JsonDocument.Parse(Envelope(team: "T_REMOTE"));
        var payload = original.RootElement.GetProperty("payload");
        using var connected = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "event_callback", team_id = "T_REMOTE", @event = payload.GetProperty("event"),
            authorizations = new[] { new { team_id = Identity.TeamId, user_id = user, is_bot = bot } }
        }));
        Assert.AreEqual(accepted, SlackDmEvent.Parse(connected.RootElement, Identity) is not null);
    }

    [TestMethod]
    public async Task Api_RateLimitCooldownIsCancelableAndPerMethod_WithoutShorteningRetryAfter()
    {
        int calls = 0;
        using var api = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(request =>
        {
            Interlocked.Increment(ref calls);
            if (request.RequestUri!.AbsolutePath.EndsWith("users.info"))
            {
                var response = Json("""{"ok":false,"error":"ratelimited"}""");
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(600));
                return Task.FromResult(response);
            }
            return Task.FromResult(Json("""{"ok":true,"team_id":"T_TEST","user_id":"U_SELF","bot_id":null}"""));
        })));
        var error = await Assert.ThrowsExceptionAsync<SlackApiException>(() => api.GetUserNameAsync("U_OTHER", default));
        Assert.AreEqual(TimeSpan.FromSeconds(600), error.RetryAfter);
        Assert.AreEqual(Identity, await api.AuthenticateAsync(default), "Other API methods must remain usable.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => api.GetUserNameAsync("U_OTHER", cancel.Token));
        Assert.AreEqual(2, calls, "A subsequent lookup must not ignore the shared method cooldown.");
    }

    [TestMethod]
    public async Task Api_HandlesMalformedResponsesAndWhitespaceProfileNames()
    {
        using var invalid = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(_ =>
            Task.FromResult(Json("[]")))));
        var error = await Assert.ThrowsExceptionAsync<SlackApiException>(() => invalid.AuthenticateAsync(default));
        Assert.AreEqual("invalid_response", error.Code);
        using var profile = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(_ =>
            Task.FromResult(Json("""{"ok":true,"user":{"profile":{"display_name":"  ","real_name":" 새 이름 "}}}""")))));
        Assert.AreEqual("새 이름", await profile.GetUserNameAsync("U_OTHER", default));
    }

    [TestMethod]
    public void IncomingDm_PreservesTextUtcPrecisionAndStableMessageIdentity()
    {
        using var doc = JsonDocument.Parse(Envelope());
        var dm = SlackDmEvent.Parse(doc.RootElement.GetProperty("payload"), Identity)!;
        Assert.IsNotNull(dm);
        Assert.AreEqual("가상 회의 & 일정 확인", dm.Body);
        Assert.AreEqual(DateTimeKind.Utc, dm.Timestamp.Kind);
        Assert.AreEqual(1234560L, dm.Timestamp.Ticks % TimeSpan.TicksPerSecond);
        Assert.AreEqual("slack:T_TEST:D_TEST:1794910000.123456", dm.Id);
        var notification = dm.ToNotification("가상 팀원");
        Assert.AreEqual("Slack", notification.AppName);
        Assert.AreEqual("가상 팀원", notification.Sender);
        Assert.AreEqual(string.Empty, notification.Title, "A DM has no subject; do not invent one for display or AI.");
        Assert.AreEqual(dm.Body, notification.Body);
        Assert.AreEqual(dm.Timestamp, notification.Timestamp);
    }

    [DataTestMethod]
    [DataRow("channel_type", "channel")]
    [DataRow("channel_type", "mpim")]
    [DataRow("user", "U_SELF")]
    [DataRow("subtype", "message_changed")]
    [DataRow("subtype", "message_deleted")]
    [DataRow("bot_id", "B_TEST")]
    [DataRow("text", "")]
    [DataRow("user", "")]
    [DataRow("ts", "NaN")]
    [DataRow("ts", "999999999999999999999")]
    public void NonIncomingTextEvents_AreNotSentToAi(string field, string value)
    {
        using var doc = JsonDocument.Parse(Envelope(change: message => message[field] = value));
        Assert.IsNull(SlackDmEvent.Parse(doc.RootElement.GetProperty("payload"), Identity));
    }

    [TestMethod]
    public void OtherWorkspace_IsIgnored()
    {
        using var doc = JsonDocument.Parse(Envelope(team: "T_OTHER"));
        Assert.IsNull(SlackDmEvent.Parse(doc.RootElement.GetProperty("payload"), Identity));
    }

    [TestMethod]
    public void Credentials_RoundTripUsesWindowsUserEncryption_AndNeverPrintsTokens()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        var original = new SlackConnectionSettings("xapp-fake-test-only", "xoxp-fake-test-only");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(original);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, encrypted);
            Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains(original.UserToken));
            var loaded = SlackConnectionSettings.Load(path);
            Assert.AreEqual(original.AppToken, loaded.AppToken);
            Assert.AreEqual(original.UserToken, loaded.UserToken);
            Assert.IsFalse(loaded.ToString().Contains(original.UserToken));
            Assert.ThrowsException<ArgumentException>(() => new SlackConnectionSettings("xapp-fake", "xoxb-wrong-type"));
        }
        finally { CryptographicOperations.ZeroMemory(plain); File.Delete(path); }
    }

    [TestMethod]
    public async Task Api_UsesSeparateBearerTokensAndDoesNotSendThemInTheBody()
    {
        var requests = new List<(string Method, string? Bearer, string Body)>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter,
                await request.Content!.ReadAsStringAsync()));
            return Json(request.RequestUri.AbsolutePath.EndsWith("auth.test")
                ? """{"ok":true,"team_id":"T_TEST","user_id":"U_SELF"}"""
                : """{"ok":true,"url":"wss://wss-primary.slack.com/link/?ticket=fake"}""");
        }));
        using var api = new SlackApiClient(new("xapp-fake", "xoxp-fake"), http);
        Assert.AreEqual(Identity, await api.AuthenticateAsync(default));
        await api.OpenConnectionAsync(default);
        Assert.AreEqual("xoxp-fake", requests[0].Bearer);
        Assert.AreEqual("xapp-fake", requests[1].Bearer);
        Assert.IsTrue(requests.All(request => !request.Body.Contains("fake")));
    }

    [TestMethod]
    public async Task Api_RejectsBotIdentity_AndSanitizesUnexpectedErrorBodies()
    {
        using var botApi = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(_ =>
            Task.FromResult(Json("""{"ok":true,"team_id":"T_TEST","user_id":"U_BOT","bot_id":"B_TEST"}""")))));
        var botError = await Assert.ThrowsExceptionAsync<SlackApiException>(() => botApi.AuthenticateAsync(default));
        Assert.AreEqual("user_token_required", botError.Code);
        using var api = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(_ =>
            Task.FromResult(Json("""{"ok":false,"error":"pretend-private-data-must-not-be-logged"}""")))));
        var error = await Assert.ThrowsExceptionAsync<SlackApiException>(() => api.AuthenticateAsync(default));
        Assert.AreEqual("api_error", error.Message);
    }

    [TestMethod]
    public async Task Api_HonorsRateLimitAndRejectsAnUnexpectedSocketHost()
    {
        using var api = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(42));
            return Task.FromResult(response);
        })));
        var error = await Assert.ThrowsExceptionAsync<SlackApiException>(() => api.OpenConnectionAsync(default));
        Assert.AreEqual(TimeSpan.FromSeconds(42), error.RetryAfter);
        using var wrongHost = new SlackApiClient(new("xapp-fake", "xoxp-fake"), new HttpClient(new Handler(_ =>
            Task.FromResult(Json("""{"ok":true,"url":"wss://example.com/link/?ticket=fake"}""")))));
        await Assert.ThrowsExceptionAsync<SlackApiException>(() => wrongHost.OpenConnectionAsync(default));
    }

    [TestMethod]
    public async Task Socket_StopStartRetainsDeduplication_AndShortConnectionsIncreaseBackoff()
    {
        using var first = new FakeSocket();
        using var second = new FakeSocket();
        using var third = new FakeSocket();
        int created = 0;
        var delays = new ConcurrentQueue<TimeSpan>();
        using var listener = new SlackSocketModeListener(new FakeApi(),
            () => Interlocked.Increment(ref created) switch { 1 => first, 2 => second, _ => third },
            (delay, _) => { delays.Enqueue(delay); return Task.CompletedTask; });
        var received = new ConcurrentQueue<RawNotification>();
        listener.NotificationReceived += (_, raw) => received.Enqueue(raw);
        await listener.StartAsync();
        first.Push(Envelope());
        await UntilAsync(() => received.Count == 1);
        first.Push("""{"type":"disconnect"}""");
        await UntilAsync(() => created == 2);
        second.Push("""{"type":"disconnect"}""");
        await UntilAsync(() => created == 3);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays.ToArray());
        listener.Stop();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        // A fresh socket after a user-driven Stop/Start must not forget previously received IDs.
        using var a = new FakeSocket();
        using var b = new FakeSocket();
        int starts = 0;
        using var restart = new SlackSocketModeListener(new FakeApi(), () => ++starts == 1 ? a : b);
        restart.NotificationReceived += (_, raw) => received.Enqueue(raw);
        await restart.StartAsync();
        a.Push(Envelope());
        await UntilAsync(() => received.Count == 2);
        restart.Stop();
        await restart.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await restart.StartAsync();
        b.Push(Envelope("duplicate"));
        b.Push(Envelope("distinct", "1794910001.123456"));
        await UntilAsync(() => received.Count == 3);
        restart.Stop();
        await restart.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(3, received.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingDm_BurstKeepsReceiptContextAndSession_AndDrainWaitsForPreparation(bool stopBeforeLookupCompletes)
    {
        using var socket = new FakeSocket();
        var lookup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slack = new SlackSocketModeListener(new FakeApi { Name = _ => lookup.Task }, () => socket);
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var repository = new SqliteNotificationRepository(db);
        var filter = new CapturingFilter();
        var originalContext = new CurrentContext("Code", "가상 작업", DateTime.UtcNow);
        var tracker = new Tracker { CurrentContext = originalContext };
        bool focus = true;
        string session = "original-session";
        using var merger = new SlackNotificationMerger(new FakeNotificationListener(), slack);
        using var pipeline = new NotificationPipelineCoordinator(merger, tracker, repository, filter, () => focus, () => session);
        int pending = 0;
        slack.NotificationPending += (_, _) => Interlocked.Increment(ref pending);
        await pipeline.StartAsync();
        socket.Push(Envelope());
        socket.Push(Envelope("second", "1794910001.123456"));
        await UntilAsync(() => pending == 2);
        focus = false;
        session = "later-session";
        tracker.CurrentContext = CurrentContext.Empty;
        var drain = pipeline.DrainSessionAsync("original-session");
        Assert.IsFalse(drain.IsCompleted, "A report must wait for both queued DMs before collecting this session.");
        if (stopBeforeLookupCompletes) pipeline.Stop();
        else lookup.SetResult("가상 팀원");
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        string[] ids = ["slack:T_TEST:D_TEST:1794910000.123456", "slack:T_TEST:D_TEST:1794910001.123456"];
        foreach (string id in ids)
        {
            var record = await repository.GetByIdAsync(id);
            if (stopBeforeLookupCompletes) Assert.IsNull(record);
            else Assert.AreEqual("original-session", record!.SessionId);
        }
        if (!stopBeforeLookupCompletes)
        {
            Assert.AreEqual(2, filter.Calls);
            Assert.AreSame(originalContext, filter.Context);
        }
        pipeline.Stop();
        await slack.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task LongUnicodeDm_IsReassembledAndPersistedInFull_AndPersistedRetrySkipsAi()
    {
        string body = string.Concat(Enumerable.Repeat("긴 원문 🥭 줄바꿈\n", 6000));
        using var socket = new FakeSocket();
        var slack = new SlackSocketModeListener(new FakeApi(), () => socket);
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var repository = new SqliteNotificationRepository(db);
        var filter = new CapturingFilter();
        using var pipeline = new NotificationPipelineCoordinator(slack, new Tracker(), repository, filter, () => true, () => "long-session");
        var processed = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.NotificationProcessed += (_, record) => processed.TrySetResult(record);
        await pipeline.StartAsync();
        socket.Push(Envelope(change: message => message["text"] = body), fragmented: true);
        var record = await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(body, record.Body);
        Assert.AreEqual(body, (await repository.GetByIdAsync(record.Id))!.Body);
        pipeline.Stop();
        await slack.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        using var replay = new FakeNotificationListener();
        var replayFilter = new CapturingFilter();
        using var restarted = new NotificationPipelineCoordinator(replay, new Tracker(), repository, replayFilter, () => true, () => "new-session");
        await restarted.StartAsync();
        replay.EmitNotification(filter.Input!);
        await restarted.DrainSessionAsync("new-session");
        Assert.AreEqual(0, replayFilter.Calls);
        Assert.AreEqual("long-session", (await repository.GetByIdAsync(record.Id))!.SessionId);
    }

    [TestMethod]
    public async Task Socket_FullQueueDoesNotAcknowledgeLostEvent_AndStopReleasesPendingWork()
    {
        using var socket = new FakeSocket();
        var stalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi { Check = _ => stalled.Task };
        using var listener = new SlackSocketModeListener(api, () => socket, queueCapacity: 1);
        var preparations = new ConcurrentQueue<Task<RawNotification?>>();
        listener.NotificationPending += (_, pending) => preparations.Enqueue(pending.PrepareAsync());
        await listener.StartAsync();
        socket.Push(Envelope());
        await UntilAsync(() => api.ChannelChecks == 1);
        socket.Push(Envelope("queued", "1794910001.123456"));
        await UntilAsync(() => preparations.Count == 2);
        socket.Push(Envelope("overflow", "1794910002.123456"));
        await UntilAsync(() => listener.Status.Contains("receive_queue_full"));
        var acked = new List<string>();
        while (socket.Acknowledgements.Reader.TryRead(out var id)) acked.Add(id);
        CollectionAssert.AreEqual(new[] { "envelope-1", "queued" }, acked);
        listener.Stop();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var preparation in preparations)
            Assert.IsNull(await preparation.WaitAsync(TimeSpan.FromSeconds(5)), "Canceled access lookups must not hang session drain.");
    }

    [TestMethod]
    public async Task Merger_OneSourceFailureDoesNotDisableOrLeakTheOtherSource()
    {
        var broken = new BrokenListener();
        var slack = new FakeNotificationListener();
        var merger = new SlackNotificationMerger(broken, slack);
        Assert.IsTrue(await merger.StartAsync());
        Assert.IsTrue(slack.IsRunning);
        merger.Stop();
        Assert.IsFalse(slack.IsRunning);
        await merger.StartAsync();
        merger.Dispose();
        Assert.IsFalse(slack.IsRunning);
        Assert.AreEqual(1, broken.DisposeCalls);
    }

    [TestMethod]
    public async Task PendingWindowsToast_UsesReceiptSessionBeforeCoalescingDelay()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var standard = new FakeNotificationListener();
        using var merger = new SlackNotificationMerger(standard, new FakeNotificationListener(), TimeSpan.FromMilliseconds(50));
        var repository = new SqliteNotificationRepository(db);
        bool focus = true;
        using var pipeline = new NotificationPipelineCoordinator(merger, new Tracker(), repository, new CapturingFilter(), () => focus, () => "toast-session");
        await pipeline.StartAsync();
        standard.EmitNotification(new("win_delayed", "Slack", "가상 팀원", "#가상채널", "가상 알림", DateTime.UtcNow));
        focus = false;
        var drain = pipeline.DrainSessionAsync("toast-session");
        Assert.IsFalse(drain.IsCompleted);
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("toast-session", (await repository.GetByIdAsync("win_delayed"))!.SessionId);
    }

    [TestMethod]
    public async Task Socket_AcknowledgesFragmentedEventsBeforeNameLookup_AndSuppressesRetries()
    {
        using var socket = new FakeSocket();
        var releaseName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi { Name = _ => releaseName.Task };
        using var listener = new SlackSocketModeListener(api, () => socket);
        var notifications = new ConcurrentQueue<RawNotification>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.NotificationReceived += (_, notification) => { notifications.Enqueue(notification); delivered.TrySetResult(); };
        await listener.StartAsync();
        socket.Push(Envelope(), fragmented: true);
        socket.Push(Envelope("retry", ts: "1794910000.123456"));
        Assert.AreEqual("envelope-1", await socket.Acknowledgements.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual("retry", await socket.Acknowledgements.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, notifications.Count, "A slow lookup must not prevent the second acknowledgement.");
        releaseName.SetResult("가상 팀원");
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A subsequent distinct event acts as a barrier after the duplicate in the single-reader queue.
        socket.Push(Envelope("second", ts: "1794910001.123456"));
        await UntilAsync(() => notifications.Count == 2);
        Assert.AreEqual(2, api.NameCalls, "Each distinct DM refreshes its sender name; retries are deduplicated first.");
        Assert.AreEqual(2, api.ChannelChecks);
        listener.Stop();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(listener.IsRunning);
    }

    [TestMethod]
    public async Task Socket_UsesChangedDisplayNameOnTheNextDm_WithoutRestarting()
    {
        using var socket = new FakeSocket();
        string currentName = "가상 이전 이름";
        var api = new FakeApi { Name = _ => Task.FromResult(currentName) };
        using var listener = new SlackSocketModeListener(api, () => socket);
        var notifications = new ConcurrentQueue<RawNotification>();
        listener.NotificationReceived += (_, notification) => notifications.Enqueue(notification);
        await listener.StartAsync();
        socket.Push(Envelope());
        await UntilAsync(() => notifications.Count == 1);
        currentName = "가상 변경 이름";
        socket.Push(Envelope("after-rename", ts: "1794910001.123456"));
        await UntilAsync(() => notifications.Count == 2);
        CollectionAssert.AreEqual(new[] { "가상 이전 이름", "가상 변경 이름" },
            notifications.Select(notification => notification.Sender).ToArray());
        Assert.AreEqual(2, api.NameCalls);
        listener.Stop();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Socket_ChecksDmVisibilityWithTheAuthenticatedUserBeforeDelivering()
    {
        using var socket = new FakeSocket();
        var api = new FakeApi { CanRead = false };
        using var listener = new SlackSocketModeListener(api, () => socket);
        int received = 0;
        listener.NotificationReceived += (_, _) => Interlocked.Increment(ref received);
        await listener.StartAsync();
        socket.Push(Envelope());
        await UntilAsync(() => api.ChannelChecks == 1);
        listener.Stop();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, received);
        Assert.AreEqual(0, api.NameCalls);
    }

    [TestMethod]
    public async Task Socket_StopsAfterUserAccessIsRevoked_EvenForAPreviouslyVerifiedDm()
    {
        using var socket = new FakeSocket();
        var api = new FakeApi();
        using var listener = new SlackSocketModeListener(api, () => socket);
        int received = 0;
        listener.NotificationReceived += (_, _) => Interlocked.Increment(ref received);
        await listener.StartAsync();
        socket.Push(Envelope());
        await UntilAsync(() => received == 1);
        api.ChannelError = new SlackApiException("token_revoked");
        socket.Push(Envelope("after-revocation", ts: "1794910001.123456"));
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, received);
        Assert.IsFalse(listener.IsRunning);
        StringAssert.Contains(listener.Status, "token_revoked");
    }

    [TestMethod]
    public async Task Socket_ReconnectsAfterSlackRefresh_AndStopsRetryingInvalidCredentials()
    {
        var api = new FakeApi();
        using var first = new FakeSocket();
        using var second = new FakeSocket();
        int created = 0;
        using var listener = new SlackSocketModeListener(api, () => Interlocked.Increment(ref created) == 1 ? first : second);
        await listener.StartAsync();
        first.Push("""{"type":"disconnect","reason":"refresh_requested"}""");
        await UntilAsync(() => created == 2);
        listener.Stop();
        await listener.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, api.OpenCalls);

        using var invalid = new SlackSocketModeListener(new FakeApi { AuthError = new SlackApiException("invalid_auth") },
            () => throw new AssertFailedException("Invalid auth must not open a socket."));
        await invalid.StartAsync();
        await invalid.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(invalid.IsRunning);
        StringAssert.Contains(invalid.Status, "invalid_auth");
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SocketDm_UsesExistingFilterAndSessionPersistence(bool focus)
    {
        using var socket = new FakeSocket();
        var slack = new SlackSocketModeListener(new FakeApi(), () => socket);
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        var repository = new SqliteNotificationRepository(db);
        var filter = new CapturingFilter();
        using var merger = new SlackNotificationMerger(new FakeNotificationListener(), slack);
        using var pipeline = new NotificationPipelineCoordinator(merger, new Tracker(), repository, filter,
            () => focus, () => "slack-test-session");
        var processed = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.NotificationProcessed += (_, record) => processed.TrySetResult(record);
        await pipeline.StartAsync();
        socket.Push(Envelope());
        var result = await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("가상 회의 & 일정 확인", filter.Input!.Body);
        Assert.AreEqual("Slack", filter.Input.AppName);
        Assert.AreEqual(string.Empty, filter.Input.Title, "The AI input must not contain a synthetic DM title.");
        Assert.AreEqual(focus ? true : (bool?)null, result.IsPassed);
        Assert.AreEqual(focus, await repository.GetByIdAsync(result.Id) is not null);
        pipeline.Stop();
        await slack.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Merger_PrefersApiForMatchingToasts_ButKeepsOtherChannelsAndApps()
    {
        var standard = new FakeNotificationListener();
        var slack = new FakeNotificationListener();
        using var merger = new SlackNotificationMerger(standard, slack, TimeSpan.FromMilliseconds(30));
        var received = new ConcurrentQueue<RawNotification>();
        merger.NotificationReceived += (_, notification) => received.Enqueue(notification);
        await merger.StartAsync();
        var api = new RawNotification("slack:1", "Slack", "가상 팀원", "개인 메시지", "회의 안내", DateTime.UtcNow);
        standard.EmitNotification(api with { Id = "win_1", Sender = "", Title = "가상 팀원" });
        slack.EmitNotification(api);
        standard.EmitNotification(api with { Id = "win_channel", Sender = "", Title = "#다른채널" });
        standard.EmitNotification(api with { Id = "other_app", AppName = "Other" });
        await UntilAsync(() => received.Count == 3);
        CollectionAssert.AreEquivalent(new[] { "slack:1", "win_channel", "other_app" }, received.Select(n => n.Id).ToArray());
        Assert.IsFalse(SlackNotificationMerger.AreSameMessage(api, api with { Sender = "다른 사람", Title = "다른 사람" }));
        Assert.IsFalse(SlackNotificationMerger.AreSameMessage(api, api with { Title = "#채널" }),
            "The same person's channel post is distinct from a DM even when its body is identical.");
    }

    [TestMethod]
    public async Task Merger_CancelsPendingToastOnStop_AndDoesNotMergeTwoSeparateIdenticalMessages()
    {
        var standard = new FakeNotificationListener();
        var slack = new FakeNotificationListener();
        using var merger = new SlackNotificationMerger(standard, slack, TimeSpan.FromMilliseconds(30));
        var received = new ConcurrentQueue<RawNotification>();
        merger.NotificationReceived += (_, notification) => received.Enqueue(notification);
        await merger.StartAsync();
        var api = new RawNotification("slack:1", "Slack", "가상 팀원", "개인 메시지", "네", DateTime.UtcNow);
        slack.EmitNotification(api);
        slack.EmitNotification(api with { Id = "slack:2" });
        standard.EmitNotification(api with { Id = "win_1" });
        merger.Stop();
        await Task.Delay(60);
        Assert.AreEqual(2, received.Count);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [TestMethod]
    [DoNotParallelize]
    public async Task Pipeline_LogsDistinctApiAndWindowsSources_WithoutPersonalText()
    {
        using var db = SqliteDatabase.CreateInMemory();
        db.Initialize();
        using var listener = new FakeNotificationListener();
        using var pipeline = new NotificationPipelineCoordinator(listener, new Tracker(),
            new SqliteNotificationRepository(db), new CapturingFilter(), () => false, () => null);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        pipeline.NotificationProcessed += (_, _) => { if (Interlocked.Increment(ref count) == 2) completed.TrySetResult(); };
        using var captured = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(captured);
            await pipeline.StartAsync();
            var notification = new RawNotification("slack:T_TEST:D_TEST:1", "Slack", "가상 비공개 이름",
                "가상 비공개 제목", "가상 비공개 본문", DateTime.UtcNow);
            listener.EmitNotification(notification);
            listener.EmitNotification(notification with { Id = "win_123_456" });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Console.SetOut(previous); }
        string log = captured.ToString();
        StringAssert.Contains(log, "[Slack API] Slack");
        StringAssert.Contains(log, "[Windows 알림] Slack");
        Assert.IsFalse(log.Contains("가상 비공개"), "The source label must not expose sender, title or body.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }

    private sealed class BrokenListener : IWindowsNotificationListener
    {
#pragma warning disable CS0067
        public event EventHandler<RawNotification>? NotificationReceived;
#pragma warning restore CS0067
        public bool IsRunning => false;
        internal int DisposeCalls;
        public Task<bool> StartAsync() => throw new IOException("synthetic source failure");
        public void Stop() => throw new IOException("synthetic stop failure");
        public void Dispose() { DisposeCalls++; throw new IOException("synthetic dispose failure"); }
    }

    private sealed class FakeApi : ISlackApiClient
    {
        internal SlackIdentity Account { get; init; } = Identity;
        internal Func<CancellationToken, Task<string>> Name { get; init; } = _ => Task.FromResult("가상 팀원");
        internal Func<CancellationToken, Task<bool>>? Check { get; init; }
        internal bool CanRead { get; init; } = true;
        internal Exception? AuthError { get; init; }
        internal Exception? ChannelError;
        internal int NameCalls, ChannelChecks, OpenCalls;
        public Task<SlackIdentity> AuthenticateAsync(CancellationToken token) => AuthError is null
            ? Task.FromResult(Account) : Task.FromException<SlackIdentity>(AuthError);
        public Task<Uri> OpenConnectionAsync(CancellationToken token)
        {
            Interlocked.Increment(ref OpenCalls);
            return Task.FromResult(new Uri("wss://wss-primary.slack.com/link/?ticket=fake"));
        }
        public Task<bool> IsPersonalDmAsync(string channel, CancellationToken token)
        {
            Interlocked.Increment(ref ChannelChecks);
            if (Check is not null) return Check(token);
            return ChannelError is null ? Task.FromResult(CanRead) : Task.FromException<bool>(ChannelError);
        }
        public Task<string> GetUserNameAsync(string user, CancellationToken token)
        {
            Interlocked.Increment(ref NameCalls);
            return Name(token);
        }
        public void Dispose() { }
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, bool End)> _frames = Channel.CreateUnbounded<(byte[], bool)>();
        private (byte[] Bytes, bool End)? _remaining;
        private int _offset;
        internal Channel<string> Acknowledgements { get; } = Channel.CreateUnbounded<string>();
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        internal void Push(string text, bool fragmented = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (fragmented)
            {
                int split = bytes.Length / 2;
                _frames.Writer.TryWrite((bytes[..split], false));
                _frames.Writer.TryWrite((bytes[split..], true));
            }
            else _frames.Writer.TryWrite((bytes, true));
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var frame = _remaining ?? await _frames.Reader.ReadAsync(token);
            int count = Math.Min(buffer.Count, frame.Bytes.Length - _offset);
            frame.Bytes.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            bool complete = _offset == frame.Bytes.Length;
            _remaining = complete ? null : frame;
            if (complete) _offset = 0;
            return new(count, WebSocketMessageType.Text, complete && frame.End);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory());
            Acknowledgements.Writer.TryWrite(doc.RootElement.GetProperty("envelope_id").GetString()!);
            return Task.CompletedTask;
        }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
    }

    private sealed class CapturingFilter : IFilterEngine
    {
        internal RawNotification? Input;
        internal CurrentContext? Context;
        internal int Calls;
        public Task<FilterResult> EvaluateAsync(RawNotification notification, CurrentContext context)
        {
            Input = notification;
            Context = context;
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new FilterResult(notification.Id, true, 5, 4, "업무"));
        }
    }

    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext { get; set; } = CurrentContext.Empty;
#pragma warning disable CS0067
        public event EventHandler<CurrentContext>? ContextChanged;
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded;
#pragma warning restore CS0067
        public CurrentContext CaptureNow() => CurrentContext;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

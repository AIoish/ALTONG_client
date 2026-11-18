using System.Net;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Altong.Client.Data;
using Altong.Client.Data.Models;
using Altong.Client.Data.Repositories;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Altong.Slack.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SlackOAuthFlowTests
{
    [TestMethod]
    public async Task DesktopToServer_ApprovalDmPipelineDisconnectAndReapproval_WorkWithoutUserTokenEntry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AltongSlackFlowTests", Guid.NewGuid().ToString("N"));
        var settings = new SlackServerOptions
        {
            PublicBaseUrl = new("https://relay.test/"), ClientId = "synthetic-client", ClientSecret = "synthetic-secret", AppId = "A_TEST",
            AppToken = "xapp-synthetic", DataDirectory = directory,
            EncryptionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Retention = TimeSpan.FromHours(1)
        };
        using var slackHttp = new HttpClient(new FakeSlack());
        try
        {
            await using var server = SlackServerApplication.Create(["--urls", "http://127.0.0.1:0"], settings, slackHttp, startWorkers: false);
            await server.StartAsync();
            var url = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            server.Services.GetRequiredService<SlackReceiverHealth>().Connected = true;
            using var browser = new HttpClient { BaseAddress = url };
            int approvals = 0;
            var callbackTasks = new List<Task<HttpResponseMessage>>();
            using var controller = new SlackRelayConnectionController(url, store: new(Path.Combine(directory, "device.dat")),
                openBrowser: uri =>
                {
                    Assert.AreEqual("slack.com", uri.Host);
                    string state = uri.Query.TrimStart('?').Split('&').Select(v => v.Split('=', 2)).Single(v => v[0] == "state")[1];
                    lock (callbackTasks) callbackTasks.Add(browser.GetAsync("/oauth/slack/callback?state=" + state + "&code=synthetic-code"));
                    Interlocked.Increment(ref approvals);
                }, delay: (_, token) => Task.Delay(10, token));
            using var db = SqliteDatabase.CreateInMemory(); db.Initialize();
            var repository = new SqliteNotificationRepository(db); var filter = new PassFilter();
            using var merger = new SlackNotificationMerger(new FakeNotificationListener(), controller);
            using var pipeline = new NotificationPipelineCoordinator(merger, new Tracker(), repository, filter, () => true, () => "synthetic-focus-session");
            var processed = new TaskCompletionSource<NotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            pipeline.NotificationProcessed += (_, notification) => processed.TrySetResult(notification);
            await pipeline.StartAsync();
            Assert.AreEqual(0, approvals, "App startup must not prompt for Slack approval.");
            Assert.IsTrue(await controller.ConnectAsync());
            await Until(() => controller.IsRunning);
            var store = server.Services.GetRequiredService<EncryptedSlackStore>();
            var router = server.Services.GetRequiredService<SlackEventRouter>();
            const string payload = """{"type":"event_callback","api_app_id":"A_TEST","team_id":"T_TEST","event_context":"synthetic-context","event_id":"Ev_1","event":{"type":"message","channel_type":"im","channel":"D_TEST","user":"U_SENDER","text":"synthetic incoming DM","ts":"1794910000.1"}}""";
            await router.RouteAsync(new("Ev_1", payload, DateTimeOffset.UtcNow), default);
            var record = await processed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await pipeline.DrainSessionAsync("synthetic-focus-session");
            Assert.AreEqual("synthetic incoming DM", record.Body);
            Assert.AreEqual("synthetic-focus-session", record.SessionId);
            Assert.AreEqual("", record.Title);
            Assert.IsNotNull(await repository.GetByIdAsync(record.Id));
            Assert.AreEqual(1, filter.Calls);
            await controller.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.AreEqual(SlackConnectionPhase.NotConnected, controller.Current.Phase);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "device.dat")));
            Assert.IsTrue(await controller.ConnectAsync());
            await Until(() => approvals == 2 && controller.IsRunning);
            Task<HttpResponseMessage>[] callbacks; lock (callbackTasks) callbacks = callbackTasks.ToArray();
            foreach (var callback in callbacks) (await callback).EnsureSuccessStatusCode();
            pipeline.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await server.StopAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DesktopToCloudflare_WranglerApprovalDmPipelineDisconnectAndReapproval()
    {
        if (Environment.GetEnvironmentVariable("ALTONG_RUN_WRANGLER_TEST") != "1")
            Assert.Inconclusive("Run with the local Wrangler test Worker and ALTONG_RUN_WRANGLER_TEST=1.");
        var url = SlackRelayConfiguration.ServerUrl();
        Assert.IsNotNull(url);
        Assert.IsTrue(url.IsLoopback && url.Port == 8793);

        string testId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "AltongWranglerFlowTests", testId);
        string sessionFile = Path.Combine(directory, "device.dat");
        var sessions = new SlackRelaySessionStore(sessionFile);
        var deliveryRequests = new DeliveryRequestCounter();
        var relayApi = new SlackRelayApiClient(url, new HttpClient(deliveryRequests));
        using var browser = new HttpClient { BaseAddress = url };
        var callbacks = new ConcurrentQueue<Task<HttpResponseMessage>>();
        int approvals = 0;
        try
        {
            using var controller = new SlackRelayConnectionController(url, api: relayApi, store: sessions, openBrowser: authorization =>
            {
                Assert.AreEqual("slack.com", authorization.Host);
                string state = authorization.Query.TrimStart('?').Split('&')
                    .Select(part => part.Split('=', 2)).Single(part => part[0] == "state")[1];
                int attempt = Interlocked.Increment(ref approvals);
                string code = "T_TEST.U_SELF." + testId + attempt.ToString(CultureInfo.InvariantCulture);
                callbacks.Enqueue(browser.GetAsync("/oauth/slack/callback?state=" + state + "&code=" + code));
            }, delay: (_, token) => Task.Delay(100, token));
            using var db = SqliteDatabase.CreateInMemory();
            db.Initialize();
            var repository = new SqliteNotificationRepository(db);
            var filter = new PassFilter();
            using var merger = new SlackNotificationMerger(new FakeNotificationListener(), controller);
            using var pipeline = new NotificationPipelineCoordinator(merger, new Tracker(), repository, filter,
                () => true, () => "wrangler-focus-session");
            var processed = new ConcurrentQueue<NotificationRecord>();
            pipeline.NotificationProcessed += (_, notification) => processed.Enqueue(notification);
            await pipeline.StartAsync();
            Assert.AreEqual(0, approvals);
            Assert.IsTrue(await controller.ConnectAsync());
            await Until(() => controller.IsRunning);
            var firstSession = sessions.Load(url);
            Assert.IsNotNull(firstSession);
            await Task.Delay(350);
            Assert.AreEqual(0, deliveryRequests.MessageFetches, "An idle socket must not cause HTTP polling.");

            await SendWranglerDmAsync(browser, "synthetic incoming DM");
            await Until(() => processed.Count == 1);
            await pipeline.DrainSessionAsync("wrangler-focus-session");
            Assert.IsTrue(processed.TryPeek(out var firstMessage));
            Assert.AreEqual("synthetic incoming DM", firstMessage.Body);
            Assert.AreEqual("wrangler-focus-session", firstMessage.SessionId);
            Assert.IsNotNull(await repository.GetByIdAsync(firstMessage.Id));
            await Task.Delay(350);
            Assert.AreEqual(1, deliveryRequests.MessageFetches, "Exactly one HTTP fetch for the incoming DM.");

            await controller.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.AreEqual(SlackConnectionPhase.NotConnected, controller.Current.Phase);
            Assert.IsFalse(File.Exists(sessionFile));
            using (var oldDeviceRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/slack/session"))
            {
                oldDeviceRequest.Headers.Authorization = new("Bearer", firstSession.Token);
                using var result = await browser.SendAsync(oldDeviceRequest);
                Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            }
            Assert.IsTrue(await controller.ConnectAsync());
            await Until(() => approvals == 2 && controller.IsRunning);
            Assert.AreEqual(firstSession.UserId, sessions.Load(url)?.UserId);
            await SendWranglerDmAsync(browser, "synthetic DM after reapproval");
            await Until(() => processed.Count == 2);
            await pipeline.DrainSessionAsync("wrangler-focus-session");
            var secondMessage = processed.Last();
            Assert.AreEqual("synthetic DM after reapproval", secondMessage.Body);
            Assert.IsNotNull(await repository.GetByIdAsync(secondMessage.Id));
            Assert.AreEqual(2, filter.Calls);
            foreach (var callback in callbacks)
            {
                using var result = await callback;
                result.EnsureSuccessStatusCode();
            }
            await Until(() => deliveryRequests.Acknowledgements == 2);
            var secondSession = sessions.Load(url);
            Assert.IsNotNull(secondSession);
            using var streamTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var observedStream = await relayApi.ConnectStreamAsync(secondSession.Token, streamTimeout.Token);
            await relayApi.DisconnectAsync(secondSession.Token, CancellationToken.None);
            var revoked = await Assert.ThrowsExceptionAsync<SlackRelayException>(
                () => SlackRelayApiClient.WaitForMessagesAsync(observedStream, streamTimeout.Token));
            Assert.AreEqual("session_expired", revoked.Code);
            await Until(() => controller.Current.Phase == SlackConnectionPhase.NeedsAttention && !File.Exists(sessionFile));
            pipeline.Stop();
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DeliveryRequestCounter() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        private int _messageFetches;
        private int _acknowledgements;
        internal int MessageFetches => Volatile.Read(ref _messageFetches);
        internal int Acknowledgements => Volatile.Read(ref _acknowledgements);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/slack/messages")
                Interlocked.Increment(ref _messageFetches);
            var result = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/slack/messages/ack" && result.IsSuccessStatusCode)
                Interlocked.Increment(ref _acknowledgements);
            return result;
        }
    }

    private static async Task SendWranglerDmAsync(HttpClient browser, string body)
    {
        var now = DateTimeOffset.UtcNow;
        string ts = (now.ToUnixTimeMilliseconds() / 1000m).ToString("0.000000", CultureInfo.InvariantCulture);
        string timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string payload = JsonSerializer.Serialize(new
        {
            type = "event_callback",
            api_app_id = "A_TEST",
            team_id = "T_TEST",
            event_context = "T_TEST:U_SELF",
            event_id = "Ev_" + Guid.NewGuid().ToString("N"),
            @event = new { type = "message", channel_type = "im", channel = "D_TEST", user = "U_SENDER", text = body, ts }
        });
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes("synthetic-signing-key"),
            Encoding.UTF8.GetBytes("v0:" + timestamp + ":" + payload));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/slack/events")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", "v0=" + Convert.ToHexStringLower(mac));
        using var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class FakeSlack : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string json = request.RequestUri!.AbsolutePath.Split('/').Last() switch
            {
                "oauth.v2.access" => """{"ok":true,"app_id":"A_TEST","team":{"id":"T_TEST"},"access_token":"xoxb-unused","authed_user":{"id":"U_SELF","access_token":"xoxp-synthetic","token_type":"user","scope":"im:history,im:read,users:read"}}""",
                "auth.test" => """{"ok":true,"user_id":"U_SELF","team_id":"T_TEST","user":"Synthetic account","team":"Synthetic workspace"}""",
                "apps.event.authorizations.list" => """{"ok":true,"authorizations":[{"team_id":"T_TEST","user_id":"U_SELF","is_bot":false}],"response_metadata":{"next_cursor":""}}""",
                "conversations.info" => """{"ok":true,"channel":{"is_im":true}}""",
                "users.info" => """{"ok":true,"user":{"profile":{"display_name":"Synthetic sender"}}}""",
                "auth.revoke" => """{"ok":true,"revoked":true}""",
                _ => throw new AssertFailedException("Unexpected Slack API method.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class PassFilter : IFilterEngine
    {
        internal int Calls;
        public Task<FilterResult> EvaluateAsync(RawNotification notification, CurrentContext context)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new FilterResult(notification.Id, true, 5, 4, "업무")); }
    }
    private sealed class Tracker : IActiveWindowTracker
    {
        public CurrentContext CurrentContext => CurrentContext.Empty;
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

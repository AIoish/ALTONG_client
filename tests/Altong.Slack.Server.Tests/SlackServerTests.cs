using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Altong.Slack.Contracts;
using Altong.Slack.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Slack.Server.Tests;

[TestClass]
public sealed class SlackServerTests
{
    [TestMethod]
    public void Pairing_RequiresOriginalProof_ConsumesStateOnce_AndCanRetryCompletedClaim()
    {
        using var fixture = new Fixture(); var store = fixture.Store;
        string state = SlackConnectionProof.NewSecret(), proof = SlackConnectionProof.NewSecret();
        var attempt = store.BeginPairing(state, SlackConnectionProof.Hash(proof));
        Assert.IsNull(store.PairingStatus(attempt.Id, SlackConnectionProof.NewSecret()));
        Assert.AreEqual(attempt.Id, store.ConsumeState(state));
        Assert.IsNull(store.ConsumeState(state));
        var session = store.CompletePairing(attempt.Id, Credential("U_A"));
        Assert.AreEqual(session.Token, store.PairingStatus(attempt.Id, proof)!.Session!.Token);
        Assert.AreEqual(session.Token, store.PairingStatus(attempt.Id, proof)!.Session!.Token);
        Assert.IsNotNull(store.Authenticate(session.Token));
        Assert.IsNull(store.Authenticate(SlackConnectionProof.NewSecret()));
    }

    [TestMethod]
    public void Pairing_ExpiredOrDeniedStateCannotBeReused()
    {
        var clock = new Clock(); using var fixture = new Fixture(clock);
        string state = SlackConnectionProof.NewSecret(), proof = SlackConnectionProof.NewSecret();
        var attempt = fixture.Store.BeginPairing(state, SlackConnectionProof.Hash(proof));
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.IsNull(fixture.Store.ConsumeState(state));
        Assert.IsNull(fixture.Store.PairingStatus(attempt.Id, proof));
        var denied = fixture.Pair("U_A");
        fixture.Store.Disconnect(fixture.Store.Authenticate(denied.Token)!);
        Assert.IsNull(fixture.Store.Authenticate(denied.Token));
    }

    [TestMethod]
    public void Store_PersistsEncryptedCredentialsAndMessagesAcrossRestart_AndLimitsAcknowledgementToDevice()
    {
        using var fixture = new Fixture();
        var a = fixture.Pair("U_A"); var b = fixture.Pair("U_B");
        var da = fixture.Store.Authenticate(a.Token)!; var db = fixture.Store.Authenticate(b.Token)!;
        var message = new SlackRelayMessage("slack:T_TEST:D_TEST:1.000000", "synthetic private sender", "synthetic private message", DateTime.UnixEpoch);
        fixture.Store.Deliver(a.UserId, message, DateTimeOffset.UtcNow);
        fixture.Store.Enqueue("Ev_1", "synthetic private event payload");
        var delivery = fixture.Store.Deliveries(da).Single();
        fixture.Store.Acknowledge(db, [delivery.Id]);
        Assert.AreEqual(1, fixture.Store.Deliveries(da).Length);
        using (var restarted = new EncryptedSlackStore(fixture.Options))
        {
            Assert.IsNotNull(restarted.Authenticate(a.Token));
            Assert.AreEqual(message, restarted.Deliveries(da).Single().Message);
            Assert.AreEqual("Ev_1", restarted.NextInbox()!.Id);
        }
        foreach (string file in Directory.GetFiles(fixture.Path))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            string bytes = Encoding.UTF8.GetString(memory.ToArray());
            Assert.IsFalse(bytes.Contains("synthetic private"));
            Assert.IsFalse(bytes.Contains("xoxp-synthetic"));
            Assert.IsFalse(bytes.Contains(a.Token));
        }
        fixture.Store.Acknowledge(da, [delivery.Id]);
        Assert.AreEqual(0, fixture.Store.Deliveries(da).Length);
        fixture.Store.Deliver(a.UserId, message, DateTimeOffset.UtcNow);
        Assert.AreEqual(0, fixture.Store.Deliveries(da).Length, "A replay after acknowledgement must not resurrect a delivery.");
    }

    [TestMethod]
    public async Task OAuth_UsesOnlyUserToken_ValidatesIdentityAndScopes()
    {
        using var fixture = new Fixture();
        var methods = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            methods.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("oauth.v2.access"))
            {
                string body = await request.Content!.ReadAsStringAsync();
                StringAssert.Contains(body, "redirect_uri=https%3A%2F%2Frelay.test%2Foauth%2Fslack%2Fcallback");
                return Json(OAuth());
            }
            Assert.AreEqual("xoxp-synthetic", request.Headers.Authorization!.Parameter);
            return Json("""{"ok":true,"team_id":"T_TEST","user_id":"U_A","team":"Test workspace","user":"Test account"}""");
        }));
        using var api = new SlackWebApi(fixture.Options, fixture.Store, http);
        var credential = await api.ExchangeAsync("synthetic-code", default);
        Assert.AreEqual("U_A", credential.Account.UserId);
        Assert.AreEqual("xoxp-synthetic", credential.AccessToken);
        Assert.IsFalse(credential.ToString().Contains(credential.AccessToken));
        Assert.IsTrue(api.AuthorizationUrl("synthetic-state").Contains("user_scope="));
        Assert.IsFalse(api.AuthorizationUrl("synthetic-state").Contains("&team="));
        Assert.AreEqual(2, methods.Count);
    }

    [DataTestMethod]
    [DataRow("bot", "im:history,im:read,users:read", "A_TEST")]
    [DataRow("user", "im:read", "A_TEST")]
    [DataRow("user", "im:history,im:read,users:read", "A_OTHER")]
    public async Task OAuth_RejectsBotInsufficientScopesAndDifferentApp(string type, string scopes, string app)
    {
        using var fixture = new Fixture();
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(_ => Task.FromResult(Json(OAuth(type, scopes, app))))));
        await Assert.ThrowsExceptionAsync<SlackServiceException>(() => api.ExchangeAsync("synthetic", default));
    }

    [TestMethod]
    public async Task Router_PagesAllAuthorizations_AndOnlyDeliversToVerifiedRecipient()
    {
        using var fixture = new Fixture();
        var a = fixture.Pair("U_A"); var b = fixture.Pair("U_B"); var other = fixture.Pair("U_C", "T_OTHER");
        int pages = 0;
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(async request =>
        {
            string method = request.RequestUri!.AbsolutePath;
            if (method.EndsWith("apps.event.authorizations.list"))
            {
                Interlocked.Increment(ref pages);
                Assert.AreEqual("xapp-synthetic", request.Headers.Authorization!.Parameter);
                bool second = (await request.Content!.ReadAsStringAsync()).Contains("cursor=second");
                return Json(second
                    ? """{"ok":true,"authorizations":[{"team_id":"T_TEST","user_id":"U_A","is_bot":false}],"response_metadata":{"next_cursor":""}}"""
                    : """{"ok":true,"authorizations":[{"team_id":"T_TEST","user_id":"U_B","is_bot":false}],"response_metadata":{"next_cursor":"second"}}""");
            }
            if (method.EndsWith("conversations.info")) return Json(request.Headers.Authorization!.Parameter == "xoxp-synthetic-U_A"
                ? """{"ok":true,"channel":{"is_im":true}}""" : """{"ok":false,"error":"channel_not_found"}""");
            return Json("""{"ok":true,"user":{"profile":{"display_name":"Synthetic sender"}}}""");
        })));
        var router = new SlackEventRouter(fixture.Store, api, fixture.Options);
        string payload = Event();
        await router.RouteAsync(new("Ev_1", payload, DateTimeOffset.UtcNow), default);
        await router.RouteAsync(new("Ev_1", payload, DateTimeOffset.UtcNow), default);
        Assert.AreEqual(4, pages);
        var dm = fixture.Store.Deliveries(fixture.Store.Authenticate(a.Token)!).Single().Message;
        Assert.AreEqual("private & synthetic DM", dm.Body);
        Assert.AreEqual(0, fixture.Store.Deliveries(fixture.Store.Authenticate(b.Token)!).Length);
        Assert.AreEqual(0, fixture.Store.Deliveries(fixture.Store.Authenticate(other.Token)!).Length);
    }

    [TestMethod]
    public async Task Router_DoesNotBroadcastWhenNoAuthorizationsOrContextExist()
    {
        using var fixture = new Fixture(); var a = fixture.Pair("U_A");
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(_ => throw new AssertFailedException("No user candidates may trigger an API call."))));
        using var doc = JsonDocument.Parse(Event());
        var payload = doc.RootElement.EnumerateObject().Where(p => p.Name != "event_context").ToDictionary(p => p.Name, p => p.Value);
        await new SlackEventRouter(fixture.Store, api, fixture.Options).RouteAsync(new("Ev_1", JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow), default);
        Assert.AreEqual(0, fixture.Store.Deliveries(fixture.Store.Authenticate(a.Token)!).Length);
    }

    [TestMethod]
    public async Task TokenRefresh_ConcurrentWorkRotatesOnceAndPersistsNewRefreshToken()
    {
        using var fixture = new Fixture();
        var credential = Credential("U_A"); credential.RefreshToken = "synthetic-refresh"; credential.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var session = fixture.Pair(credential); int refreshes = 0;
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(async _ =>
        {
            Interlocked.Increment(ref refreshes); await Task.Delay(20);
            return Json("""{"ok":true,"token_type":"user","access_token":"synthetic-new-access","refresh_token":"synthetic-new-refresh","expires_in":3600}""");
        })));
        var first = api.ForUserAsync(session.UserId, (c, _) => Task.FromResult(c.AccessToken), default);
        var second = api.ForUserAsync(session.UserId, (c, _) => Task.FromResult(c.AccessToken), default);
        await Task.WhenAll(first, second);
        Assert.AreEqual(1, refreshes);
        Assert.AreEqual("synthetic-new-access", await second);
        Assert.AreEqual("synthetic-new-refresh", fixture.Store.Installation(session.UserId)!.Credential.RefreshToken);
    }

    [TestMethod]
    public async Task Disconnect_OnlyLastDeviceRevokesUserGrant_AndReauthorizationWorks()
    {
        using var fixture = new Fixture();
        var first = fixture.Pair("U_A"); var second = fixture.Pair("U_A"); var other = fixture.Pair("U_B"); int revokes = 0;
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(_ =>
        { Interlocked.Increment(ref revokes); return Task.FromResult(Json("""{"ok":true,"revoked":true}""")); })));
        await api.DisconnectAsync(fixture.Store.Authenticate(first.Token)!, default);
        Assert.AreEqual(0, revokes); Assert.IsNotNull(fixture.Store.Authenticate(second.Token));
        await api.DisconnectAsync(fixture.Store.Authenticate(second.Token)!, default);
        Assert.AreEqual(1, revokes); Assert.IsNull(fixture.Store.Installation(first.UserId));
        Assert.IsNotNull(fixture.Store.Authenticate(other.Token));
        var again = fixture.Pair("U_A");
        Assert.AreEqual(first.UserId, again.UserId); Assert.IsNotNull(fixture.Store.Authenticate(again.Token));
        Assert.IsNull(fixture.Store.Authenticate(first.Token));
    }

    [TestMethod]
    public async Task HttpFlow_BeginApproveClaimReceiveAckDisconnect_UsesDeviceProofAndAuthorization()
    {
        using var fixture = new Fixture();
        using var slack = new HttpClient(new Handler(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("oauth.v2.access")
            ? OAuth() : """{"ok":true,"team_id":"T_TEST","user_id":"U_A","team":"Test workspace","user":"Test account"}"""))));
        await using var app = SlackServerApplication.Create(["--urls", "http://127.0.0.1:0"], fixture.Options, slack, startWorkers: false);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        string proof = SlackConnectionProof.NewSecret();
        var begin = await http.PostAsJsonAsync("/v1/slack/connections", new BeginSlackConnection(SlackConnectionProof.Hash(proof)));
        begin.EnsureSuccessStatusCode(); var attempt = (await begin.Content.ReadFromJsonAsync<SlackConnectionAttempt>())!;
        var query = new Uri(attempt.AuthorizationUrl).Query.TrimStart('?').Split('&').Select(v => v.Split('=', 2)).ToDictionary(v => v[0], v => Uri.UnescapeDataString(v[1]));
        Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync("/v1/slack/connections/" + attempt.Id)).StatusCode);
        var callback = await http.GetAsync("/oauth/slack/callback?state=" + query["state"] + "&code=synthetic-code");
        callback.EnsureSuccessStatusCode();
        Assert.IsFalse((await callback.Content.ReadAsStringAsync()).Contains("xoxp"));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await http.GetAsync("/oauth/slack/callback?state=" + query["state"] + "&code=synthetic-code")).StatusCode);
        using var claim = new HttpRequestMessage(HttpMethod.Get, "/v1/slack/connections/" + attempt.Id);
        claim.Headers.Add("X-Altong-Pairing-Proof", proof);
        var status = (await (await http.SendAsync(claim)).Content.ReadFromJsonAsync<SlackPairingStatus>())!;
        Assert.AreEqual("complete", status.Status); var session = status.Session!;
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/slack/messages?userId=" + session.UserId)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        Assert.AreEqual(session.UserId, (await http.GetFromJsonAsync<SlackSessionStatus>("/v1/slack/session"))!.UserId);
        var store = app.Services.GetRequiredService<EncryptedSlackStore>();
        store.Deliver(session.UserId, new("slack:T_TEST:D_TEST:1.000000", "Synthetic", "Synthetic message", DateTime.UnixEpoch), DateTimeOffset.UtcNow);
        var batch = (await http.GetFromJsonAsync<SlackDeliveryBatch>("/v1/slack/messages"))!;
        Assert.AreEqual(1, batch.Messages.Length);
        (await http.PostAsJsonAsync("/v1/slack/messages/ack", new AcknowledgeSlackDeliveries([batch.Messages[0].Id]))).EnsureSuccessStatusCode();
        Assert.AreEqual(0, (await http.GetFromJsonAsync<SlackDeliveryBatch>("/v1/slack/messages"))!.Messages.Length);
        (await http.DeleteAsync("/v1/slack/session")).EnsureSuccessStatusCode();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/slack/session")).StatusCode);
        await app.StopAsync();
    }

    [TestMethod]
    public void Pairing_CancelRequiresProof_AndBlocksLateCallbackCompletion()
    {
        using var fixture = new Fixture();
        string state = SlackConnectionProof.NewSecret(), proof = SlackConnectionProof.NewSecret();
        var attempt = fixture.Store.BeginPairing(state, SlackConnectionProof.Hash(proof));
        fixture.Store.CancelPairing(attempt.Id, SlackConnectionProof.NewSecret());
        Assert.AreEqual(attempt.Id, fixture.Store.ConsumeState(state));
        fixture.Store.CancelPairing(attempt.Id, proof);
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Store.CompletePairing(attempt.Id, Credential("U_A")));
        Assert.AreEqual("cancelled", fixture.Store.PairingStatus(attempt.Id, proof)!.Status);
    }

    [TestMethod]
    public void Queue_DeferredRecipientDoesNotBlockFollowingEvents_AndRetentionPurgesPrivatePayloads()
    {
        var time = new Clock(); using var fixture = new Fixture(time);
        var a = fixture.Pair("U_A"); var device = fixture.Store.Authenticate(a.Token)!;
        fixture.Store.Enqueue("first", "private synthetic event");
        fixture.Store.DeferInbox("first", TimeSpan.FromMinutes(5));
        fixture.Store.Enqueue("second", "another private synthetic event");
        Assert.AreEqual("second", fixture.Store.NextInbox()!.Id);
        fixture.Store.Deliver(a.UserId, new("slack:T:D:1.0", "Synthetic", "private synthetic body", DateTime.UnixEpoch), time.Now);
        Assert.AreEqual(1, fixture.Store.Deliveries(device).Length);
        time.Now += TimeSpan.FromHours(2);
        Assert.IsNull(fixture.Store.NextInbox()); Assert.AreEqual(0, fixture.Store.Deliveries(device).Length);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Revocation_StopsRevokedAccountButPreservesReauthorizedGrantAndOtherUsers(bool currentGrantValid)
    {
        using var fixture = new Fixture(); var a = fixture.Pair("U_A"); var b = fixture.Pair("U_B");
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(_ => Task.FromResult(Json(currentGrantValid
            ? """{"ok":true,"user_id":"U_A","team_id":"T_TEST"}""" : """{"ok":false,"error":"token_revoked"}""")))));
        await new SlackEventRouter(fixture.Store, api, fixture.Options).RouteAsync(new("Ev_revoke",
            """{"type":"event_callback","api_app_id":"A_TEST","team_id":"T_TEST","event":{"type":"tokens_revoked","tokens":{"oauth":["U_A"]}}}""", DateTimeOffset.UtcNow), default);
        Assert.AreEqual(currentGrantValid, fixture.Store.Authenticate(a.Token) is not null);
        Assert.IsNotNull(fixture.Store.Authenticate(b.Token));
    }

    [DataTestMethod]
    [DataRow("channel_type", "mpim")]
    [DataRow("channel_type", "channel")]
    [DataRow("subtype", "message_changed")]
    [DataRow("subtype", "message_deleted")]
    [DataRow("bot_id", "B_SYNTHETIC")]
    [DataRow("text", "")]
    public async Task Router_IgnoresGroupsChannelsBotsAndEditedMessages(string property, string value)
    {
        using var fixture = new Fixture();
        using var api = new SlackWebApi(fixture.Options, fixture.Store, new HttpClient(new Handler(_ => throw new AssertFailedException("Excluded messages must not query user access."))));
        var node = System.Text.Json.Nodes.JsonNode.Parse(Event())!; node["event"]![property] = value;
        await new SlackEventRouter(fixture.Store, api, fixture.Options).RouteAsync(new("Ev_1", node.ToJsonString(), DateTimeOffset.UtcNow), default);
    }

    private static string OAuth(string type = "user", string scope = "im:history,im:read,users:read", string app = "A_TEST") => JsonSerializer.Serialize(new
    { ok = true, app_id = app, access_token = "xoxb-never-use", team = new { id = "T_TEST" },
        authed_user = new { id = "U_A", token_type = type, access_token = "xoxp-synthetic", scope } });
    private static SlackCredential Credential(string user, string team = "T_TEST") => new()
    { Account = new(team, user, "Synthetic account", "Synthetic workspace"), AccessToken = "xoxp-synthetic-" + user, Scopes = SlackWebApi.RequiredScopes };
    private static string Event() => """{"type":"event_callback","api_app_id":"A_TEST","team_id":"T_TEST","event_id":"Ev_1","event_context":"context","event":{"type":"message","channel_type":"im","channel":"D_TEST","user":"U_SENDER","text":"private &amp; synthetic DM","ts":"1794910000.1"}}""";
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
    private sealed class Clock : TimeProvider
    { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AltongSlackTests", Guid.NewGuid().ToString("N"));
        internal SlackServerOptions Options { get; }
        internal EncryptedSlackStore Store { get; }
        internal Fixture(TimeProvider? time = null)
        {
            Options = new() { PublicBaseUrl = new("https://relay.test/"), ClientId = "synthetic-client", ClientSecret = "synthetic-secret", AppId = "A_TEST",
                AppToken = "xapp-synthetic", DataDirectory = Path, EncryptionKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Retention = TimeSpan.FromHours(1) };
            Store = new(Options, time);
        }
        internal SlackDeviceSession Pair(string user, string team = "T_TEST") => Pair(Credential(user, team));
        internal SlackDeviceSession Pair(SlackCredential credential)
        {
            string state = SlackConnectionProof.NewSecret(); var attempt = Store.BeginPairing(state, SlackConnectionProof.Hash(SlackConnectionProof.NewSecret()));
            Store.ConsumeState(state); var session = Store.CompletePairing(attempt.Id, credential);
            Store.Authenticate(session.Token); return session;
        }
        public void Dispose() { Store.Dispose(); if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}

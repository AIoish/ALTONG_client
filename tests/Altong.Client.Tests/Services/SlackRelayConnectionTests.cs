using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Altong.Client.Services.Notifications;
using Altong.Slack.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SlackRelayConnectionTests
{
    private static readonly Uri Server = new("https://relay.test/");

    [TestMethod]
    public async Task OAuth_OpensBrowserClaimsWithProof_AndReceivesThroughPendingPipelineWithoutSlackTokens()
    {
        string path = TempPath(); string? challenge = null; int opens = 0, acks = 0, polls = 0;
        string session = SlackConnectionProof.NewSecret();
        var notification = new TaskCompletionSource<PendingNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            Assert.AreEqual("relay.test", request.RequestUri!.Host);
            Assert.IsFalse(request.RequestUri.Query.Contains(session));
            string route = request.RequestUri.AbsolutePath;
            if (route == "/v1/slack/connections")
            {
                var body = await request.Content!.ReadFromJsonAsync<BeginSlackConnection>(cancellationToken: token);
                challenge = body!.Challenge;
                return Json(new SlackConnectionAttempt("attempt", "https://slack.com/oauth/v2/authorize?client_id=synthetic", DateTimeOffset.UtcNow.AddMinutes(5)));
            }
            if (route == "/v1/slack/connections/attempt")
            {
                string proof = request.Headers.GetValues("X-Altong-Pairing-Proof").Single();
                Assert.AreEqual(challenge, SlackConnectionProof.Hash(proof));
                Assert.IsNull(request.Headers.Authorization);
                return Json(new SlackPairingStatus("complete", new(session, "user-a", "가상 계정", "가상 팀")));
            }
            Assert.AreEqual(session, request.Headers.Authorization!.Parameter);
            if (route == "/v1/slack/session") return Json(new SlackSessionStatus("user-a", "가상 계정", "가상 팀", true));
            if (route == "/v1/slack/messages/ack") { Interlocked.Increment(ref acks); return new(HttpStatusCode.NoContent); }
            if (Interlocked.Increment(ref polls) == 1)
                return Json(new SlackDeliveryBatch(true, [new(SlackConnectionProof.Hash("delivery"), new("slack:T_A:D_A:1.000000", "가상 발신자", "가상 DM", DateTime.UnixEpoch))]));
            return await idle.Task.WaitAsync(token);
        });
        try
        {
            using var controller = new SlackRelayConnectionController(Server, new SlackRelayApiClient(Server, new HttpClient(handler)),
                new(path), uri => { Assert.AreEqual("slack.com", uri.Host); Interlocked.Increment(ref opens); }, (_, token) => Task.Delay(1, token));
            controller.NotificationPending += (_, pending) => notification.TrySetResult(pending);
            Assert.IsFalse(await controller.StartAsync(), "Startup must not open OAuth for a new user.");
            Assert.IsTrue(await controller.ConnectAsync());
            var pending = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("가상 DM", (await pending.PrepareAsync())!.Body);
            await Until(() => acks == 1);
            Assert.AreEqual(1, opens); Assert.AreEqual(SlackConnectionPhase.Connected, controller.Current.Phase);
            Assert.AreEqual("가상 계정", controller.Current.Account);
            Assert.IsFalse(System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains(session));
            Assert.AreEqual(session, new SlackRelaySessionStore(path).Load(Server)!.Token);
            controller.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task SavedSession_RestartsWithoutBrowser_AndRevocationRequiresApprovalAgain()
    {
        string path = TempPath(); var saved = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "user-a", "가상 계정", "가상 팀");
        new SlackRelaySessionStore(path).Save(Server, saved);
        try
        {
            using var api = new SlackRelayApiClient(Server, new HttpClient(new Handler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/v1/slack/session"
                    ? Json(new SlackSessionStatus("user-a", "가상 계정", "가상 팀", true))
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized)))));
            using var controller = new SlackRelayConnectionController(Server, api, new(path), _ => Assert.Fail("Saved-session startup must not open a browser."));
            Assert.IsTrue(await controller.StartAsync());
            await controller.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(SlackConnectionPhase.NeedsAttention, controller.Current.Phase);
            Assert.IsFalse(File.Exists(path));
            StringAssert.Contains(controller.Feedback!, "승인");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Disconnect_RemovesOnlyRelaySession_AndNextClickStartsNewOAuth()
    {
        string path = TempPath(); var session = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "user-a", "가상 계정", "가상 팀");
        var store = new SlackRelaySessionStore(path); store.Save(Server, session);
        int deletes = 0, opens = 0;
        var waiting = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var api = new SlackRelayApiClient(Server, new HttpClient(new Handler((request, token) =>
            {
                string route = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Delete) { Interlocked.Increment(ref deletes); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
                if (route == "/v1/slack/session") return Task.FromResult(Json(new SlackSessionStatus("user-a", "가상 계정", "가상 팀", true)));
                if (route == "/v1/slack/connections") return Task.FromResult(Json(new SlackConnectionAttempt("attempt", "https://slack.com/oauth/v2/authorize", DateTimeOffset.UtcNow.AddMinutes(5))));
                return waiting.Task.WaitAsync(token);
            })));
            using var controller = new SlackRelayConnectionController(Server, api, store, _ => Interlocked.Increment(ref opens));
            await controller.StartAsync();
            await controller.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, deletes); Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(SlackConnectionPhase.NotConnected, controller.Current.Phase);
            Assert.IsTrue(await controller.ConnectAsync());
            await Until(() => opens == 1);
            Assert.AreEqual(SlackConnectionPhase.AwaitingApproval, controller.Current.Phase);
            await controller.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, deletes, "Cancel a pending browser approval on the server too.");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task OAuth_DeniedApprovalAndUnconfiguredServiceNeverPretendConnected()
    {
        using var missing = new SlackRelayConnectionController(null);
        Assert.IsFalse(await missing.ConnectAsync()); StringAssert.Contains(missing.Feedback!, "설정");
        string path = TempPath();
        try
        {
            using var api = new SlackRelayApiClient(Server, new HttpClient(new Handler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/v1/slack/connections"
                    ? Json(new SlackConnectionAttempt("attempt", "https://slack.com/oauth/v2/authorize", DateTimeOffset.UtcNow.AddMinutes(5)))
                    : Json(new SlackPairingStatus("denied"))))));
            using var controller = new SlackRelayConnectionController(Server, api, new(path), _ => { });
            Assert.IsTrue(await controller.ConnectAsync()); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(controller.IsRunning); Assert.AreEqual(SlackConnectionPhase.NeedsAttention, controller.Current.Phase);
            Assert.IsFalse(File.Exists(path)); StringAssert.Contains(controller.Feedback!, "취소");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task FailedDisconnect_RetainsDeviceCredentialForRetry()
    {
        string path = TempPath(); var store = new SlackRelaySessionStore(path);
        var session = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "user-a", "가상 계정", "가상 팀"); store.Save(Server, session);
        try
        {
            using var controller = new SlackRelayConnectionController(Server,
                new SlackRelayApiClient(Server, new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))), store);
            await Assert.ThrowsExceptionAsync<SlackRelayException>(() => controller.DisconnectAsync());
            Assert.AreEqual(session.Token, store.Load(Server)!.Token);
            Assert.AreEqual(SlackConnectionPhase.NeedsAttention, controller.Current.Phase);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void SessionStore_IsBoundToServerOrigin_AndDoesNotExposeDeviceCredential()
    {
        string path = TempPath(); var store = new SlackRelaySessionStore(path);
        var session = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "user-a", "가상 계정", "가상 팀");
        try
        {
            store.Save(Server, session); Assert.AreEqual(session, store.Load(Server));
            Assert.IsNull(store.Load(new Uri("https://different.test/")));
            Assert.IsFalse(session.ToString().Contains(session.Token));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task SavedSession_RetriesTemporaryServerOutageWithoutReopeningApproval()
    {
        string path = TempPath(); var store = new SlackRelaySessionStore(path);
        store.Save(Server, new(SlackConnectionProof.NewSecret(), "user-a", "가상 계정", "가상 팀"));
        int checks = 0;
        var waiting = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var api = new SlackRelayApiClient(Server, new HttpClient(new Handler((request, token) =>
            {
                if (request.RequestUri!.AbsolutePath != "/v1/slack/session") return waiting.Task.WaitAsync(token);
                return Task.FromResult(Interlocked.Increment(ref checks) == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Json(new SlackSessionStatus("user-a", "가상 계정", "가상 팀", true)));
            })));
            using var controller = new SlackRelayConnectionController(Server, api, store,
                _ => Assert.Fail("A temporary outage must not ask the user to grant access again."), (_, token) => Task.Delay(1, token));
            Assert.IsTrue(await controller.StartAsync()); await Until(() => controller.IsRunning);
            Assert.IsTrue(checks >= 2); Assert.IsTrue(File.Exists(path));
            controller.Stop(); await controller.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { File.Delete(path); }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SingleConnection_RestoresSavedAccount_AndDoesNotSwitchToAnotherAfterDisconnect(bool hasPrimary)
    {
        string folder = Directory.CreateTempSubdirectory("altong-slack-single-").FullName;
        try
        {
            string primaryPath = Path.Combine(folder, "device.dat");
            var primary = new SlackRelaySessionStore(primaryPath);
            var firstPrevious = new SlackRelaySessionStore(Path.Combine(primaryPath + ".workspaces", Guid.Empty.ToString("N") + ".dat"));
            var secondPrevious = new SlackRelaySessionStore(Path.Combine(primaryPath + ".workspaces", "00000000000000000000000000000001.dat"));
            var existing = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "primary", "가상 계정", "기존 워크스페이스");
            var first = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "first", "가상 계정", "첫 워크스페이스");
            var second = new SlackDeviceSession(SlackConnectionProof.NewSecret(), "second", "가상 계정", "둘째 워크스페이스");
            if (hasPrimary) primary.Save(Server, existing);
            firstPrevious.Save(Server, first);
            secondPrevious.Save(Server, second);

            var selected = primary.SelectSingleConnection(Server);
            Assert.AreEqual(hasPrimary ? existing : first, selected.Load(Server));
            selected.Clear();
            Assert.IsNull(new SlackRelaySessionStore(primaryPath).SelectSingleConnection(Server).Load(Server));
            Assert.AreEqual(second, secondPrevious.Load(Server));

            selected.Save(Server, existing);
            Assert.AreEqual(existing, new SlackRelaySessionStore(primaryPath).SelectSingleConnection(Server).Load(Server));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}

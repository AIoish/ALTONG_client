using System.IO;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Focus;
using Altong.Client.Services.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class WorkContextClassifierTests
{
    [DataTestMethod]
    [DataRow("Code.exe", WorkCategory.Work)]
    [DataRow("devenv.exe", WorkCategory.Work)]
    [DataRow("Antigravity.exe", WorkCategory.Work)]
    [DataRow("WindowsTerminal.exe", WorkCategory.Work)]
    [DataRow("WINWORD.EXE", WorkCategory.Work)]
    [DataRow("Hwp.exe", WorkCategory.Work)]
    [DataRow("Figma.exe", WorkCategory.Work)]
    [DataRow("KakaoTalk.exe", WorkCategory.Neutral)]
    [DataRow("explorer.exe", WorkCategory.Neutral)]
    [DataRow("Discord.exe", WorkCategory.Neutral)]
    [DataRow("Zoom.exe", WorkCategory.Neutral)]
    [DataRow("Spotify.exe", WorkCategory.Neutral)]
    [DataRow("chrome.exe", WorkCategory.Neutral)]
    [DataRow("msedge.exe", WorkCategory.Neutral)]
    [DataRow("whale.exe", WorkCategory.Neutral)]
    [DataRow("Acrobat.exe", WorkCategory.Neutral)]
    [DataRow("League of Legends.exe", WorkCategory.Leisure)]
    [DataRow("LeagueClientUx.exe", WorkCategory.Leisure)]
    [DataRow("Steam.exe", WorkCategory.Leisure)]
    [DataRow("VALORANT-Win64-Shipping.exe", WorkCategory.Leisure)]
    public void ClassifiesKnownApps(string process, WorkCategory expected) =>
        Assert.AreEqual(expected, WorkContextClassifier.Classify(process));

    [DataTestMethod]
    [DataRow("code")]
    [DataRow("CODE.EXE")]
    [DataRow("  Code.exe ")]
    public void IgnoresCaseExeSuffixAndWhitespace(string process) =>
        Assert.AreEqual(WorkCategory.Work, WorkContextClassifier.Classify(process));

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Unknown.exe")]
    [DataRow("SomeNewApp.exe")]
    public void UnknownOrEmpty_IsNeutral(string? process) =>
        Assert.AreEqual(WorkCategory.Neutral, WorkContextClassifier.Classify(process));
}

[TestClass]
public sealed class AutoDndSettingsStoreTests
{
    private string _directory = null!;
    private string Path_ => Path.Combine(_directory, "auto-dnd-settings.json");

    [TestInitialize]
    public void Setup() => _directory = Directory.CreateTempSubdirectory("altong-autodnd-").FullName;

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [TestMethod]
    public void DefaultsToEnabled_OnFirstRun()
    {
        var store = new AutoDndSettingsStore(Path_);
        Assert.IsTrue(store.Current.Enabled);
        Assert.IsFalse(File.Exists(Path_), "처음 실행에는 파일을 만들지 않는다.");
    }

    [TestMethod]
    public void SaveAndReload_RoundTrips_AndRaisesChanged()
    {
        var store = new AutoDndSettingsStore(Path_);
        int changed = 0;
        store.Changed += (_, _) => changed++;

        store.Save(new AutoDndSettings(Enabled: false));
        Assert.IsFalse(store.Current.Enabled);
        Assert.AreEqual(1, changed);
        Assert.IsFalse(new AutoDndSettingsStore(Path_).Current.Enabled);

        store.Save(new AutoDndSettings(Enabled: false));
        Assert.AreEqual(1, changed, "같은 값이면 알리지 않는다.");
    }

    [TestMethod]
    public void CorruptedFile_FallsBackToEnabled()
    {
        File.WriteAllText(Path_, "{ not json");
        Assert.IsTrue(new AutoDndSettingsStore(Path_).Current.Enabled);
    }
}

[TestClass]
public sealed class AutoDndControllerTests
{
    [TestMethod]
    public void RequestsAndClosesSuggestion_FromTrackerContext()
    {
        var clock = new Clock();
        var tracker = new Tracker("Code.exe");
        var idle = new Idle();
        bool focus = false;
        var controller = new AutoDndController(tracker, idle, new Fullscreen(), () => focus, () => true,
            new AutoDndStateMachine(clock));
        int requested = 0, closed = 0;
        controller.SuggestionRequested += (_, _) => requested++;
        controller.SuggestionClosed += (_, _) => closed++;

        for (int i = 0; i < 91; i++) { clock.Advance(); controller.Tick(); }
        Assert.AreEqual(1, requested);
        Assert.AreEqual(0, closed);

        Assert.IsTrue(controller.Accept());
        Assert.AreEqual(1, closed);
        Assert.IsFalse(controller.Accept(), "같은 제안을 두 번 수락하지 않는다.");
    }

    [TestMethod]
    public void UsesTrackerProcessForClassification()
    {
        var clock = new Clock();
        var tracker = new Tracker("chrome.exe");
        var controller = new AutoDndController(tracker, new Idle(), new Fullscreen(), () => false, () => true,
            new AutoDndStateMachine(clock));
        for (int i = 0; i < 200; i++) { clock.Advance(); controller.Tick(); }
        Assert.AreEqual(AutoDndState.Standby, controller.Machine.State, "브라우저는 1단계에서 Neutral이다.");
    }

    [TestMethod]
    public void SkipsWin32Queries_WhenUnavailableOrInFocus()
    {
        var idle = new Idle(); var fullscreen = new Fullscreen();
        bool available = false, focus = false;
        var controller = new AutoDndController(new Tracker("Code.exe"), idle, fullscreen, () => focus, () => available);
        controller.Tick();
        available = true; focus = true;
        controller.Tick();
        Assert.AreEqual(0, idle.Calls);
        Assert.AreEqual(0, fullscreen.Calls);

        focus = false;
        controller.Tick();
        Assert.AreEqual(1, idle.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now += TimeSpan.FromSeconds(1);
    }

    private sealed class Idle : IUserIdleDetector
    {
        public int Calls { get; private set; }
        public TimeSpan GetIdleTime() { Calls++; return TimeSpan.Zero; }
    }

    private sealed class Fullscreen : IFullscreenDetector
    {
        public int Calls { get; private set; }
        public bool IsFullscreenActive() { Calls++; return false; }
    }

    private sealed class Tracker(string process) : IActiveWindowTracker
    {
        public CurrentContext CurrentContext { get; } = new(process, "제목은 분류에 쓰지 않음", DateTime.UtcNow);
        public event EventHandler<CurrentContext>? ContextChanged { add { } remove { } }
        public event EventHandler<WindowSessionEndedEventArgs>? WindowSessionEnded { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public CurrentContext CaptureNow() => CurrentContext;
        public void Dispose() { }
    }
}

using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Win32;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class ActiveWindowTrackerTests
{
    [TestMethod]
    public void Start_CapturesInitialWindow_Immediately()
    {
        var timeProvider = new TestTimeProvider();
        var windowProvider = new FakeWindowInfoProvider
        {
            Current = new WindowInfo(100, 1234, "Code.exe", "App.tsx - Visual Studio Code")
        };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromSeconds(2),
            stabilizationThreshold: TimeSpan.FromSeconds(5),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        Assert.AreEqual("Code.exe", tracker.CurrentContext.ActiveProcess);
        Assert.AreEqual("App.tsx - Visual Studio Code", tracker.CurrentContext.WindowTitle);
        Assert.AreEqual(1, tracker.CurrentContext.RecentProcesses?.Count);
        Assert.AreEqual("Code.exe", tracker.CurrentContext.RecentProcesses?[0]);
    }

    [TestMethod]
    public void TransientWindow_Under5Seconds_RetainsPreviousStableContext()
    {
        var timeProvider = new TestTimeProvider();
        var vsCodeWindow = new WindowInfo(100, 1234, "Code.exe", "App.tsx - Visual Studio Code");
        var kakaoWindow = new WindowInfo(200, 5678, "KakaoTalk.exe", "카카오톡");

        var windowProvider = new FakeWindowInfoProvider { Current = vsCodeWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromSeconds(2),
            stabilizationThreshold: TimeSpan.FromSeconds(5),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 10초 경과 (VS Code에 10초 머묾)
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        tracker.CaptureNow();

        // 카카오톡으로 전환 후 1초 경과 (포커스 튐 상황 시뮬레이션)
        windowProvider.Current = kakaoWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        CurrentContext contextAt1Sec = tracker.CaptureNow();

        // 카톡에 1초 머물렀을 때 맥락은 여전히 VS Code여야 함!
        Assert.AreEqual("Code.exe", contextAt1Sec.ActiveProcess);
        Assert.AreEqual("App.tsx - Visual Studio Code", contextAt1Sec.WindowTitle);

        // 카카오톡 전환 후 4초 경과 시점 (총 4초 체류)
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        CurrentContext contextAt4Sec = tracker.CaptureNow();

        // 5초 미만이므로 여전히 VS Code여야 함!
        Assert.AreEqual("Code.exe", contextAt4Sec.ActiveProcess);
    }

    [TestMethod]
    public void CandidateWindow_Exceeding5Seconds_TransitionsToNewContext()
    {
        var timeProvider = new TestTimeProvider();
        var vsCodeWindow = new WindowInfo(100, 1234, "Code.exe", "App.tsx - Visual Studio Code");
        var kakaoWindow = new WindowInfo(200, 5678, "KakaoTalk.exe", "카카오톡");

        var windowProvider = new FakeWindowInfoProvider { Current = vsCodeWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromSeconds(2),
            stabilizationThreshold: TimeSpan.FromSeconds(5),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 카카오톡으로 전환
        windowProvider.Current = kakaoWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();

        // 5초 경과 (후보 창 진입 시점 T=1초 기준 5.5초 추가 체류 -> 총 5.5초 경과로 5초 룰 통과)
        timeProvider.Advance(TimeSpan.FromSeconds(5.5));
        CurrentContext contextAfterThreshold = tracker.CaptureNow();

        // 5초 룰 통과: 카카오톡으로 성공적으로 전환!
        Assert.AreEqual("KakaoTalk.exe", contextAfterThreshold.ActiveProcess);
        Assert.AreEqual("카카오톡", contextAfterThreshold.WindowTitle);

        // 최근 프로세스 목록에 KakaoTalk 및 직전의 Code.exe가 포함되어야 함
        CollectionAssert.AreEqual(
            new[] { "KakaoTalk.exe", "Code.exe" },
            contextAfterThreshold.RecentProcesses?.ToArray());
    }

    [TestMethod]
    public void SelfAppWindow_IsIgnored_RetainsPreviousContext()
    {
        var timeProvider = new TestTimeProvider();
        var vsCodeWindow = new WindowInfo(100, 1234, "Code.exe", "App.tsx - Visual Studio Code");
        var altongAppWindow = new WindowInfo(300, 9999, "Altong.Client.exe", "알통 설정");

        var windowProvider = new FakeWindowInfoProvider { Current = vsCodeWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromSeconds(2),
            stabilizationThreshold: TimeSpan.FromSeconds(5),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 사용자가 알통 앱 창(PID 9999)을 클릭함
        windowProvider.Current = altongAppWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        CurrentContext context = tracker.CaptureNow();

        // 알통 자체 창은 무시되고 이전의 VS Code 맥락이 유지되어야 함
        Assert.AreEqual("Code.exe", context.ActiveProcess);
        Assert.AreEqual("App.tsx - Visual Studio Code", context.WindowTitle);
    }

    [TestMethod]
    public void WorkingSet_MaintainsUpTo3_UniqueRecentProcesses()
    {
        var timeProvider = new TestTimeProvider();
        var windowProvider = new FakeWindowInfoProvider();

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromSeconds(2),
            stabilizationThreshold: TimeSpan.FromSeconds(5),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        // 1. VS Code 진입
        windowProvider.Current = new WindowInfo(1, 101, "Code.exe", "Editor");
        tracker.Start();

        // 2. Chrome 진입 후 6초
        windowProvider.Current = new WindowInfo(2, 102, "chrome.exe", "Google");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        tracker.CaptureNow();

        // 3. Slack 진입 후 6초
        windowProvider.Current = new WindowInfo(3, 103, "slack.exe", "Slack Channel");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        tracker.CaptureNow();

        // 4. Notion 진입 후 6초 (총 4개째 -> 최대 3개만 유지되어야 함)
        windowProvider.Current = new WindowInfo(4, 104, "Notion.exe", "Project Notes");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext finalContext = tracker.CaptureNow();

        CollectionAssert.AreEqual(
            new[] { "Notion.exe", "slack.exe", "chrome.exe" },
            finalContext.RecentProcesses?.ToArray());
    }

    private sealed class FakeWindowInfoProvider : IWindowInfoProvider
    {
        public WindowInfo? Current { get; set; }
        public WindowInfo? GetForegroundWindowInfo() => Current;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}

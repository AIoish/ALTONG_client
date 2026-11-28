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
        // 첫 창이라 직전 창이 없다. 현재 창은 최근 창 목록에 넣지 않는다.
        Assert.AreEqual(0, tracker.CurrentContext.RecentWindows?.Count ?? 0);
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

        // 최근 창 목록에는 직전 창(VS Code)만 있고 현재 창(카카오톡)은 없어야 함
        CollectionAssert.AreEqual(
            new[] { "Code.exe|App.tsx - Visual Studio Code" },
            contextAfterThreshold.RecentWindows?.Select(w => $"{w.AppName}|{w.WindowTitle}").ToArray());
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
    public void WorkingSet_MaintainsUpTo3_UniqueRecentWindows()
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
        windowProvider.Current = new WindowInfo(1, 101, "Code.exe", "main.cs");
        tracker.Start();

        // 2. Chrome 진입 후 6초
        windowProvider.Current = new WindowInfo(2, 102, "chrome.exe", "Google Search");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        tracker.CaptureNow();

        // 3. Slack 진입 후 6초
        windowProvider.Current = new WindowInfo(3, 103, "slack.exe", "General");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        tracker.CaptureNow();

        // 4. Notion 진입 후 6초
        windowProvider.Current = new WindowInfo(4, 104, "Notion.exe", "Docs");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        tracker.CaptureNow();

        // 5. Excel 진입 후 6초 (직전 창 4개 -> 최대 3개 유지)
        windowProvider.Current = new WindowInfo(5, 105, "EXCEL.EXE", "Budget");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext finalContext = tracker.CaptureNow();

        // 최근 창은 현재 창(Excel)을 뺀 직전 창들이다 (필터 모델 학습 형식).
        Assert.IsNotNull(finalContext.RecentWindows);
        CollectionAssert.AreEqual(
            new[] { "Notion.exe|Docs", "slack.exe|General", "chrome.exe|Google Search" },
            finalContext.RecentWindows.Select(w => $"{w.AppName}|{w.WindowTitle}").ToArray());
    }

    [TestMethod]
    public void RecentWindows_ReturningToAWindow_DoesNotListItAsRecent()
    {
        var timeProvider = new TestTimeProvider();
        var windowProvider = new FakeWindowInfoProvider();

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromSeconds(2),
            stabilizationThreshold: TimeSpan.FromSeconds(5),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        windowProvider.Current = new WindowInfo(1, 101, "Code.exe", "main.cs");
        tracker.Start();
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        tracker.CaptureNow();

        windowProvider.Current = new WindowInfo(2, 102, "chrome.exe", "Docs");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        tracker.CaptureNow();

        windowProvider.Current = new WindowInfo(1, 101, "Code.exe", "main.cs");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext context = tracker.CaptureNow();

        Assert.AreEqual("Code.exe", context.ActiveProcess);
        CollectionAssert.AreEqual(new[] { "chrome.exe|Docs" },
            context.RecentWindows!.Select(w => $"{w.AppName}|{w.WindowTitle}").ToArray());
    }

    [TestMethod]
    public void EmptyOrWhitespaceWindowTitle_IsIgnored_RetainsPreviousStableContext()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var emptyExplorerWindow = new WindowInfo(200, 5678, "explorer.exe", "");
        var whitespaceExplorerWindow = new WindowInfo(201, 5678, "explorer.exe", "   ");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 초기 작업 맥락 확인
        Assert.AreEqual("Antigravity IDE.exe", tracker.CurrentContext.ActiveProcess);
        Assert.AreEqual("ALTONG_client - Antigravity", tracker.CurrentContext.WindowTitle);

        // 빈 제목 창으로 전환 (바탕화면/작업표시줄 클릭 등) 후 임계값(3초) 초과 체류
        windowProvider.Current = emptyExplorerWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext context = tracker.CaptureNow();

        // 빈 제목 창은 무시되고 기존 작업 맥락 유지
        Assert.AreEqual("Antigravity IDE.exe", context.ActiveProcess);
        Assert.AreEqual("ALTONG_client - Antigravity", context.WindowTitle);
        Assert.AreEqual(5, context.DurationSeconds);

        // 공백 제목 창으로 전환 후 추가 체류
        windowProvider.Current = whitespaceExplorerWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext contextAfterWhitespace = tracker.CaptureNow();

        // 공백 제목 창도 무시되고 기존 작업 맥락 유지
        Assert.AreEqual("Antigravity IDE.exe", contextAfterWhitespace.ActiveProcess);
        Assert.AreEqual("ALTONG_client - Antigravity", contextAfterWhitespace.WindowTitle);
        Assert.AreEqual(10, contextAfterWhitespace.DurationSeconds);
    }

    [TestMethod]
    public void SystemOverlayProcess_SnippingTool_IsIgnored_RetainsPreviousContext()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var snippingToolWindow = new WindowInfo(200, 5678, "SnippingTool.exe", "캡처 및 스케치");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 캡처 도구 활성화 (1.5초 동안 화면 캡처 진행)
        windowProvider.Current = snippingToolWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(1.5));
        CurrentContext contextAt1500Ms = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", contextAt1500Ms.ActiveProcess);

        // 캡처 도구에 5초 이상 오래 머물러도 작업 맥락을 빼앗기지 않아야 함
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext contextAfterLongWait = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", contextAfterLongWait.ActiveProcess);
        Assert.AreEqual("ALTONG_client - Antigravity", contextAfterLongWait.WindowTitle);
    }

    [TestMethod]
    public void SystemOverlayProcess_ScreenClippingHost_IsIgnored_RetainsPreviousContext()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var screenClippingWindow = new WindowInfo(200, 5678, "ScreenClippingHost.exe", "Screen Clipping");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // Win+Shift+S 화면 캡처 오버레이 발동 (2초 경과)
        windowProvider.Current = screenClippingWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        CurrentContext contextDuringClipping = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", contextDuringClipping.ActiveProcess);

        // 캡처 완료 후 원래 창으로 복귀
        windowProvider.Current = ideWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        CurrentContext contextAfterClipping = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", contextAfterClipping.ActiveProcess);
        // 최근 창 목록에 ScreenClippingHost가 오염되지 않아야 함 (같은 창으로 돌아왔으니 직전 창도 없음)
        Assert.AreEqual(0, contextAfterClipping.RecentWindows?.Count ?? 0);
    }

    [TestMethod]
    public void OtherSystemOverlays_ShellExperienceHostAndSearchHost_AreIgnored()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var shellExpWindow = new WindowInfo(200, 5678, "ShellExperienceHost.exe", "Windows Shell Experience");
        var searchHostWindow = new WindowInfo(300, 9101, "SearchHost.exe", "Search");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // ShellExperienceHost 진입
        windowProvider.Current = shellExpWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        CurrentContext context1 = tracker.CaptureNow();
        Assert.AreEqual("Antigravity IDE.exe", context1.ActiveProcess);

        // SearchHost 진입
        windowProvider.Current = searchHostWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        CurrentContext context2 = tracker.CaptureNow();
        Assert.AreEqual("Antigravity IDE.exe", context2.ActiveProcess);
    }

    [TestMethod]
    public void SystemDialogsAndFlyouts_PickerHostAndShellHost_AreIgnored()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 실제 로그에서 현재 창으로 잡혔던 시스템 대화상자·플라이아웃·실행기. 몇 초 떠 있어도 작업 맥락은 그대로여야 한다.
        var systemWindows = new[]
        {
            new WindowInfo(200, 5678, "PickerHost.exe", "배터리 전원이 부족합니다."),
            new WindowInfo(201, 5678, "PickerHost.exe", "Claude의 카메라 액세스를 허용할까요?"),
            new WindowInfo(300, 9101, "ShellHost.exe", "빠른 설정"),
            new WindowInfo(400, 9102, "CredentialUIBroker.exe", "Windows 보안"),
            new WindowInfo(500, 9103, "Microsoft.CmdPal.UI.exe", "명령 도구 모음"),
            new WindowInfo(501, 9103, "Microsoft.CmdPal.UI.exe", "PowerDock"),
            new WindowInfo(600, 9104, "SamsungSettingsHost.exe", "Samsung Settings Quick"),
        };

        foreach (var systemWindow in systemWindows)
        {
            windowProvider.Current = systemWindow;
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            tracker.CaptureNow();
            timeProvider.Advance(TimeSpan.FromSeconds(4));
            CurrentContext context = tracker.CaptureNow();

            Assert.AreEqual("Antigravity IDE.exe", context.ActiveProcess, systemWindow.WindowTitle);
            Assert.AreEqual(0, context.RecentWindows?.Count ?? 0, systemWindow.WindowTitle);
        }
    }

    [TestMethod]
    public void TrayOverflowWindow_IsIgnoredByClassName_ButFileExplorerIsNot()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 트레이 펼침 창은 explorer.exe지만 창 클래스로 거른다
        windowProvider.Current = new WindowInfo(200, 5678, "explorer.exe", "시스템 트레이 오버플로 창입니다.",
            "TopLevelWindowForOverflowXamlIsland");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 같은 explorer.exe라도 파일 탐색기 창은 정상 작업 창
        windowProvider.Current = new WindowInfo(300, 5678, "explorer.exe", "AI서비스 프로젝트 - 파일 탐색기", "CabinetWClass");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(3.5));
        CurrentContext context = tracker.CaptureNow();

        Assert.AreEqual("explorer.exe", context.ActiveProcess);
        Assert.AreEqual("AI서비스 프로젝트 - 파일 탐색기", context.WindowTitle);
    }

    [TestMethod]
    public void LockScreen_IsCurrentWhileLocked_ButIsNotKeptAsRecentWindow()
    {
        var timeProvider = new TestTimeProvider();
        var windowProvider = new FakeWindowInfoProvider { Current = new WindowInfo(1, 101, "Code.exe", "main.cs") };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        windowProvider.Current = new WindowInfo(2, 102, "chrome.exe", "Docs");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(3.5));
        tracker.CaptureNow();

        // 잠겨 있는 동안에는 잠금 화면이 현재 창이다 (자리를 비운 상태를 그대로 보여 준다)
        windowProvider.Current = new WindowInfo(3, 103, "LockApp.exe", "Windows 기본 잠금 화면");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(3.5));
        CurrentContext locked = tracker.CaptureNow();
        Assert.AreEqual("LockApp.exe", locked.ActiveProcess);
        CollectionAssert.AreEqual(new[] { "chrome.exe|Docs", "Code.exe|main.cs" },
            locked.RecentWindows!.Select(w => $"{w.AppName}|{w.WindowTitle}").ToArray());

        // 잠금 해제 뒤에는 최근 창 자리를 차지하지 않는다
        windowProvider.Current = new WindowInfo(1, 101, "Code.exe", "main.cs");
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(3.5));
        CurrentContext unlocked = tracker.CaptureNow();

        Assert.AreEqual("Code.exe", unlocked.ActiveProcess);
        CollectionAssert.AreEqual(new[] { "chrome.exe|Docs" },
            unlocked.RecentWindows!.Select(w => $"{w.AppName}|{w.WindowTitle}").ToArray());
    }

    [TestMethod]
    public void ValidExplorerWindow_WithNonEmptyTitle_IsNotIgnored()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var normalExplorerWindow = new WindowInfo(200, 5678, "explorer.exe", "내 PC");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 정상 파일 탐색기 창으로 전환 (유효한 타이틀 보유)
        windowProvider.Current = normalExplorerWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.CaptureNow();

        // 3초 이상 경과 시 정상적으로 탐색기 맥락으로 전환되어야 함
        timeProvider.Advance(TimeSpan.FromSeconds(3.5));
        CurrentContext context = tracker.CaptureNow();

        Assert.AreEqual("explorer.exe", context.ActiveProcess);
        Assert.AreEqual("내 PC", context.WindowTitle);
    }

    [TestMethod]
    public void DefaultStabilizationThreshold_Is3Seconds_DefendsBrief2SecondSwitch()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var kakaoWindow = new WindowInfo(200, 5678, "KakaoTalk.exe", "카카오톡");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        // 기본 안정화 임계값(DefaultStabilizationThreshold = 3초) 적용 생성자 사용
        using var tracker = new ActiveWindowTracker(
            windowProvider,
            timeProvider: timeProvider,
            selfProcessId: 9999);

        Assert.AreEqual(TimeSpan.FromSeconds(3), ActiveWindowTracker.DefaultStabilizationThreshold);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), ActiveWindowTracker.DefaultPollingInterval);

        tracker.Start();

        // 카카오톡으로 전환 감지 (후보 창 진입)
        windowProvider.Current = kakaoWindow;
        tracker.CaptureNow();

        // 2초 경과 (1~2초 스치는 포커스 튐 시뮬레이션: 2초 < 3초 임계값)
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        CurrentContext contextAt2Sec = tracker.CaptureNow();

        // 3초 임계값에 의해 여전히 기존 작업 맥락(IDE)을 방어해야 함!
        Assert.AreEqual("Antigravity IDE.exe", contextAt2Sec.ActiveProcess);

        // 추가 1.5초 경과 (총 3.5초 체류 -> 3초 룰 통과)
        timeProvider.Advance(TimeSpan.FromSeconds(1.5));
        CurrentContext contextAt35Sec = tracker.CaptureNow();

        // 3초 경과 후 정상 전환!
        Assert.AreEqual("KakaoTalk.exe", contextAt35Sec.ActiveProcess);
        Assert.AreEqual("카카오톡", contextAt35Sec.WindowTitle);
    }

    [TestMethod]
    public void StartupWithOverlayOrEmptyTitleWindow_WaitsForFirstValidWindow()
    {
        var timeProvider = new TestTimeProvider();
        var snippingToolWindow = new WindowInfo(100, 1234, "SnippingTool.exe", "Snipping Tool");
        var ideWindow = new WindowInfo(200, 5678, "Antigravity IDE.exe", "ALTONG_client - Antigravity");

        // 시작 시점이 캡처 도구인 경우
        var windowProvider = new FakeWindowInfoProvider { Current = snippingToolWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 캡처 도구는 무시되므로 맥락은 Empty여야 함
        Assert.AreEqual(string.Empty, tracker.CurrentContext.ActiveProcess);

        // 정상 창 진입 시 즉시 최초 맥락으로 안정화
        windowProvider.Current = ideWindow;
        CurrentContext context = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", context.ActiveProcess);
        Assert.AreEqual("ALTONG_client - Antigravity", context.WindowTitle);
    }

    [TestMethod]
    public void SystemOverlayProcess_ScreenSketch_IsIgnored_RetainsPreviousContext()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var screenSketchWindow = new WindowInfo(200, 5678, "ScreenSketch.exe", "Snip & Sketch");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // ScreenSketch.exe 오버레이 진입 (Windows 10 Snip & Sketch 환경)
        windowProvider.Current = screenSketchWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        CurrentContext context = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", context.ActiveProcess);
        Assert.AreEqual("ALTONG_client - Antigravity", context.WindowTitle);
    }

    [TestMethod]
    public void NullAndZeroHandleWindow_IsIgnored_RetainsPreviousStableContext()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var zeroHandleWindow = new WindowInfo(0, 5678, "unknown.exe", "Valid Title");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 1. null 윈도우 반환 시뮬레이션
        windowProvider.Current = null;
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        CurrentContext contextNull = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", contextNull.ActiveProcess);
        Assert.AreEqual(2, contextNull.DurationSeconds);

        // 2. Handle == nint.Zero 반환 시뮬레이션
        windowProvider.Current = zeroHandleWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        CurrentContext contextZero = tracker.CaptureNow();

        Assert.AreEqual("Antigravity IDE.exe", contextZero.ActiveProcess);
        Assert.AreEqual(4, contextZero.DurationSeconds);
    }

    [TestMethod]
    public void PathAndExtensionVariations_ForIgnoredProcesses_AreAllDefended()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 1. 전체 경로 포함 (C:\Windows\SystemApps\...\ScreenClippingHost.exe)
        windowProvider.Current = new WindowInfo(201, 1001, @"C:\Windows\SystemApps\ScreenClippingHost.exe", "Screen Clipping");
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 2. 확장자 없는 프로세스명 (SnippingTool)
        windowProvider.Current = new WindowInfo(202, 1002, "SnippingTool", "Snipping Tool");
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 3. 앞뒤 공백 포함 프로세스명 (" ScreenClippingHost.exe ")
        windowProvider.Current = new WindowInfo(203, 1003, " ScreenClippingHost.exe ", "Screen Clipping");
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 4. 대소문자 혼합 (sEaRcHhOsT.EXE)
        windowProvider.Current = new WindowInfo(204, 1004, "sEaRcHhOsT.EXE", "Search");
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);
    }

    [TestMethod]
    public void CandidateStabilization_InterruptedByTransientOverlay_ResetsCandidateTimer()
    {
        var timeProvider = new TestTimeProvider();
        var ideWindow = new WindowInfo(100, 1234, "Antigravity IDE.exe", "ALTONG_client - Antigravity");
        var notionWindow = new WindowInfo(200, 5678, "Notion.exe", "Work Notes");
        var screenClippingWindow = new WindowInfo(300, 9991, "ScreenClippingHost.exe", "Screen Clipping");

        var windowProvider = new FakeWindowInfoProvider { Current = ideWindow };

        using var tracker = new ActiveWindowTracker(
            windowProvider,
            pollingInterval: TimeSpan.FromMilliseconds(500),
            stabilizationThreshold: TimeSpan.FromSeconds(3),
            selfProcessId: 9999,
            timeProvider: timeProvider);

        tracker.Start();

        // 1. 노션으로 전환 진입 (2초 체류: 임계값 3초 미만이므로 아직 후보)
        windowProvider.Current = notionWindow;
        tracker.CaptureNow();
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 2. 이 상태에서 사용자가 캡처 도구 단축키 실행 (오버레이 출현)
        windowProvider.Current = screenClippingWindow;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 3. 캡처 종료 후 노션으로 다시 돌아옴
        // 오버레이 개입으로 인해 기존 노션 후보 타이머가 리셋되었으므로, 처음부터 다시 3초를 머물러야 함!
        windowProvider.Current = notionWindow;
        tracker.CaptureNow();

        // 2초 체류 시점 (총 노션 체류는 앞선 2초 + 현재 2초 = 4초이지만, 리셋되었으므로 아직 IDE 유지)
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual("Antigravity IDE.exe", tracker.CaptureNow().ActiveProcess);

        // 추가 1.5초 경과 (리셋 시점 기준 3.5초 경과) -> 노션으로 정상 승격!
        timeProvider.Advance(TimeSpan.FromSeconds(1.5));
        CurrentContext context = tracker.CaptureNow();

        Assert.AreEqual("Notion.exe", context.ActiveProcess);
        Assert.AreEqual("Work Notes", context.WindowTitle);
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

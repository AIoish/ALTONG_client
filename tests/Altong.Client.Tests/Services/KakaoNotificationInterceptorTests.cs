using System.IO;
using Altong.Client.Data.Models;
using Altong.Client.Models;
using Altong.Client.Services;
using Altong.Client.Services.Notifications;
using Altong.Client.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class KakaoNotificationInterceptorTests
{
    private readonly List<string> _tempDirectories = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in _tempDirectories)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private string NewJournalPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "altong-kakao-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(directory);
        return Path.Combine(directory, "kakao-mute-recovery.json");
    }

    // 실제 앱의 복구 기록 파일을 건드리지 않도록 임시 경로를 쓴다.
    private LiveKakaoAudioOperator NewLiveAudio() => new(NewJournalPath());

    private static List<RawNotification> CaptureInFocus(FakeKakaoWindowOperator windows, nint hwnd, bool focused = true)
    {
        windows.NotificationWindows.Add(hwnd);
        using var interceptor = new KakaoNotificationInterceptor(() => focused, windows);
        var received = new List<RawNotification>();
        interceptor.NotificationReceived += (_, notification) => received.Add(notification);
        interceptor.HandleWindowShowEvent(hwnd);
        return received;
    }

    [TestMethod]
    public void FocusCapture_MessageDrawnOverPopupDuringOcr_IsReadAgainBeforeClosing()
    {
        var windows = new FakeKakaoWindowOperator();
        var reads = new Queue<KakaoNotificationText>(
        [
            new("팀원A", "알통 팀방", "첫 메시지") { ContentHash = 1 },
            new("팀원B", "알통 팀방", "덮어 그려진 메시지") { ContentHash = 2 },
        ]);
        windows.TextExtractor = _ => reads.Dequeue();
        windows.ContentHashProvider = _ => 2;

        var received = CaptureInFocus(windows, 71001);

        CollectionAssert.AreEqual(new[] { "첫 메시지", "덮어 그려진 메시지" }, received.Select(n => n.Body).ToArray());
        Assert.AreEqual("팀원B", received[1].Sender);
        Assert.AreNotEqual(received[0].Id, received[1].Id);
        CollectionAssert.AreEqual(new nint[] { 71001 }, windows.ClosedWindows, "다시 읽은 뒤 한 번만 닫습니다.");
    }

    [TestMethod]
    public void FocusCapture_PopupUnchangedAfterOcr_IsReadOnce()
    {
        var windows = new FakeKakaoWindowOperator
        {
            TextExtractor = _ => new("팀원A", "팀원A", "본문") { ContentHash = 7 },
            ContentHashProvider = _ => 7,
        };

        var received = CaptureInFocus(windows, 71002);

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(1, windows.ExtractCallCount);
    }

    [TestMethod]
    public void FocusCapture_PictureChangedButSameBody_DoesNotDuplicate()
    {
        int reads = 0;
        var windows = new FakeKakaoWindowOperator
        {
            // 방 이름 OCR이 흔들려도 본문이 같으면 같은 메시지다.
            TextExtractor = _ => new("팀원A", reads++ == 0 ? "Altong" : "Altond", "본문") { ContentHash = 1 },
            ContentHashProvider = _ => 2,
        };

        var received = CaptureInFocus(windows, 71003);

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(2, windows.ExtractCallCount);
    }

    [TestMethod]
    public void FocusCapture_UnknownContentHash_SkipsTheCheck()
    {
        var windows = new FakeKakaoWindowOperator
        {
            TextExtractor = _ => new("팀원A", "팀원A", "본문"),
            ContentHashProvider = _ => 9,
        };

        var received = CaptureInFocus(windows, 71004);

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(1, windows.ExtractCallCount);
    }

    [TestMethod]
    public void CaptureOutsideFocus_DoesNotReadAgain()
    {
        var windows = new FakeKakaoWindowOperator
        {
            TextExtractor = _ => new("팀원A", "팀원A", "본문") { ContentHash = 1 },
            ContentHashProvider = _ => 2,
        };

        var received = CaptureInFocus(windows, 71005, focused: false);

        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(1, windows.ExtractCallCount);
        Assert.AreEqual(0, windows.ClosedWindows.Count);
    }

    [TestMethod]
    public void MuteJournal_RoundTripsAndDeletesWhenEmpty()
    {
        string path = NewJournalPath();
        Assert.IsFalse(LiveKakaoAudioOperator.HasPendingRecovery(path));

        LiveKakaoAudioOperator.SaveJournal(path, ["장치1|카톡", "장치2|카톡"]);
        Assert.IsTrue(LiveKakaoAudioOperator.HasPendingRecovery(path));
        CollectionAssert.AreEquivalent(new[] { "장치1|카톡", "장치2|카톡" }, LiveKakaoAudioOperator.LoadJournal(path).ToArray());

        LiveKakaoAudioOperator.SaveJournal(path, []);
        Assert.IsFalse(LiveKakaoAudioOperator.HasPendingRecovery(path));
    }

    [TestMethod]
    public void MuteJournal_CorruptFile_LoadsAsEmpty()
    {
        string path = NewJournalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.AreEqual(0, LiveKakaoAudioOperator.LoadJournal(path).Count);
    }

    [TestMethod]
    public void Unmute_SessionNotFound_KeepsRecoveryRecordForNextTime()
    {
        // 카카오톡이 꺼져 있어 되돌릴 세션을 못 찾으면 기록을 지우지 않는다.
        string path = NewJournalPath();
        LiveKakaoAudioOperator.SaveJournal(path, ["없는 장치|없는 세션"]);

        using (var audio = new LiveKakaoAudioOperator(path))
        {
            audio.Unmute();
        }

        CollectionAssert.AreEqual(new[] { "없는 장치|없는 세션" }, LiveKakaoAudioOperator.LoadJournal(path).ToArray());
    }
    [TestMethod]
    public void HandleWindowShowEvent_NotNotificationWindow_Ignored()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        nint regularWindowHwnd = 12345;
        interceptor.HandleWindowShowEvent(regularWindowHwnd);

        Assert.IsNull(received, "알림 창이 아닌 윈도우는 이벤트를 무시해야 합니다.");
        Assert.AreEqual(0, fakeOp.HiddenWindows.Count);
    }

    [TestMethod]
    public void HandleWindowShowEvent_FocusModeOff_DoesNotHideWindow_EmitsNotification()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 99999;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = false;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);

        Assert.IsNotNull(received);
        Assert.AreEqual("KakaoTalk.exe", received.AppName);
        Assert.AreEqual("팀원A", received.Sender);
        Assert.AreEqual("오늘 회의 언제 시작하나요?", received.Body);
        Assert.AreEqual(0, fakeOp.HiddenWindows.Count, "집중 모드가 꺼져 있을 때는 창을 숨기지 않아야 합니다.");
    }

    [TestMethod]
    public void HandleWindowShowEvent_FocusModeOn_StealthHidesImmediately_EmitsNotification()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 88888;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);

        Assert.IsNotNull(received);
        Assert.IsTrue(fakeOp.HiddenWindows.Contains(notiHwnd), "집중 모드 활성화 시 알림 창을 0ms 스텔스 숨김 처리해야 합니다.");
    }

    [TestMethod]
    public void OnNotificationProcessed_PassedNotification_ClosesWindowWithoutRestoring()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 77777;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);
        Assert.IsNotNull(received);

        // AI 판정 결과: 통과(IsPassed = true)
        var record = new NotificationRecord(
            Id: received.Id,
            AppName: received.AppName,
            Sender: received.Sender,
            Title: received.Title,
            Body: received.Body,
            ReceivedAt: received.Timestamp,
            IsPassed: true,
            UrgencyScore: 5,
            RelevanceScore: 5,
            Category: "업무",
            AiSummaryReason: "긴급 업무 알림");

        interceptor.OnNotificationProcessed(record);

        Assert.IsTrue(fakeOp.ClosedWindows.Contains(notiHwnd), "통과 알림도 원래 팝업을 닫고 미니바에서 표시합니다.");
        Assert.AreEqual(0, fakeOp.ShownWindows.Count);
    }

    [TestMethod]
    public void OnNotificationProcessed_BlockedNotification_ClosesWindow()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        nint notiHwnd = 66666;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp);

        RawNotification? received = null;
        interceptor.NotificationReceived += (_, noti) => received = noti;

        interceptor.HandleWindowShowEvent(notiHwnd);
        Assert.IsNotNull(received);

        // AI 판정 결과: 차단(IsPassed = false)
        var record = new NotificationRecord(
            Id: received.Id,
            AppName: received.AppName,
            Sender: received.Sender,
            Title: received.Title,
            Body: received.Body,
            ReceivedAt: received.Timestamp,
            IsPassed: false,
            UrgencyScore: 1,
            RelevanceScore: 1,
            Category: "잡담",
            AiSummaryReason: "단순 잡담 알림");

        interceptor.OnNotificationProcessed(record);

        Assert.IsTrue(fakeOp.ClosedWindows.Contains(notiHwnd), "AI 차단 판정을 받은 알림 창은 조용히 소멸(WM_CLOSE)되어야 합니다.");
        Assert.AreEqual(0, fakeOp.ShownWindows.Count);
    }

    [TestMethod]
    public void HandleWindowShowEvent_FocusModeOn_DoesNotCallAudioOperatorDirectly()
    {
        // 알림 수신마다 오디오 제어를 호출하던 기존 방식에서, 집중 모드 수명주기 전용 작업자 제어로 디커플링됨
        var fakeOp = new FakeKakaoWindowOperator();
        var fakeAudio = new FakeKakaoAudioOperator();
        nint notiHwnd = 55555;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = true;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp, fakeAudio);

        interceptor.HandleWindowShowEvent(notiHwnd);

        Assert.AreEqual(0, fakeAudio.MuteCallCount, "알림 수신 시 오디오 제어를 직접 호출하지 않고 집중 모드 수명주기에 위임해야 합니다.");
    }

    [TestMethod]
    public void ProcessedNotification_OutsideFocus_DoesNotCloseOrRestoreTheNormalPopup()
    {
        var windows = new FakeKakaoWindowOperator();
        var audio = new FakeKakaoAudioOperator();
        windows.NotificationWindows.Add(81234);
        using var interceptor = new KakaoNotificationInterceptor(() => false, windows, audio);
        RawNotification? raw = null;
        interceptor.NotificationReceived += (_, notification) => raw = notification;
        interceptor.HandleWindowShowEvent(81234);
        Assert.IsNotNull(raw);
        interceptor.OnNotificationProcessed(new NotificationRecord(raw.Id, raw.AppName, raw.Sender,
            raw.Title, raw.Body, raw.Timestamp, IsPassed: true));
        Assert.AreEqual(0, windows.HiddenWindows.Count);
        Assert.AreEqual(0, windows.ClosedWindows.Count);
        Assert.AreEqual(0, windows.ShownWindows.Count);
        Assert.AreEqual(0, audio.MuteCallCount);
    }

    [TestMethod]
    public void FocusEndsBeforeVerdict_ClosesOnlyThePopupCapturedDuringFocus()
    {
        bool focused = true;
        var windows = new FakeKakaoWindowOperator();
        windows.NotificationWindows.Add(81235);
        using var interceptor = new KakaoNotificationInterceptor(() => focused, windows);
        RawNotification? raw = null;
        interceptor.NotificationReceived += (_, notification) => raw = notification;
        interceptor.HandleWindowShowEvent(81235);
        focused = false;
        Assert.IsNotNull(raw);
        var record = new NotificationRecord(raw.Id, raw.AppName, raw.Sender, raw.Title, raw.Body,
            raw.Timestamp, IsPassed: true);
        interceptor.OnNotificationProcessed(record);
        interceptor.OnNotificationProcessed(record);
        Assert.AreEqual(1, windows.ClosedWindows.Count, "중복 판정에도 한 번만 닫습니다.");
        Assert.AreEqual(0, windows.ShownWindows.Count);
    }

    [TestMethod]
    public void HandleWindowShowEvent_FocusModeOff_DoesNotCallAudioMute()
    {
        var fakeOp = new FakeKakaoWindowOperator();
        var fakeAudio = new FakeKakaoAudioOperator();
        nint notiHwnd = 44444;
        fakeOp.NotificationWindows.Add(notiHwnd);

        bool isFocusMode = false;
        using var interceptor = new KakaoNotificationInterceptor(() => isFocusMode, fakeOp, fakeAudio);

        interceptor.HandleWindowShowEvent(notiHwnd);

        Assert.IsFalse(fakeAudio.IsMuted, "집중 모드가 아닐 때는 오디오 음소거가 호출되지 않아야 합니다.");
        Assert.AreEqual(0, fakeAudio.MuteCallCount);
    }

    [TestMethod]
    public void FocusModeStateTransition_ControlsKakaoAudio()
    {
        var focusService = new FocusModeService();
        using var audioOp = new FakeKakaoAudioOperator();

        focusService.StateChanged += (_, _) =>
        {
            if (focusService.IsEnabled)
                audioOp.Mute();
            else
                audioOp.Unmute();
        };

        Assert.IsFalse(audioOp.IsMuted);

        focusService.Start();
        Assert.IsTrue(audioOp.IsMuted);
        Assert.AreEqual(1, audioOp.MuteCallCount);

        focusService.Stop();
        Assert.IsFalse(audioOp.IsMuted);
        Assert.AreEqual(1, audioOp.UnmuteCallCount);
    }

    [TestMethod]
    public void LiveKakaoAudioOperator_InitializesWithoutException()
    {
        using var liveAudio = NewLiveAudio();
        Assert.IsFalse(liveAudio.IsMuted);
        liveAudio.Mute();
        Assert.IsTrue(liveAudio.IsMuted);
        liveAudio.Unmute();
        Assert.IsFalse(liveAudio.IsMuted);
    }

    [TestMethod]
    public void LiveKakaoAudioOperator_MultipleMuteCalls_AreIdempotent()
    {
        using var liveAudio = NewLiveAudio();
        liveAudio.Mute();
        liveAudio.Mute();
        liveAudio.Mute();
        Assert.IsTrue(liveAudio.IsMuted, "연속 Mute() 호출에도 IsMuted는 true여야 합니다.");

        liveAudio.Unmute();
        liveAudio.Unmute();
        Assert.IsFalse(liveAudio.IsMuted, "연속 Unmute() 호출에도 IsMuted는 false여야 합니다.");
    }

    [TestMethod]
    public void LiveKakaoAudioOperator_FastOnOffCycles_SucceedWithoutException()
    {
        using var liveAudio = NewLiveAudio();
        for (int i = 0; i < 5; i++)
        {
            liveAudio.Mute();
            Assert.IsTrue(liveAudio.IsMuted);
            liveAudio.Unmute();
            Assert.IsFalse(liveAudio.IsMuted);
        }
    }

    [TestMethod]
    public void LiveKakaoAudioOperator_Dispose_IsIdempotentAndUnmutes()
    {
        var liveAudio = NewLiveAudio();
        liveAudio.Mute();
        Assert.IsTrue(liveAudio.IsMuted);

        liveAudio.Dispose();
        Assert.IsFalse(liveAudio.IsMuted, "Dispose 시 자동으로 음소거가 해제되어야 합니다.");

        // 중복 Dispose 호출 안전성
        liveAudio.Dispose();
        Assert.IsFalse(liveAudio.IsMuted);
    }
}

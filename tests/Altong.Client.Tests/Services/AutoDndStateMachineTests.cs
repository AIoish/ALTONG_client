using Altong.Client.Services.Focus;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// AutoDND 진입 제안 상태 머신을 가상 시간으로 검증한다. 1초에 한 번 판단한다고 가정한다
/// (첫 판단은 0초로 세므로 작업 90초를 채우려면 작업 창에서 91번 판단해야 한다).
/// </summary>
[TestClass]
public sealed class AutoDndStateMachineTests
{
    private static readonly TimeSpan Active = TimeSpan.Zero;

    private ManualClock _clock = null!;
    private AutoDndStateMachine _machine = null!;
    private List<(AutoDndState Previous, AutoDndState Current)> _changes = null!;

    [TestInitialize]
    public void Setup()
    {
        _clock = new ManualClock();
        _machine = new AutoDndStateMachine(_clock);
        _changes = [];
        _machine.StateChanged += (_, change) => _changes.Add(change);
    }

    // ───────────────────────────── 진입 조건 ─────────────────────────────

    [TestMethod]
    public void SuggestsAfter90SecondsOfWork_WithRecentInput()
    {
        Run(90, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.WarmingUp, _machine.State);
        Assert.AreEqual(TimeSpan.FromSeconds(89), _machine.WorkInWindow);

        Run(1, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void StaysInStandby_WhenOnlyNeutralOrUnknownApps()
    {
        Run(300, WorkCategory.Neutral);
        Assert.AreEqual(AutoDndState.Standby, _machine.State);
        Assert.IsFalse(Suggested());
    }

    [TestMethod]
    public void ShortNeutralVisit_KeepsProgress()
    {
        // 작업 60초 → 카톡 20초 → 작업 30초: 연속 90초가 아니어도 최근 120초 안에 작업 90초면 제안한다.
        Run(61, WorkCategory.Work);
        Run(20, WorkCategory.Neutral);
        Assert.AreEqual(AutoDndState.WarmingUp, _machine.State, "Neutral은 진행도를 초기화하지 않는다.");

        Run(29, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.WarmingUp, _machine.State);
        Run(1, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void ShortLeisureVisit_UnderFifteenSeconds_DoesNotBlock()
    {
        Run(61, WorkCategory.Work);
        Run(14, WorkCategory.Leisure);
        Run(30, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void FifteenSecondsOfLeisure_BlocksUntilItLeavesTheWindow()
    {
        Run(61, WorkCategory.Work);
        Run(15, WorkCategory.Leisure);
        Run(60, WorkCategory.Work);
        Assert.IsTrue(_machine.WorkInWindow >= TimeSpan.FromSeconds(90));
        Assert.AreEqual(AutoDndState.WarmingUp, _machine.State, "최근 120초 안에 비작업 15초가 있으면 제안하지 않는다.");
        Assert.IsFalse(Suggested());

        // 비작업 기록이 120초 구간 밖으로 밀려나면 제안한다.
        RunUntil(AutoDndState.Suggesting, max: 60, WorkCategory.Work);
        Assert.IsTrue(_machine.LeisureInWindow < TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public void WorkCountsOnlyWithinTheLast120Seconds()
    {
        Run(200, WorkCategory.Work, idle: TimeSpan.FromSeconds(30)); // 입력이 없어 대기만 한다
        Assert.AreEqual(TimeSpan.FromSeconds(120), _machine.WorkInWindow);
    }

    [TestMethod]
    public void LongTimeGap_IsNotCountedAsWork()
    {
        // 절전·타이머 지연으로 판단 간격이 벌어지면 그 사이를 작업으로 치지 않는다.
        Run(30, WorkCategory.Work);
        _clock.Advance(TimeSpan.FromSeconds(59)); // Update가 1초 더 넘겨 60초 공백
        Update(WorkCategory.Work);
        Assert.AreEqual(TimeSpan.FromSeconds(29), _machine.WorkInWindow);
    }

    // ───────────────────────────── 입력 대기 (인강·PDF) ─────────────────────────────

    [TestMethod]
    public void LectureWithoutInput_WaitsInPendingInput_ThenSuggestsOnFirstInput()
    {
        // 강의 재생 후 손을 뗀 상태: 작업 창이지만 입력이 30초째 없다.
        RunWithGrowingIdle(91, WorkCategory.Work, startIdle: TimeSpan.FromSeconds(20));
        Assert.AreEqual(AutoDndState.PendingInput, _machine.State);
        Assert.IsFalse(Suggested(), "아무도 안 보는 배너를 띄우지 않는다.");

        Update(WorkCategory.Work, idle: TimeSpan.Zero);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void LongAbsence_DoesNotCountAsWork_AndPendingFallsBack()
    {
        // 입력이 180초 이상 없으면 작업 창이 떠 있어도 자리비움이다.
        RunWithGrowingIdle(400, WorkCategory.Work, startIdle: TimeSpan.Zero);
        Assert.AreNotEqual(AutoDndState.PendingInput, _machine.State);
        Assert.AreNotEqual(AutoDndState.Suggesting, _machine.State);
        Assert.IsTrue(_machine.WorkInWindow < TimeSpan.FromSeconds(90));

        // 돌아와 처음 마우스를 움직였다고 바로 제안하지 않는다.
        Update(WorkCategory.Work, idle: TimeSpan.Zero);
        Assert.AreNotEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void Fullscreen_DefersSuggestion_UntilItEnds()
    {
        Run(91, WorkCategory.Work, fullscreen: true);
        Assert.AreEqual(AutoDndState.PendingInput, _machine.State, "발표·전체화면 위로 배너를 띄우지 않는다.");

        Run(30, WorkCategory.Work, fullscreen: true);
        Assert.IsFalse(Suggested());

        Update(WorkCategory.Work, fullscreen: false);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void PendingInput_SwitchingToLeisure_CancelsSuggestion()
    {
        Run(91, WorkCategory.Work, idle: TimeSpan.FromSeconds(30));
        Assert.AreEqual(AutoDndState.PendingInput, _machine.State);

        Run(15, WorkCategory.Leisure, idle: TimeSpan.FromSeconds(30));
        Update(WorkCategory.Leisure, idle: Active);
        Assert.AreNotEqual(AutoDndState.Suggesting, _machine.State, "유튜브로 옮긴 뒤 입력했다고 제안하지 않는다.");
    }

    // ───────────────────────────── 무응답 ─────────────────────────────

    [TestMethod]
    public void NoResponse_ClosesAfter15Seconds_AndDoesNotAskAgainDuringTheSameStreak()
    {
        Run(91, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);

        Run(14, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
        Run(1, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
        Assert.AreEqual(0, _machine.RejectionCount, "무응답은 거절로 세지 않는다.");

        // 같은 작업 흐름이 30분 이어져도 다시 묻지 않는다 (예전 설계의 105초 반복 문제).
        _changes.Clear();
        Run(30 * 60, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
        Assert.IsFalse(Suggested());
    }

    [TestMethod]
    public void Suppression_EndsAfterFiveMinutesWithoutWork()
    {
        IgnoreOneSuggestion();

        Run(299, WorkCategory.Neutral);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
        Run(1, WorkCategory.Neutral);
        Assert.AreNotEqual(AutoDndState.Suppressed, _machine.State);

        Run(91, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State, "새 작업 흐름에서는 다시 제안한다.");
    }

    [TestMethod]
    public void Suppression_EndsAfterFiveMinutesAway()
    {
        IgnoreOneSuggestion();

        Update(WorkCategory.Work, idle: TimeSpan.FromMinutes(5));
        Assert.AreNotEqual(AutoDndState.Suppressed, _machine.State);
    }

    [TestMethod]
    public void BriefLeisure_DoesNotEndSuppression()
    {
        IgnoreOneSuggestion();
        Run(120, WorkCategory.Leisure);
        Run(120, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
    }

    // ───────────────────────────── 거절 ─────────────────────────────

    [TestMethod]
    public void Reject_BacksOff_15_30_60_60Minutes()
    {
        int[] expectedMinutes = [15, 30, 60, 60];
        for (int i = 0; i < expectedMinutes.Length; i++)
        {
            RunUntil(AutoDndState.Suggesting, max: 2 * 60 * 60, WorkCategory.Work);
            var rejectedAt = _clock.GetUtcNow();
            Assert.IsTrue(_machine.Reject());
            Assert.AreEqual(AutoDndState.Cooldown, _machine.State);
            Assert.AreEqual(i + 1, _machine.RejectionCount);
            Assert.AreEqual(rejectedAt + TimeSpan.FromMinutes(expectedMinutes[i]), _machine.CooldownUntil);

            Run(expectedMinutes[i] * 60 - 1, WorkCategory.Work);
            Assert.AreEqual(AutoDndState.Cooldown, _machine.State);
            Run(1, WorkCategory.Work);
            Assert.AreEqual(AutoDndState.Suggesting, _machine.State, "쿨다운이 끝나면 계속 작업 중일 때 다시 제안한다.");
        }
    }

    [TestMethod]
    public void Accept_ResetsRejectionCount()
    {
        RunUntil(AutoDndState.Suggesting, max: 200, WorkCategory.Work);
        _machine.Reject();
        Run(15 * 60, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);

        Assert.IsTrue(_machine.Accept());
        Assert.AreEqual(0, _machine.RejectionCount);
    }

    [TestMethod]
    public void AcceptAndReject_AreIgnoredWhenNotSuggesting()
    {
        Run(10, WorkCategory.Work);
        Assert.IsFalse(_machine.Accept());
        Assert.IsFalse(_machine.Reject());
        Assert.AreEqual(AutoDndState.WarmingUp, _machine.State);
        Assert.AreEqual(0, _machine.RejectionCount);
    }

    // ───────────────────────────── 집중 모드와의 관계 ─────────────────────────────

    [TestMethod]
    public void AfterFocusSession_KeepWorking_DoesNotAskUntilABreak()
    {
        Run(91, WorkCategory.Work);
        Assert.IsTrue(_machine.Accept());
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);

        Run(50 * 60, WorkCategory.Work, focus: true);
        Assert.AreEqual(AutoDndState.InFocus, _machine.State);

        // 집중을 끄고도 작업 창에서 계속 일한다 → 집중 없이 하겠다는 뜻으로 보고 묻지 않는다.
        Run(60 * 60, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
        Assert.IsFalse(SuggestedAfter(AutoDndState.InFocus));

        // 5분 쉬고 돌아와 다시 작업하면 새 작업 흐름이므로 제안한다.
        Run(5 * 60, WorkCategory.Neutral);
        Run(91, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void AfterFocusSession_FifteenMinuteBreakThenResume_SuggestsAgain()
    {
        // 30분 고정 쿨다운이었다면 15분 쉬고 돌아와도 제안하지 않았다.
        Run(25 * 60, WorkCategory.Work, focus: true);
        Run(15 * 60, WorkCategory.Leisure);
        Assert.AreNotEqual(AutoDndState.Suppressed, _machine.State, "충분히 쉬었다.");

        // 유튜브 기록(비작업)이 120초 구간 밖으로 밀려나고 작업 90초가 차면 제안한다.
        RunUntil(AutoDndState.Suggesting, max: 120, WorkCategory.Work);
    }

    [TestMethod]
    public void AfterFocusSession_ShortBreak_StillCountsAsSameFlow()
    {
        Run(25 * 60, WorkCategory.Work, focus: true);
        Run(4 * 60, WorkCategory.Neutral);
        Run(10 * 60, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State, "4분 휴식은 같은 흐름이다.");
    }

    [TestMethod]
    public void ManualFocusWhileSuggesting_ClosesTheSuggestion()
    {
        Run(91, WorkCategory.Work);
        Update(WorkCategory.Work, focus: true);
        Assert.AreEqual(AutoDndState.InFocus, _machine.State);
        Assert.AreEqual((AutoDndState.Suggesting, AutoDndState.InFocus), _changes[^1]);
        Assert.IsFalse(_machine.Accept(), "이미 닫힌 배너의 늦은 클릭은 무시한다.");
    }

    [TestMethod]
    public void ManualFocusSession_EndsInSameFlowSuppression_NotACooldown()
    {
        Run(10, WorkCategory.Work);
        Run(60, WorkCategory.Work, focus: true);
        Update(WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
        Assert.IsNull(_machine.CooldownUntil, "집중 종료는 시간 쿨다운을 걸지 않는다.");
        Assert.AreEqual(0, _machine.RejectionCount);
    }

    // ───────────────────────────── 이탈: 자리비움 ─────────────────────────────

    [TestMethod]
    public void AwayExit_RequestedOnceAfterFifteenMinutesWithoutInput()
    {
        int requests = 0;
        _machine.AwayExitRequested += (_, _) => requests++;

        RunFocusWithIdle(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, requests, "15분 전에는 끄지 않는다.");

        RunFocusWithIdle(TimeSpan.FromMinutes(15));
        Assert.AreEqual(1, requests);

        RunFocusWithIdle(TimeSpan.FromMinutes(40));
        Assert.AreEqual(1, requests, "한 세션에 한 번만 요청한다 (끄기에 실패해도 반복하지 않음).");
    }

    [TestMethod]
    public void AwayExit_ShortAbsences_DoNotEndFocus()
    {
        int requests = 0;
        _machine.AwayExitRequested += (_, _) => requests++;
        for (int round = 0; round < 6; round++)
        {
            RunFocusWithIdle(TimeSpan.FromMinutes(14));
            Update(WorkCategory.Work, idle: TimeSpan.Zero, focus: true); // 돌아와 마우스를 움직임
        }
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public void AwayExit_WaitsUntilTimerBreakEnds()
    {
        int requests = 0;
        _machine.AwayExitRequested += (_, _) => requests++;

        // 집중 타이머의 휴식 시간에 자리를 비우는 건 정상이다.
        Update(WorkCategory.Work, idle: TimeSpan.FromMinutes(20), focus: true, onBreak: true);
        Assert.AreEqual(0, requests);

        // 휴식이 끝났는데도 자리에 없으면 그때 끝낸다.
        Update(WorkCategory.Work, idle: TimeSpan.FromMinutes(20), focus: true, onBreak: false);
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public void AwayExit_ResetsForTheNextSession()
    {
        int requests = 0;
        _machine.AwayExitRequested += (_, _) => requests++;
        RunFocusWithIdle(TimeSpan.FromMinutes(15));
        Update(WorkCategory.Work, idle: TimeSpan.FromMinutes(15)); // 집중 종료
        Update(WorkCategory.Work, focus: true);                    // 새 세션
        RunFocusWithIdle(TimeSpan.FromMinutes(15));
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public void AwayExit_NotWhenUnavailable()
    {
        int requests = 0;
        _machine.AwayExitRequested += (_, _) => requests++;
        Update(WorkCategory.Work, idle: TimeSpan.FromHours(1), focus: true, available: false);
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public void AfterAwayExit_ReturningAndWorking_SuggestsAgain()
    {
        // 자리비움으로 끝났다면 이미 충분히 쉰 것이므로, 돌아와 작업하면 다시 집중을 제안한다.
        RunFocusWithIdle(TimeSpan.FromMinutes(15));
        Update(WorkCategory.Work, idle: TimeSpan.FromMinutes(15));
        Assert.AreNotEqual(AutoDndState.Suppressed, _machine.State);

        Run(91, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    // ───────────────────────────── 꺼짐 ─────────────────────────────

    [TestMethod]
    public void Unavailable_NeverSuggests_AndStartsOverWhenEnabled()
    {
        Run(60, WorkCategory.Work);
        Run(300, WorkCategory.Work, available: false);
        Assert.AreEqual(AutoDndState.Disabled, _machine.State);
        Assert.IsFalse(Suggested());

        Run(90, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.WarmingUp, _machine.State, "다시 켜면 작업 시간을 처음부터 쌓는다.");
        Run(1, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
    }

    [TestMethod]
    public void DisablingWhileSuggesting_ClosesTheSuggestion()
    {
        Run(91, WorkCategory.Work);
        Update(WorkCategory.Work, available: false);
        Assert.AreEqual((AutoDndState.Suggesting, AutoDndState.Disabled), _changes[^1]);
    }

    // ───────────────────────────── 도우미 ─────────────────────────────

    private void IgnoreOneSuggestion()
    {
        Run(91, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suggesting, _machine.State);
        Run(15, WorkCategory.Work);
        Assert.AreEqual(AutoDndState.Suppressed, _machine.State);
    }

    private bool Suggested() => _changes.Any(c => c.Current == AutoDndState.Suggesting);

    private bool SuggestedAfter(AutoDndState state)
    {
        int index = _changes.FindLastIndex(c => c.Current == state);
        return _changes.Skip(index + 1).Any(c => c.Current == AutoDndState.Suggesting);
    }

    private void Update(WorkCategory category, TimeSpan? idle = null, bool fullscreen = false,
        bool focus = false, bool available = true, bool onBreak = false)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        _machine.Update(new AutoDndObservation(category, idle ?? Active, fullscreen, focus, available, onBreak));
    }

    /// <summary>집중 중 입력 없이 <paramref name="idle"/>까지 기다린 상태를 한 번 관찰한다.</summary>
    private void RunFocusWithIdle(TimeSpan idle) => Update(WorkCategory.Work, idle: idle, focus: true);

    private void Run(int seconds, WorkCategory category, TimeSpan? idle = null, bool fullscreen = false,
        bool focus = false, bool available = true)
    {
        for (int i = 0; i < seconds; i++) Update(category, idle, fullscreen, focus, available);
    }

    private void RunWithGrowingIdle(int seconds, WorkCategory category, TimeSpan startIdle)
    {
        for (int i = 0; i < seconds; i++) Update(category, startIdle + TimeSpan.FromSeconds(i));
    }

    private void RunUntil(AutoDndState state, int max, WorkCategory category)
    {
        for (int i = 0; i < max && _machine.State != state; i++) Update(category);
        Assert.AreEqual(state, _machine.State);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}

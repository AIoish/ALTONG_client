using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 집중 모드 동안 카카오톡 프로세스의 오디오 세션을 볼륨 믹서 레벨에서 단일 작업자(1초 주기)로
/// 안전하게 음소거(Mute)하고 집중 모드 종료 시 원래 상태로 복원(Unmute)하는 실제 구현체.
/// </summary>
public sealed class LiveKakaoAudioOperator : IKakaoAudioOperator
{
    private static readonly Guid IidAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;

    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, SessionTrackEntry> _trackedSessions = new();

    private bool _isFocusModeActive;
    private bool _isDisposed;

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    public bool IsMuted
    {
        get
        {
            lock (_stateLock)
            {
                return _isFocusModeActive;
            }
        }
    }

    public LiveKakaoAudioOperator()
    {
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>
    /// 집중 모드 ON: 즉시 1회 탐색/음소거 후 약 1초 간격의 백그라운드 단일 순차 작업자를 시작합니다.
    /// 이미 실행 중이면 추가 작업자 생성 없이 멱등(Idempotent)하게 유지됩니다.
    /// </summary>
    public void Mute()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_isFocusModeActive)
            {
                return;
            }

            _isFocusModeActive = true;
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;
            _workerTask = Task.Run(() => WorkerLoopAsync(token), token);
        }
    }

    /// <summary>
    /// 집중 모드 OFF: 주기적 탐색을 즉시 중단하고 앱이 실제로 음소거한 세션만 원래 상태로 복원합니다.
    /// </summary>
    public void Unmute()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }
        }

        StopWorkerAndRestore();
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        StopWorkerAndRestore();
    }

    private void StopWorkerAndRestore()
    {
        CancellationTokenSource? ctsToCancel;
        Task? taskToWait;

        lock (_stateLock)
        {
            if (!_isFocusModeActive)
            {
                return;
            }

            _isFocusModeActive = false;
            ctsToCancel = _workerCts;
            taskToWait = _workerTask;
            _workerCts = null;
            _workerTask = null;
        }

        // 1. 작업자 즉시 취소 요청 및 안전 대기 (최대 1초, UI 스레드 블로킹 방지)
        ctsToCancel?.Cancel();
        try
        {
            taskToWait?.Wait(1000);
        }
        catch (Exception)
        {
            // 태스크 취소 예외 무시
        }
        finally
        {
            ctsToCancel?.Dispose();
        }

        // 2. 앱이 변경했던 세션을 원래 상태로 복원
        RestoreTrackedSessions();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    #region Worker Loop & Scan

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        // 1. 진입 즉시 1회 탐색 및 음소거
        try
        {
            ScanAndMuteAllEndpoints(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoAudio] 초기 오디오 세션 제어 오류: {ex.Message}");
        }

        // 2. 집중 모드 유지 중 약 1초 간격 직렬 순차 탐색
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                ScanAndMuteAllEndpoints(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[KakaoAudio] 주기적 오디오 세션 제어 오류: {ex.Message}");
            }
        }
    }

    private void ScanAndMuteAllEndpoints(CancellationToken token)
    {
        var targetPids = GetKakaoTalkProcessIds();
        if (targetPids.Count == 0)
        {
            return;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out collection);
            if (hr != 0 || collection == null)
            {
                return;
            }

            hr = collection.GetCount(out int deviceCount);
            if (hr != 0 || deviceCount <= 0)
            {
                return;
            }

            for (int i = 0; i < deviceCount; i++)
            {
                token.ThrowIfCancellationRequested();

                IMMDevice? device = null;
                try
                {
                    hr = collection.Item(i, out device);
                    if (hr != 0 || device == null)
                    {
                        continue;
                    }

                    hr = device.GetId(out string deviceId);
                    if (hr != 0 || string.IsNullOrEmpty(deviceId))
                    {
                        continue;
                    }

                    ScanDeviceSessions(device, deviceId, targetPids, token);
                }
                finally
                {
                    ReleaseCom(ref device);
                }
            }
        }
        finally
        {
            ReleaseCom(ref collection);
            ReleaseCom(ref enumerator);
        }
    }

    private void ScanDeviceSessions(IMMDevice device, string deviceId, HashSet<uint> targetPids, CancellationToken token)
    {
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            var iid = IidAudioSessionManager2;
            int hr = device.Activate(ref iid, 23 /* CLSCTX_ALL */, nint.Zero, out object objMgr);
            if (hr != 0 || objMgr is not IAudioSessionManager2 sessionManager)
            {
                return;
            }
            mgr = sessionManager;

            hr = mgr.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null)
            {
                return;
            }

            hr = sessionEnum.GetCount(out int count);
            if (hr != 0 || count <= 0)
            {
                return;
            }

            for (int s = 0; s < count; s++)
            {
                token.ThrowIfCancellationRequested();

                object? sessionObj = null;
                try
                {
                    hr = sessionEnum.GetSession(s, out sessionObj);
                    if (hr != 0 || sessionObj == null)
                    {
                        continue;
                    }

                    if (sessionObj is not IAudioSessionControl2 ctl2 || sessionObj is not ISimpleAudioVolume vol)
                    {
                        continue;
                    }

                    // 시스템 사운드 세션 제외
                    if (ctl2.IsSystemSoundsSession() == 0)
                    {
                        continue;
                    }

                    hr = ctl2.GetProcessId(out uint pid);
                    if (hr != 0 || pid == 0 || !targetPids.Contains(pid))
                    {
                        continue;
                    }

                    hr = ctl2.GetSessionInstanceIdentifier(out string instanceId);
                    if (hr != 0 || string.IsNullOrEmpty(instanceId))
                    {
                        continue;
                    }

                    string sessionKey = $"{deviceId}::{instanceId}";

                    // 실제 현재 음소거 상태 확인
                    hr = vol.GetMute(out bool currentMute);
                    if (hr != 0)
                    {
                        continue;
                    }

                    // 같은 집중 모드 동안 원래 상태는 최초 1회만 스냅샷 기록
                    var entry = _trackedSessions.GetOrAdd(sessionKey, _ => new SessionTrackEntry
                    {
                        DeviceId = deviceId,
                        SessionInstanceId = instanceId,
                        OriginalMute = currentMute,
                        MutedByApp = false
                    });

                    // 이미 음소거 상태이면 불필요하게 다시 호출하지 않음
                    if (currentMute)
                    {
                        continue;
                    }

                    // 새 세션이거나 음소거가 풀린 세션에만 적용
                    var emptyGuid = Guid.Empty;
                    hr = vol.SetMute(true, ref emptyGuid);
                    if (hr == 0)
                    {
                        entry.MutedByApp = true;
                        AppLogger.Info($"[KakaoAudio] 카카오톡 오디오 세션 음소거 적용 (PID {pid})");
                    }
                    else
                    {
                        AppLogger.Warn($"[KakaoAudio] 음소거 적용 실패 (hr=0x{hr:X8}, PID {pid})");
                    }
                }
                finally
                {
                    ReleaseCom(ref sessionObj);
                }
            }
        }
        finally
        {
            ReleaseCom(ref sessionEnum);
            ReleaseCom(ref mgr);
        }
    }

    #endregion

    #region Session Restoration

    private void RestoreTrackedSessions()
    {
        // 앱이 실제로 음소거했고, 원래는 음소거가 아니었던 세션만 복원
        var entriesToRestore = _trackedSessions.Values
            .Where(e => e.MutedByApp && !e.OriginalMute)
            .ToList();

        if (entriesToRestore.Count == 0)
        {
            _trackedSessions.Clear();
            return;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(0, 1, out collection);
            if (hr != 0 || collection == null)
            {
                return;
            }

            hr = collection.GetCount(out int deviceCount);
            if (hr != 0 || deviceCount <= 0)
            {
                return;
            }

            var restoreLookup = entriesToRestore.ToDictionary(e => $"{e.DeviceId}::{e.SessionInstanceId}");

            for (int i = 0; i < deviceCount; i++)
            {
                IMMDevice? device = null;
                try
                {
                    hr = collection.Item(i, out device);
                    if (hr != 0 || device == null)
                    {
                        continue;
                    }

                    hr = device.GetId(out string deviceId);
                    if (hr != 0 || string.IsNullOrEmpty(deviceId))
                    {
                        continue;
                    }

                    RestoreDeviceSessions(device, deviceId, restoreLookup);
                }
                finally
                {
                    ReleaseCom(ref device);
                }
            }
        }
        finally
        {
            ReleaseCom(ref collection);
            ReleaseCom(ref enumerator);
            _trackedSessions.Clear();
        }
    }

    private void RestoreDeviceSessions(IMMDevice device, string deviceId, Dictionary<string, SessionTrackEntry> restoreLookup)
    {
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            var iid = IidAudioSessionManager2;
            int hr = device.Activate(ref iid, 23, nint.Zero, out object objMgr);
            if (hr != 0 || objMgr is not IAudioSessionManager2 sessionManager)
            {
                return;
            }
            mgr = sessionManager;

            hr = mgr.GetSessionEnumerator(out sessionEnum);
            if (hr != 0 || sessionEnum == null)
            {
                return;
            }

            hr = sessionEnum.GetCount(out int count);
            if (hr != 0 || count <= 0)
            {
                return;
            }

            for (int s = 0; s < count; s++)
            {
                object? sessionObj = null;
                try
                {
                    hr = sessionEnum.GetSession(s, out sessionObj);
                    if (hr != 0 || sessionObj == null)
                    {
                        continue;
                    }

                    if (sessionObj is not IAudioSessionControl2 ctl2 || sessionObj is not ISimpleAudioVolume vol)
                    {
                        continue;
                    }

                    hr = ctl2.GetSessionInstanceIdentifier(out string instanceId);
                    if (hr != 0 || string.IsNullOrEmpty(instanceId))
                    {
                        continue;
                    }

                    string sessionKey = $"{deviceId}::{instanceId}";
                    if (restoreLookup.TryGetValue(sessionKey, out var entry))
                    {
                        var emptyGuid = Guid.Empty;
                        hr = vol.SetMute(false, ref emptyGuid);
                        if (hr == 0)
                        {
                            entry.MutedByApp = false;
                            AppLogger.Info("[KakaoAudio] 카카오톡 오디오 세션 음소거 복원 완료 (Unmute)");
                        }
                        else
                        {
                            AppLogger.Warn($"[KakaoAudio] 음소거 복원 실패 (hr=0x{hr:X8})");
                        }
                    }
                }
                finally
                {
                    ReleaseCom(ref sessionObj);
                }
            }
        }
        finally
        {
            ReleaseCom(ref sessionEnum);
            ReleaseCom(ref mgr);
        }
    }

    #endregion

    #region Helpers & COM Cleanup

    private static HashSet<uint> GetKakaoTalkProcessIds()
    {
        var set = new HashSet<uint>();
        try
        {
            var procs = Process.GetProcessesByName("KakaoTalk");
            foreach (var p in procs)
            {
                try
                {
                    set.Add((uint)p.Id);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // 프로세스 목록 조회 예외 무시
        }
        return set;
    }

    private static void ReleaseCom<T>(ref T? comObj) where T : class
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            try
            {
                Marshal.ReleaseComObject(comObj);
            }
            catch
            {
                // 해제 실패 무시
            }
            comObj = null;
        }
    }

    private sealed class SessionTrackEntry
    {
        public required string DeviceId { get; init; }
        public required string SessionInstanceId { get; init; }
        public bool OriginalMute { get; init; }
        public bool MutedByApp { get; set; }
    }

    #endregion

    #region Native COM Interface Definitions

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(nint pClient);
        int UnregisterEndpointNotificationCallback(nint pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out nint ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int pcDevices);
        int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, out nint SessionControl);
        int GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, out ISimpleAudioVolume AudioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        int RegisterSessionNotification(nint SessionNotification);
        int UnregisterSessionNotification(nint SessionNotification);
        int RegisterDuckNotification(string sessionID, nint duckNotification);
        int UnregisterDuckNotification(nint duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int SessionCount);
        int GetSession(int SessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object Session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int pRetVal);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        int GetGroupingParam(out Guid pRetVal);
        int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        int RegisterAudioSessionNotification(nint NewNotifications);
        int UnregisterAudioSessionNotification(nint NewNotifications);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int GetProcessId(out uint pRetVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float fLevel, ref Guid EventContext);
        int GetMasterVolume(out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    #endregion
}

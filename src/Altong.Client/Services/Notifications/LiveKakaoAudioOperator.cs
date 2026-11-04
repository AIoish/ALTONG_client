using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Altong.Client.Services.NotificationControl;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// 집중 모드 동안 카카오톡 프로세스의 오디오 세션을 볼륨 믹서 레벨에서 단일 작업자(1초 주기)로
/// 음소거(Mute)하고 집중 모드 종료 시 원래 상태로 복원(Unmute)하는 실제 구현체.
/// 알통이 음소거한 세션은 음소거하기 전에 파일에 적어 두므로, 집중 중 비정상 종료돼도 다음 실행의
/// Unmute(집중이 아닐 때 시작 시 호출)나 다음 집중 종료 때 되돌린다.
/// </summary>
public sealed class LiveKakaoAudioOperator : IKakaoAudioOperator
{
    private static readonly Guid IidAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;

    private readonly object _stateLock = new();

    // 음소거 스캔과 복원을 직렬화한다. 복원이 끝난 뒤에 작업자가 다시 음소거하지 않게 한다.
    private readonly object _audioLock = new();
    private readonly string _journalPath;

    // 알통이 음소거했고 아직 되돌리지 않은 세션 ("장치 ID|세션 ID"). 파일 내용과 같다.
    private readonly HashSet<string> _mutedByApp;

    // 이번 집중 동안 처음 본 세션의 원래 음소거 상태. 원래 음소거였던 세션은 되돌리지 않는다.
    private readonly Dictionary<string, bool> _originalMute = new();

    private bool _isFocusModeActive;
    private bool _isDisposed;

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    public static string DefaultJournalPath =>
        Path.Combine(NotificationControlPreferences.DirectoryPath, "kakao-mute-recovery.json");

    public static bool HasPendingRecovery(string? journalPath = null) => File.Exists(journalPath ?? DefaultJournalPath);

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

    public LiveKakaoAudioOperator(string? journalPath = null)
    {
        _journalPath = journalPath ?? DefaultJournalPath;
        _mutedByApp = LoadJournal(_journalPath);
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
            if (_isDisposed || _isFocusModeActive)
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
    /// 집중 모드 OFF: 주기적 탐색을 멈추고 알통이 음소거한 세션을 되돌립니다.
    /// 집중 중이 아니어도 직전 실행이 남긴 음소거가 있으면 되돌립니다.
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
        CancellationTokenSource? cts;
        Task? worker;

        lock (_stateLock)
        {
            _isFocusModeActive = false;
            cts = _workerCts;
            worker = _workerTask;
            _workerCts = null;
            _workerTask = null;
        }

        // 작업자를 기다리지 않는다. 진행 중인 스캔은 _audioLock에서 끝나기를 기다리고,
        // 그다음 스캔은 취소를 보고 음소거하지 않는다.
        if (cts is not null)
        {
            cts.Cancel();
            if (worker is null)
                cts.Dispose();
            else
                worker.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        }

        lock (_audioLock)
        {
            _originalMute.Clear();
            RestoreMutedSessions();
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    #region Worker Loop & Scan

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                lock (_audioLock)
                {
                    token.ThrowIfCancellationRequested();
                    ScanAndMute(token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[KakaoAudio] 오디오 세션 제어 오류: {ex.Message}");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        while (true);
    }

    private void ScanAndMute(CancellationToken token)
    {
        var kakaoPids = GetKakaoTalkProcessIds();
        if (kakaoPids.Count == 0)
        {
            return;
        }

        VisitSessions((key, pid, volume) =>
        {
            if (!kakaoPids.Contains(pid) || volume.GetMute(out bool muted) != 0)
            {
                return;
            }

            if (!_originalMute.TryGetValue(key, out bool originallyMuted))
            {
                // 직전 실행이 음소거한 채 끝났다면 지금의 음소거는 사용자의 원래 상태가 아니다.
                originallyMuted = muted && !_mutedByApp.Contains(key);
                _originalMute[key] = originallyMuted;
            }

            if (muted)
            {
                return;
            }

            // 음소거하기 전에 기록한다. 기록과 음소거 사이에 종료되면 음소거 안 된 세션을 되돌릴 뿐이다.
            if (!originallyMuted && _mutedByApp.Add(key))
            {
                SaveJournal();
            }

            var context = Guid.Empty;
            int hr = volume.SetMute(true, ref context);
            if (hr == 0)
            {
                AppLogger.Info($"[KakaoAudio] 카카오톡 오디오 세션 음소거 적용 (PID {pid})");
            }
            else
            {
                AppLogger.Warn($"[KakaoAudio] 음소거 적용 실패 (hr=0x{hr:X8}, PID {pid})");
            }
        }, token);
    }

    #endregion

    #region Session Restoration

    // _audioLock 안에서 호출한다.
    private void RestoreMutedSessions()
    {
        if (_mutedByApp.Count == 0)
        {
            return;
        }

        var restored = new HashSet<string>();
        try
        {
            VisitSessions((key, _, volume) =>
            {
                if (!_mutedByApp.Contains(key) || volume.GetMute(out bool muted) != 0)
                {
                    return;
                }

                if (muted)
                {
                    var context = Guid.Empty;
                    int hr = volume.SetMute(false, ref context);
                    if (hr != 0)
                    {
                        AppLogger.Warn($"[KakaoAudio] 음소거 복원 실패 (hr=0x{hr:X8})");
                        return;
                    }

                    AppLogger.Info("[KakaoAudio] 카카오톡 오디오 세션 음소거 복원 완료 (Unmute)");
                }

                restored.Add(key);
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[KakaoAudio] 음소거 복원 중 오디오 장치 조회 오류: {ex.Message}");
        }

        // 카카오톡이 꺼져 있어 세션을 못 찾았으면 기록을 남겨 다음 기회에 되돌린다.
        if (restored.Count > 0)
        {
            _mutedByApp.ExceptWith(restored);
            SaveJournal();
        }
    }

    #endregion

    #region Journal

    private sealed record KakaoMuteJournal(List<string> Sessions);

    internal static HashSet<string> LoadJournal(string path)
    {
        try
        {
            var journal = JsonSerializer.Deserialize<KakaoMuteJournal>(File.ReadAllText(path));
            return new HashSet<string>(journal?.Sessions?.Where(s => !string.IsNullOrWhiteSpace(s)) ?? [], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Warn($"[KakaoAudio] 음소거 복구 기록을 읽지 못했습니다: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    internal static void SaveJournal(string path, IReadOnlyCollection<string> sessions)
    {
        if (sessions.Count == 0)
        {
            File.Delete(path);
            return;
        }

        DurableJson.Write(path, new KakaoMuteJournal(sessions.Order(StringComparer.Ordinal).ToList()));
    }

    private void SaveJournal()
    {
        try
        {
            SaveJournal(_journalPath, _mutedByApp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"[KakaoAudio] 음소거 복구 기록을 저장하지 못했습니다: {ex.Message}");
        }
    }

    #endregion

    #region Session Enumeration & COM Cleanup

    // key는 "장치 ID|세션 ID"다. 세션 ID는 실행 파일 경로 기반이라 카카오톡을 다시 켜도 같다.
    private delegate void SessionVisitor(string key, uint pid, ISimpleAudioVolume volume);

    private static void VisitSessions(SessionVisitor visit, CancellationToken token)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out collection) != 0
                || collection == null
                || collection.GetCount(out int deviceCount) != 0)
            {
                return;
            }

            for (int i = 0; i < deviceCount; i++)
            {
                token.ThrowIfCancellationRequested();

                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device == null
                        || device.GetId(out string deviceId) != 0 || string.IsNullOrEmpty(deviceId))
                    {
                        continue;
                    }

                    VisitDeviceSessions(device, deviceId, visit, token);
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

    private static void VisitDeviceSessions(IMMDevice device, string deviceId, SessionVisitor visit, CancellationToken token)
    {
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            var iid = IidAudioSessionManager2;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, nint.Zero, out object activated) != 0
                || activated is not IAudioSessionManager2 sessionManager)
            {
                return;
            }

            manager = sessionManager;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null
                || sessions.GetCount(out int count) != 0)
            {
                return;
            }

            for (int s = 0; s < count; s++)
            {
                token.ThrowIfCancellationRequested();

                object? session = null;
                try
                {
                    if (sessions.GetSession(s, out session) != 0
                        || session is not IAudioSessionControl2 control
                        || session is not ISimpleAudioVolume volume)
                    {
                        continue;
                    }

                    // IsSystemSoundsSession은 시스템 소리 세션이면 S_OK(0)를 반환한다.
                    if (control.IsSystemSoundsSession() == 0
                        || control.GetSessionIdentifier(out string sessionId) != 0 || string.IsNullOrEmpty(sessionId))
                    {
                        continue;
                    }

                    if (control.GetProcessId(out uint pid) != 0)
                    {
                        pid = 0;
                    }

                    visit($"{deviceId}|{sessionId}", pid, volume);
                }
                finally
                {
                    ReleaseCom(ref session);
                }
            }
        }
        finally
        {
            ReleaseCom(ref sessions);
            ReleaseCom(ref manager);
        }
    }

    private static HashSet<uint> GetKakaoTalkProcessIds()
    {
        var set = new HashSet<uint>();
        try
        {
            foreach (var process in Process.GetProcessesByName("KakaoTalk"))
            {
                using (process)
                {
                    set.Add((uint)process.Id);
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

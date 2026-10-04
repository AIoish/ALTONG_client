using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Altong.Client.Services.Notifications;

/// <summary>
/// Windows Core Audio API (WASAPI)를 직접 제어하여
/// 집중 모드 중 카카오톡 프로세스의 오디오 세션을 볼륨 믹서 레벨에서 음소거(Mute) 및 복원(Unmute)하는 실제 구현체.
/// </summary>
public sealed class LiveKakaoAudioOperator : IKakaoAudioOperator
{
    private readonly object _syncLock = new();
    private volatile bool _shouldBeMuted;
    private bool _isDisposed;

    public bool IsMuted
    {
        get
        {
            lock (_syncLock)
            {
                return _shouldBeMuted;
            }
        }
    }

    public LiveKakaoAudioOperator()
    {
        // 프로세스 예기치 않은 종료(Ctrl+C, 비정상 종료 등) 시 카카오톡 음소거가 잔류하지 않도록 안전망 등록
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // 시작 시 혹시 이전 비정상 종료로 카카오톡이 음소거 상태로 남아있다면 안전하게 해제
        try
        {
            SetKakaoTalkMute(false);
        }
        catch { }
    }

    public void Mute()
    {
        SetKakaoTalkMute(true);
    }

    public void Unmute()
    {
        SetKakaoTalkMute(false);
    }

    private void SetKakaoTalkMute(bool mute)
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _shouldBeMuted = mute;

            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int mutedCount = 0;
                uint targetPid = 0;

                // 1. 모든 활성 오디오 출력 장치(헤드폰, 스피커 등)의 카카오톡 세션 일괄 음소거/복원
                int hr = enumerator.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out var colPtr);
                if (hr == 0 && colPtr != nint.Zero)
                {
                    try
                    {
                        var col = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(colPtr);
                        col.GetCount(out int devCount);
                        for (int d = 0; d < devCount; d++)
                        {
                            if (col.Item(d, out var dev) == 0 && dev != null)
                            {
                                try
                                {
                                    if (MuteSessionsOnDevice(dev, mute, out uint pid))
                                    {
                                        mutedCount++;
                                        targetPid = pid;
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(dev);
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(colPtr);
                    }
                }
                else
                {
                    // 보조 경로: 기본 오디오 엔드포인트 제어
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out var defaultDev) == 0 && defaultDev != null)
                    {
                        try
                        {
                            if (MuteSessionsOnDevice(defaultDev, mute, out uint pid))
                            {
                                mutedCount++;
                                targetPid = pid;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(defaultDev);
                        }
                    }
                }

                if (mutedCount > 0)
                {
                    string action = mute ? "음소거" : "음소거 해제";
                    AppLogger.Info($"[KakaoAudio] 카카오톡 오디오 세션 {action} 완료 (PID: {targetPid})");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[KakaoAudio] 카카오톡 오디오 제어 실패: {ex.Message}");
            }
        }
    }

    private bool MuteSessionsOnDevice(IMMDevice dev, bool mute, out uint matchedPid)
    {
        matchedPid = 0;
        var iid = typeof(IAudioSessionManager2).GUID;
        int hr = dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, nint.Zero, out var objMgr);
        if (hr != 0 || objMgr is not IAudioSessionManager2 mgr)
        {
            return false;
        }

        bool anyMuted = false;
        try
        {
            hr = mgr.GetSessionEnumerator(out var sessionEnum);
            if (hr != 0 || sessionEnum == null)
            {
                return false;
            }

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                hr = sessionEnum.GetSession(i, out var ctlPtr);
                if (hr != 0 || ctlPtr == nint.Zero)
                {
                    continue;
                }

                try
                {
                    var ctl = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(ctlPtr);
                    ctl.GetProcessId(out uint pid);
                    if (pid != 0 && IsTargetProcess(pid))
                    {
                        var vol = (ISimpleAudioVolume)Marshal.GetObjectForIUnknown(ctlPtr);
                        var context = Guid.Empty;
                        vol.SetMute(mute, ref context);
                        matchedPid = pid;
                        anyMuted = true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[KakaoAudio] 세션[{i}] 제어 중 예외: {ex.Message}");
                }
                finally
                {
                    Marshal.Release(ctlPtr);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(mgr);
        }

        return anyMuted;
    }

    private static bool IsTargetProcess(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName.Equals("KakaoTalk", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            // 종료 시 반드시 카카오톡 음소거 해제
            if (_shouldBeMuted)
            {
                SetKakaoTalkMute(false);
            }

            _isDisposed = true;
        }
    }

    #region COM Interfaces

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint ppDevices);
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
        int GetSession(int SessionIndex, out nint Session);
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

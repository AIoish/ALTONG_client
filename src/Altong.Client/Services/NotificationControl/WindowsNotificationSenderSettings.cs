using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Altong.Client.Services.NotificationControl;

/// <summary>
/// Build-pinned adapter for the private NotificationController COM ABI.
/// This is not a supported/public Windows API. Refuse writes on an unknown DLL, including after Windows updates.
/// Slot signatures were checked against the matching Microsoft symbols for each validated DLL; unused slots are never invoked.
/// </summary>
public sealed class WindowsNotificationSenderSettings : INotificationSenderSettings
{
    /// <summary>
    /// 검증한 <c>NotificationController.dll</c> SHA256별 앱 설정 인터페이스 ID와 vtable 순번.
    /// factory 3(GetSettingsForAumid)과 앱 설정 5(알림 켜짐)·7/8(배너)·9(알림센터 보관)은 모든 검증 빌드에서 같다.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, NotificationControllerLayout> ValidatedLayouts =
        new Dictionary<string, NotificationControllerLayout>(StringComparer.OrdinalIgnoreCase)
        {
            ["3242604B327A90314F88F044FFA4E3B8B2DD28F7D9E7009AB38793ABFE6FFC2D"] = new(
                "Windows 11 25H2 build 26200.9711", new("9DCA8F00-CB98-434B-9473-5A0691200DC4"),
                SoundFile: 33, SetSoundFile: 34, Title: 37, HasValidAppInfo: 43),
            ["C2C4693C3204BA7FB1D2BA1D78D8E4705CCA76BBA5C7D368D43C7A05CB599F97"] = new(
                "Windows 10 22H2 build 19045.6466", new("266DE187-1DE7-4021-A421-806ABA49A99E"),
                SoundFile: 21, SetSoundFile: 22, Title: 25, HasValidAppInfo: 30),
        };

    private const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private static readonly Guid ClassId = new("D18705BE-FC2F-44C8-AEFF-1CD49AEA8FC1");
    private static readonly Guid FactoryId = new("443B1739-3779-4ABA-953B-9E4FF84DD4B6");
    private readonly NotificationControllerLayout _layout = null!;
    public string? CompatibilityError { get; }

    public WindowsNotificationSenderSettings()
    {
        try
        {
            var dll = Path.Combine(Environment.SystemDirectory, "NotificationController.dll");
            using var file = File.OpenRead(dll);
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (!Environment.Is64BitProcess || !ValidatedLayouts.TryGetValue(hash, out var layout))
                CompatibilityError = "현재 Windows의 알림 제어 모듈은 검증되지 않아 Windows 방해 금지 방식을 사용합니다.";
            else
                _layout = layout;
        }
        catch (Exception ex) { CompatibilityError = $"알림 제어 호환성 확인 실패: {ex.GetType().Name}"; }
    }

    public IReadOnlyList<NotificationSender> Enumerate()
    {
        EnsureSupported();
        using var root = Registry.CurrentUser.OpenSubKey(SettingsPath);
        var rows = new List<NotificationSender>();
        using var batch = BeginBatch();
        foreach (string id in root?.GetSubKeyNames() ?? [])
        {
            try
            {
                rows.Add(WithSender(id, p =>
                {
                    var state = ReadState(p);
                    string name = ReadString(p, _layout.Title);
                    int valid = ReadInt(p, _layout.HasValidAppInfo);
                    return new NotificationSender(id, string.IsNullOrWhiteSpace(name) ? id : name,
                        NotificationSenderPolicy.ExclusionReason(id, valid != 0, state.Enabled, state.ShowInActionCenter));
                }));
            }
            catch (Exception ex)
            {
                rows.Add(new(id, id, $"설정 접근 불가 ({ex.GetType().Name})"));
            }
        }
        return rows.OrderBy(row => !row.IsEligible).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public bool IsRegistered(string id)
    {
        // Absence is distinct from COM, access or compatibility failures during recovery.
        using var key = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + id);
        return key is not null;
    }

    public SenderState Read(string id) => WithSender(id, ReadState);
    public void SetBanner(string id, int value) => WithSender(id, p =>
    {
        Marshal.ThrowExceptionForHR(Method<PutInt>(p, 8)(p, value));
        return true;
    });
    public void SetSound(string id, string value) => WithSender(id, p =>
    {
        Marshal.ThrowExceptionForHR(Method<PutString>(p, _layout.SetSoundFile)(p, value));
        return true;
    });

    public RegistrySetting ReadRegistry(string id, bool sound)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + id);
        string name = sound ? "SoundFile" : "ShowBanner";
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(false, 0, null);
        return new(true, (int)key.GetValueKind(name), JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
    }

    public void RestoreRegistry(string id, bool sound, RegistrySetting value)
    {
        EnsureSupported();
        using var key = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + id, writable: true)
            ?? throw new IOException("알림 발신자 등록이 없어졌습니다.");
        string name = sound ? "SoundFile" : "ShowBanner";
        if (!value.Exists) { key.DeleteValue(name, throwOnMissingValue: false); return; }
        var data = JsonSerializer.Deserialize<JsonElement>(value.Json!);
        var kind = (RegistryValueKind)value.Kind;
        object restored = kind switch
        {
            RegistryValueKind.DWord => data.GetInt32(),
            RegistryValueKind.QWord => data.GetInt64(),
            RegistryValueKind.String or RegistryValueKind.ExpandString => data.GetString()!,
            RegistryValueKind.MultiString => data.Deserialize<string[]>()!,
            RegistryValueKind.Binary or RegistryValueKind.None => data.Deserialize<byte[]>()!,
            _ => throw new InvalidDataException("지원하지 않는 알림 설정 값 형식입니다.")
        };
        key.SetValue(name, restored, kind);
    }

    private SenderState ReadState(IntPtr p) => new(ReadInt(p, 5) != 0, ReadInt(p, 9) != 0,
        ReadInt(p, 7), ReadString(p, _layout.SoundFile));

    /// <summary>
    /// 이 COM 서버는 다른 프로세스(LOCAL_SERVER)라 factory를 만들 때마다 비용이 크다. 일괄 작업(<see cref="BeginBatch"/>) 동안에는
    /// factory 하나를 함께 쓰고, 끝나면 놓는다. 오래 들고 있지 않아 서버가 다시 시작돼도 끊긴 연결이 남지 않는다.
    /// COM 포인터는 만든 스레드에서만 쓰므로 다른 스레드의 호출은 따로 만든다.
    /// </summary>
    private IntPtr _batchFactory;
    private int _batchDepth;
    private int _batchThread;

    public IDisposable BeginBatch()
    {
        int thread = Environment.CurrentManagedThreadId;
        if (_batchDepth > 0 && _batchThread != thread) return NoBatch.Instance;
        _batchThread = thread;
        _batchDepth++;
        return new Batch(this);
    }

    private sealed class Batch(WindowsNotificationSenderSettings owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--owner._batchDepth == 0) ReleaseFactory(ref owner._batchFactory);
        }
    }

    private T WithSender<T>(string id, Func<IntPtr, T> action)
    {
        EnsureSupported();
        bool batched = _batchDepth > 0 && _batchThread == Environment.CurrentManagedThreadId;
        if (!batched)
        {
            IntPtr factory = CreateFactory();
            try { return WithSender(id, factory, action); }
            finally { ReleaseFactory(ref factory); }
        }
        if (_batchFactory == IntPtr.Zero) _batchFactory = CreateFactory();
        try { return WithSender(id, _batchFactory, action); }
        catch (COMException)
        {
            // 함께 쓰던 연결이 끊겼을 수 있다. 새 factory로 한 번만 다시 시도한다.
            ReleaseFactory(ref _batchFactory);
            _batchFactory = CreateFactory();
            return WithSender(id, _batchFactory, action);
        }
    }

    /// <summary>이미 만든 factory로 발신자 하나를 열어 <paramref name="action"/>을 실행한다. factory는 호출자가 놓는다.</summary>
    private T WithSender<T>(string id, IntPtr factory, Func<IntPtr, T> action)
    {
        EnsureSupported();
        // GetSettingsForAumid may create settings. Only ever open an already registered sender.
        using var registered = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + id);
        if (registered is null) throw new InvalidOperationException("등록되지 않은 알림 발신자입니다.");
        IntPtr sender = IntPtr.Zero, typed = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetSender>(factory, 3)(factory, id, out sender));
            var senderIid = _layout.SenderId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sender, in senderIid, out typed));
            return action(typed);
        }
        finally
        {
            if (typed != IntPtr.Zero) Marshal.Release(typed);
            if (sender != IntPtr.Zero) Marshal.Release(sender);
        }
    }

    private static IntPtr CreateFactory()
    {
        var clsid = ClassId;
        var iid = FactoryId;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out IntPtr factory));
        return factory;
    }

    private static void ReleaseFactory(ref IntPtr factory)
    {
        if (factory != IntPtr.Zero) Marshal.Release(factory);
        factory = IntPtr.Zero;
    }

    private void EnsureSupported()
    {
        if (CompatibilityError is { } error) throw new NotSupportedException(error);
    }
    private static T Method<T>(IntPtr p, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), slot * IntPtr.Size));
    private static int ReadInt(IntPtr p, int slot)
    {
        Marshal.ThrowExceptionForHR(Method<GetInt>(p, slot)(p, out int value));
        return value;
    }
    private static string ReadString(IntPtr p, int slot)
    {
        IntPtr value = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetString>(p, slot)(p, out value));
            return Marshal.PtrToStringUni(value) ?? "";
        }
        finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSender(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr sender);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInt(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PutInt(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetString(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutString(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string value);
}

/// <summary>검증한 Windows 빌드 하나의 앱 설정 인터페이스 ID와, 빌드마다 다른 vtable 순번.</summary>
public sealed record NotificationControllerLayout(
    string Build, Guid SenderId, int SoundFile, int SetSoundFile, int Title, int HasValidAppInfo);

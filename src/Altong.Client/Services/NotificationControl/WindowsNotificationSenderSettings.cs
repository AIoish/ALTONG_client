using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Altong.Client.Services.NotificationControl;

/// <summary>
/// Build-pinned adapter for the notebook's private NotificationController COM ABI.
/// This is not a supported/public Windows API. Refuse writes on an unknown DLL, including after Windows updates.
/// Slot signatures were checked against the matching Microsoft symbols; unused slots are never invoked.
/// </summary>
public sealed class WindowsNotificationSenderSettings : INotificationSenderSettings
{
    public const string ValidatedDllHash = "3242604B327A90314F88F044FFA4E3B8B2DD28F7D9E7009AB38793ABFE6FFC2D";
    private const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private static readonly Guid ClassId = new("D18705BE-FC2F-44C8-AEFF-1CD49AEA8FC1");
    private static readonly Guid FactoryId = new("443B1739-3779-4ABA-953B-9E4FF84DD4B6");
    private static readonly Guid SenderId = new("9DCA8F00-CB98-434B-9473-5A0691200DC4");
    public string? CompatibilityError { get; }

    public WindowsNotificationSenderSettings()
    {
        try
        {
            var dll = Path.Combine(Environment.SystemDirectory, "NotificationController.dll");
            using var file = File.OpenRead(dll);
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (!Environment.Is64BitProcess || hash != ValidatedDllHash)
                CompatibilityError = "현재 Windows의 알림 제어 모듈은 검증되지 않았습니다. Windows 방식으로 전환해 주세요.";
        }
        catch (Exception ex) { CompatibilityError = $"알림 제어 호환성 확인 실패: {ex.GetType().Name}"; }
    }

    public IReadOnlyList<NotificationSender> Enumerate()
    {
        EnsureSupported();
        using var root = Registry.CurrentUser.OpenSubKey(SettingsPath);
        var rows = new List<NotificationSender>();
        foreach (string id in root?.GetSubKeyNames() ?? [])
        {
            try
            {
                rows.Add(WithSender(id, p =>
                {
                    var state = ReadState(p);
                    string name = ReadString(p, 37);
                    int valid = ReadInt(p, 43);
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
        Marshal.ThrowExceptionForHR(Method<PutString>(p, 34)(p, value));
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

    private static SenderState ReadState(IntPtr p) => new(ReadInt(p, 5) != 0, ReadInt(p, 9) != 0,
        ReadInt(p, 7), ReadString(p, 33));

    private T WithSender<T>(string id, Func<IntPtr, T> action)
    {
        EnsureSupported();
        // GetSettingsForAumid may create settings. Only ever open an already registered sender.
        using var registered = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + id);
        if (registered is null) throw new InvalidOperationException("등록되지 않은 알림 발신자입니다.");
        IntPtr factory = IntPtr.Zero, sender = IntPtr.Zero, typed = IntPtr.Zero;
        try
        {
            var clsid = ClassId;
            var iid = FactoryId;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out factory));
            Marshal.ThrowExceptionForHR(Method<GetSender>(factory, 3)(factory, id, out sender));
            var senderIid = SenderId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(sender, in senderIid, out typed));
            return action(typed);
        }
        finally
        {
            if (typed != IntPtr.Zero) Marshal.Release(typed);
            if (sender != IntPtr.Zero) Marshal.Release(sender);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
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

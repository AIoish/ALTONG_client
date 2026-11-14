using System.Collections.Frozen;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Altong.Client.Services.NotificationControl;

/// <summary>
/// Select by the exact module content, never a Windows version range or a successful QueryInterface alone.
/// Several OS builds can ship the same DLL. Unknown future DLLs must not inherit a guessed private ABI.
/// </summary>
internal static class NotificationControllerCompatibility
{
    internal static readonly NotificationControllerModule[] Modules = LoadModules();
    internal static readonly FrozenDictionary<string, NotificationControllerLayout> Layouts = CreateLayouts();

    internal static NotificationControllerLayout? Resolve(Stream module, Architecture processArchitecture)
    {
        if (processArchitecture is not (Architecture.X64 or Architecture.Arm64)) return null;
        return Resolve(Convert.ToHexString(SHA256.HashData(module)), processArchitecture);
    }

    internal static NotificationControllerLayout? Resolve(string hash, Architecture processArchitecture) =>
        processArchitecture is Architecture.X64 or Architecture.Arm64 && Layouts.TryGetValue(hash, out var layout)
            ? layout : null;

    private static NotificationControllerModule[] LoadModules()
    {
        using var stream = typeof(NotificationControllerCompatibility).Assembly.GetManifestResourceStream(
            "Altong.Client.NotificationControllerModules.json")
            ?? throw new InvalidDataException("알림 제어 호환성 자료가 없습니다.");
        return JsonSerializer.Deserialize<NotificationControllerModule[]>(stream)
            ?? throw new InvalidDataException("알림 제어 호환성 자료가 비어 있습니다.");
    }

    private static FrozenDictionary<string, NotificationControllerLayout> CreateLayouts()
    {
        var layouts = new Dictionary<string, NotificationControllerLayout>(StringComparer.OrdinalIgnoreCase)
        {
            ["C2C4693C3204BA7FB1D2BA1D78D8E4705CCA76BBA5C7D368D43C7A05CB599F97"] = new(
                "Windows 10 22H2 build 19045.6466", new("266DE187-1DE7-4021-A421-806ABA49A99E"),
                SoundFile: 21, SetSoundFile: 22, Title: 25, HasValidAppInfo: 30),
        };
        foreach (var module in Modules)
        {
            if (module.Sha256.Length != 64 || module.Sha256.Any(c => !Uri.IsHexDigit(c))
                || module.Architecture is not ("x64" or "arm64") || module.WindowsBuilds.Length == 0)
                throw new InvalidDataException("알림 제어 호환성 자료 형식이 올바르지 않습니다.");
            layouts.Add(module.Sha256, new(
                $"Windows 11 {string.Join(", ", module.WindowsBuilds)} ({module.Architecture}, DLL {module.FileVersion})",
                new("9DCA8F00-CB98-434B-9473-5A0691200DC4"),
                SoundFile: 33, SetSoundFile: 34, Title: 37, HasValidAppInfo: 43));
        }
        return layouts.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record NotificationControllerModule(
    string Sha256, string FileVersion, string Architecture, string[] WindowsBuilds, string[] Updates);

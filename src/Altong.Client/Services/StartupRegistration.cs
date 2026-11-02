using Microsoft.Win32;

namespace Altong.Client.Services;

/// <summary>
/// Windows 시작 시 알통을 실행하도록 현재 사용자의 시작 프로그램(<c>HKCU\...\Run</c>)에 등록한다.
/// 관리자 권한이 필요 없고, 레지스트리 값 자체가 설정이다(별도 설정 파일 없음). 기본은 등록하지 않는다.
/// 시작으로 실행될 때는 <see cref="StartupArgument"/>를 붙여 대시보드 없이 트레이에만 뜬다.
/// </summary>
public sealed class StartupRegistration
{
    public const string StartupArgument = "--startup";
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Altong";

    private readonly string _runKeyPath;
    private readonly string _executablePath;

    /// <param name="executablePath">등록할 실행 파일. 생략하면 지금 실행 중인 <c>Altong.Client.exe</c>.</param>
    /// <param name="runKeyPath">HKCU 아래 Run 키 경로. 테스트에서만 바꾼다.</param>
    public StartupRegistration(string? executablePath = null, string? runKeyPath = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        _runKeyPath = runKeyPath ?? DefaultRunKeyPath;
    }

    /// <summary>Run 키에 등록할 명령줄. 경로에 공백·한글이 있어도 되도록 따옴표로 감싼다.</summary>
    public string Command => $"\"{_executablePath}\" {StartupArgument}";

    /// <summary>시작 프로그램에 알통이 등록되어 있는지 (다른 위치의 알통으로 등록된 경우도 포함).</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
            return;
        }
        using var existing = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        existing?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 등록되어 있는데 다른 경로(폴더를 옮겼거나 다른 빌드)를 가리키면 지금 실행 중인 알통으로 고친다.
    /// 등록되어 있지 않으면 아무것도 하지 않는다(사용자가 켠 적 없는 설정을 만들지 않음). 고쳤으면 true.
    /// </summary>
    public bool RepairPathIfRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        if (key?.GetValue(ValueName) is not string current || current == Command) return false;
        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }
}

using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Altong.Client.Tests;

/// <summary>
/// 시작 프로그램 등록을 실제 Run 키가 아닌 테스트 전용 HKCU 키에서 검증한다.
/// </summary>
[TestClass]
public sealed class StartupRegistrationTests
{
    private const string TestRoot = @"Software\AltongTests";
    private string _runKey = null!;

    [TestInitialize]
    public void Setup() => _runKey = $@"{TestRoot}\Run-{Guid.NewGuid():N}";

    [TestCleanup]
    public void Cleanup()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_runKey, throwOnMissingSubKey: false);
        // 다른 테스트가 쓰고 있지 않으면 빈 테스트 루트 키도 지운다.
        using (var root = Registry.CurrentUser.OpenSubKey(TestRoot))
            if (root is null || root.SubKeyCount > 0 || root.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
    }

    [TestMethod]
    public void IsDisabledByDefault()
    {
        var registration = new StartupRegistration(@"C:\Apps\Altong.Client.exe", _runKey);
        Assert.IsFalse(registration.IsEnabled);
    }

    [TestMethod]
    public void Enable_WritesQuotedPathWithStartupArgument_AndDisableRemovesIt()
    {
        const string exe = @"C:\Users\김동준\AI 서비스\Altong.Client.exe";
        var registration = new StartupRegistration(exe, _runKey);

        registration.SetEnabled(true);
        Assert.IsTrue(registration.IsEnabled);
        Assert.AreEqual($"\"{exe}\" --startup", ReadValue(), "한글·공백 경로도 따옴표로 감싼다.");

        registration.SetEnabled(false);
        Assert.IsFalse(registration.IsEnabled);
        Assert.IsNull(ReadValue());

        registration.SetEnabled(false); // 이미 꺼져 있어도 예외 없이 끝난다.
    }

    [TestMethod]
    public void RepairPath_UpdatesMovedInstall_ButNeverRegistersOnItsOwn()
    {
        var current = new StartupRegistration(@"D:\New\Altong.Client.exe", _runKey);
        Assert.IsFalse(current.RepairPathIfRegistered(), "사용자가 켠 적 없으면 등록하지 않는다.");
        Assert.IsNull(ReadValue());

        new StartupRegistration(@"C:\Old\Altong.Client.exe", _runKey).SetEnabled(true);
        Assert.IsTrue(current.RepairPathIfRegistered());
        Assert.AreEqual(current.Command, ReadValue());
        Assert.IsFalse(current.RepairPathIfRegistered(), "이미 맞으면 다시 쓰지 않는다.");
    }

    private string? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue("Altong") as string;
    }
}

using Altong.Client.Services;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class SingleInstanceServiceTests
{
    private static string NewKey() => $"Altong.Tests.{Guid.NewGuid():N}";

    [TestMethod]
    public void SecondaryInstance_CannotAcquireLock_AndCanActivatePrimaryRepeatedly()
    {
        string key = NewKey();
        using var primary = new SingleInstanceService(key);
        Assert.IsTrue(primary.IsPrimary);
        int requests = 0;
        primary.StartListening(() => Interlocked.Increment(ref requests));

        // 다른 스레드에서 검사해 Mutex의 같은 스레드 재진입과 구분한다.
        Task.Run(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var secondary = new SingleInstanceService(key);
                Assert.IsFalse(secondary.IsPrimary);
                Assert.IsTrue(secondary.ActivateExistingAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
            }
        }).GetAwaiter().GetResult();

        Assert.AreEqual(3, requests);
    }

    [TestMethod]
    public void SecondaryInstance_WhenPrimaryIsNotReady_TimesOutWithoutBecomingPrimary()
    {
        string key = NewKey();
        using var primary = new SingleInstanceService(key);
        Task.Run(() =>
        {
            using var secondary = new SingleInstanceService(key);
            Assert.IsFalse(secondary.IsPrimary);
            Assert.IsFalse(secondary.ActivateExistingAsync(TimeSpan.FromMilliseconds(150)).GetAwaiter().GetResult());
            Assert.IsFalse(secondary.IsPrimary);
        }).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void PrimaryExit_ReleasesLockAndPipe_ForNextLaunch()
    {
        string key = NewKey();
        using (var primary = new SingleInstanceService(key))
            primary.StartListening(() => { });

        Task.Run(() =>
        {
            using var next = new SingleInstanceService(key);
            Assert.IsTrue(next.IsPrimary);
            next.StartListening(() => { });
        }).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void CrashedProcess_DoesNotBlockNextLaunch()
    {
        string key = NewKey();
        // 별도 프로세스가 잠금을 보유한 상태에서 비정상 종료되는 상황을 재현한다.
        string script = $"$m = New-Object System.Threading.Mutex($true, 'Local\\{key}'); " +
            "[Console]::WriteLine('ready'); Start-Sleep -Seconds 60";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        try
        {
            Assert.AreEqual("ready", process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
            using (var secondary = new SingleInstanceService(key))
            {
                Assert.IsFalse(secondary.IsPrimary);
                process.Kill();
                Assert.IsTrue(process.WaitForExit(5000));
                using var next = new SingleInstanceService(key);
                Assert.IsTrue(next.IsPrimary);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
    }
}

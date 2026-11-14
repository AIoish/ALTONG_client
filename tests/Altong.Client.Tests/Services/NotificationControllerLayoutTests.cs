using Altong.Client.Services.NotificationControl;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationControllerLayoutTests
{
    [DataTestMethod]
    [DataRow("3B3F227FDF3A9214F06AA5A959FA82F1A966A9A2CDC89398F63CD22C8273AB36", "28000.1575")]
    [DataRow("6AD4A955AF8D0EA5CD0096AA5C544FFF4123E82F23B226FECA3EA555B3E8E594", "28000.1836")]
    [DataRow("2BBFC0CF1E8316BAF97AC2592BA1C79A630A7CF11C014DE789D363F935C9981B", "28000.2525")]
    [DataRow("812956BFB0E512404B8E794D9EC0D309907844E580359E031FAB58E2DF6B2EC9", "28000.2956")]
    [DataRow("6E4670B545B39398E5A806415883B0912EC04F435BB03D1560ED28D1C9009101", "26300.9457")]
    [DataRow("21DFC6F1E63E3615ACA0486B90C02D759A535E0ED3795E2751551B10247057D2", "26300.9550")]
    public void EarlierServicingBuilds_RemainCovered(string hash, string build)
    {
        var module = NotificationControllerCompatibility.Modules.Single(m => m.Sha256 == hash);
        CollectionAssert.Contains(module.WindowsBuilds, build);
        Assert.IsNotNull(NotificationControllerCompatibility.Resolve(hash, Architecture.X64));
    }

    [DataTestMethod]
    [DataRow("4BC537B3C70F87C1BDD1EC8919F38FBBE2077EB283A8CCE7C98280867CE345C6")]
    [DataRow("07B5BBBCC1A0399A4CFC488D17D5C7D3A7782D4EAFDE853A383FAE2CA43A2E27")]
    [DataRow("800DA001B2D438DD692A5AF825E3212EC9A47B0C3C2BAD746061B04E338F653C")]
    [DataRow("7231FAE99137A870FAB9DC9D8EE5866D5ED5BFBA76D87986D524BFCF9169508D")]
    public void LatestWindows11Modules_ResolveWithoutAnOsBuildRange(string hash)
    {
        var baseline = WindowsNotificationSenderSettings.ValidatedLayouts["3242604B327A90314F88F044FFA4E3B8B2DD28F7D9E7009AB38793ABFE6FFC2D"];
        foreach (var architecture in new[] { Architecture.X64, Architecture.Arm64 })
        {
            // Cross-process COM can serve x64 emulation on ARM64. Match the OS module hash, not the caller's ISA.
            var layout = NotificationControllerCompatibility.Resolve(hash.ToLowerInvariant(), architecture);
            Assert.IsNotNull(layout);
            Assert.AreEqual(baseline.SenderId, layout.SenderId);
            Assert.AreEqual((33, 34, 37, 43), (layout.SoundFile, layout.SetSoundFile, layout.Title, layout.HasValidAppInfo));
        }
    }

    [DataTestMethod]
    [DataRow(Architecture.X86)]
    [DataRow(Architecture.Arm)]
    public void KnownModule_DoesNotEnableUnsupportedProcessArchitecture(Architecture architecture)
    {
        Assert.IsNull(NotificationControllerCompatibility.Resolve(
            "4BC537B3C70F87C1BDD1EC8919F38FBBE2077EB283A8CCE7C98280867CE345C6", architecture));
    }

    [TestMethod]
    public void UnknownOrModifiedModule_NeverInheritsTheLatestLayout()
    {
        Assert.IsNull(NotificationControllerCompatibility.Resolve(new string('A', 64), Architecture.X64));
        Assert.IsNull(NotificationControllerCompatibility.Resolve(
            "4BC537B3C70F87C1BDD1EC8919F38FBBE2077EB283A8CCE7C98280867CE345C7", Architecture.X64));
        using var unrelatedModule = new MemoryStream([0x4D, 0x5A, 0x00]);
        Assert.IsNull(NotificationControllerCompatibility.Resolve(unrelatedModule, Architecture.X64));
    }

    [TestMethod]
    public void SameDllCoversSeveralServicingBuilds()
    {
        var module = NotificationControllerCompatibility.Modules.Single(m =>
            m.Sha256.Equals("4BC537B3C70F87C1BDD1EC8919F38FBBE2077EB283A8CCE7C98280867CE345C6", StringComparison.OrdinalIgnoreCase));
        CollectionAssert.Contains(module.WindowsBuilds, "26300.9818");
        CollectionAssert.Contains(module.WindowsBuilds, "26300.9974");
        Assert.IsTrue(WindowsNotificationSenderSettings.ValidatedLayouts is not Dictionary<string, NotificationControllerLayout>,
            "A caller must not be able to add unchecked hashes to the process-wide allowlist.");
    }

    [TestMethod]
    public void ValidatedLayouts_CoverWindows11AndWindows10Builds()
    {
        var win11 = WindowsNotificationSenderSettings.ValidatedLayouts["3242604B327A90314F88F044FFA4E3B8B2DD28F7D9E7009AB38793ABFE6FFC2D"];
        Assert.AreEqual(new Guid("9DCA8F00-CB98-434B-9473-5A0691200DC4"), win11.SenderId);
        Assert.AreEqual((33, 34, 37, 43), (win11.SoundFile, win11.SetSoundFile, win11.Title, win11.HasValidAppInfo));

        // Windows 10 19045 DLL: IAumidNotificationSettings has fewer members, so the later slots move forward.
        var win10 = WindowsNotificationSenderSettings.ValidatedLayouts["C2C4693C3204BA7FB1D2BA1D78D8E4705CCA76BBA5C7D368D43C7A05CB599F97"];
        Assert.AreEqual(new Guid("266DE187-1DE7-4021-A421-806ABA49A99E"), win10.SenderId);
        Assert.AreEqual((21, 22, 25, 30), (win10.SoundFile, win10.SetSoundFile, win10.Title, win10.HasValidAppInfo));
    }

    [TestMethod]
    public void ValidatedLayouts_NeverReuseSharedSlots()
    {
        // 3: factory GetSettingsForAumid, 5: Enabled, 7/8: ShowBanner, 9: ShowInActionCenter are shared by every build.
        int[] shared = [5, 7, 8, 9];
        foreach (var layout in WindowsNotificationSenderSettings.ValidatedLayouts.Values)
        {
            int[] slots = [layout.SoundFile, layout.SetSoundFile, layout.Title, layout.HasValidAppInfo];
            Assert.AreEqual(slots.Length, slots.Distinct().Count(), layout.Build);
            Assert.IsFalse(slots.Intersect(shared).Any(), layout.Build);
            Assert.AreEqual(layout.SoundFile + 1, layout.SetSoundFile, layout.Build);
        }
    }
}

using Altong.Client.Services.NotificationControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class NotificationControllerLayoutTests
{
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

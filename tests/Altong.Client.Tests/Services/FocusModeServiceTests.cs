using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class FocusModeServiceTests
{
    [TestMethod]
    public void NewService_IsDisabled()
    {
        var service = new FocusModeService();

        Assert.IsFalse(service.IsEnabled);
    }

    [TestMethod]
    public void Start_EnablesFocusModeAndRaisesStateChangedOnce()
    {
        var service = new FocusModeService();
        var eventCount = 0;
        service.StateChanged += (_, _) => eventCount++;

        service.Start();
        service.Start();

        Assert.IsTrue(service.IsEnabled);
        Assert.AreEqual(1, eventCount);
    }

    [TestMethod]
    public void Stop_DisablesFocusModeAndRaisesStateChangedOnce()
    {
        var service = new FocusModeService();
        service.Start();
        var eventCount = 0;
        service.StateChanged += (_, _) => eventCount++;

        service.Stop();
        service.Stop();

        Assert.IsFalse(service.IsEnabled);
        Assert.AreEqual(1, eventCount);
    }

    [TestMethod]
    public void Toggle_SwitchesFocusModeState()
    {
        var service = new FocusModeService();

        service.Toggle();
        Assert.IsTrue(service.IsEnabled);

        service.Toggle();
        Assert.IsFalse(service.IsEnabled);
    }
}

using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

[TestClass]
public sealed class DockPositionTests
{
    private string _directory = null!;
    private string SettingsPath => Path.Combine(_directory, "dock-position.json");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "AltongDockTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void FirstRun_UsesDefaultPositionWithoutWriting()
    {
        var store = new DockPositionStore(SettingsPath);

        Assert.IsNull(store.VerticalRatio);
        Assert.IsFalse(File.Exists(SettingsPath));
    }

    [TestMethod]
    public void SavedPosition_RestoresAfterRestart()
    {
        var store = new DockPositionStore(SettingsPath);
        store.Save(0.75);

        Assert.AreEqual(0.75, new DockPositionStore(SettingsPath).VerticalRatio);
    }

    [DataTestMethod]
    [DataRow(-0.1)]
    [DataRow(1.1)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void InvalidRatio_DoesNotReplaceSavedPosition(double ratio)
    {
        var store = new DockPositionStore(SettingsPath);
        store.Save(0.5);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.Save(ratio));
        Assert.AreEqual(0.5, new DockPositionStore(SettingsPath).VerticalRatio);
    }

    [DataTestMethod]
    [DataRow("{broken")]
    [DataRow("{\"VerticalRatio\":2}")]
    [DataRow("null")]
    public void InvalidSavedFile_FallsBackWithoutOverwriting(string content)
    {
        File.WriteAllText(SettingsPath, content);

        Assert.IsNull(new DockPositionStore(SettingsPath).VerticalRatio);
        Assert.AreEqual(content, File.ReadAllText(SettingsPath));
    }

    [TestMethod]
    public void WorkArea_ClampsAndRestoresRelativePosition()
    {
        Assert.AreEqual(100, DockPositioning.ClampTop(50, 100, 800, 136));
        Assert.AreEqual(764, DockPositioning.ClampTop(900, 100, 800, 136));

        var ratio = DockPositioning.ToRatio(432, 100, 800, 136);
        Assert.AreEqual(0.5, ratio, 0.0001);
        Assert.AreEqual(232, DockPositioning.FromRatio(ratio, 0, 600, 136));
    }

    [TestMethod]
    public void SmallWorkArea_KeepsDockAtTop()
    {
        Assert.AreEqual(40, DockPositioning.ClampTop(900, 40, 100, 136));
        Assert.AreEqual(0, DockPositioning.ToRatio(40, 40, 100, 136));
    }

    [TestMethod]
    public void PointerThreshold_DistinguishesClickFromDrag()
    {
        Assert.IsFalse(DockPositioning.IsDrag(2, 3, 4, 4));
        Assert.IsTrue(DockPositioning.IsDrag(4, 0, 4, 4));
        Assert.IsTrue(DockPositioning.IsDrag(0, -5, 4, 4));
    }
}

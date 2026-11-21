using System.IO;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests.Services;

[TestClass]
public sealed class AppLoggerRotationTests
{
    [TestMethod]
    public void Rotate_ShiftsFilesAndDropsOldest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"altong-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "altong.log");
            File.WriteAllText(log, "current");
            File.WriteAllText(Path.Combine(dir, "altong.1.log"), "previous");
            File.WriteAllText(Path.Combine(dir, "altong.2.log"), "oldest");

            AppLogger.Rotate(log, kept: 2);

            Assert.IsFalse(File.Exists(log), "현재 파일은 비워서 새로 쓴다");
            Assert.AreEqual("current", File.ReadAllText(Path.Combine(dir, "altong.1.log")));
            Assert.AreEqual("previous", File.ReadAllText(Path.Combine(dir, "altong.2.log")));
            Assert.AreEqual(2, Directory.GetFiles(dir).Length, "가장 오래된 파일은 지운다");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Rotate_WorksWhenOnlyCurrentFileExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"altong-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "altong.log");
            File.WriteAllText(log, "current");

            AppLogger.Rotate(log, kept: 2);

            Assert.AreEqual("current", File.ReadAllText(Path.Combine(dir, "altong.1.log")));
            Assert.AreEqual(1, Directory.GetFiles(dir).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void TotalLogSize_IsCapped()
    {
        Assert.AreEqual(15L * 1024 * 1024, AppLogger.MaxFileBytes * (AppLogger.KeptFiles + 1));
    }
}

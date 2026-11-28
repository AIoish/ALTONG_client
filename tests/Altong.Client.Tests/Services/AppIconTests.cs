using System.Drawing;
using System.IO;
using Altong.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Altong.Client.Tests;

/// <summary>
/// 앱 아이콘과 트레이 아이콘이 빌드 결과에 들어가는지 확인한다.
/// 원본 그림은 assets/icon/에 있고 tools/generate-assets.ps1로 .ico를 만든다.
/// </summary>
[TestClass]
public sealed class AppIconTests
{
    [TestMethod]
    [DataRow(TrayIconService.IdleIconResource)]
    [DataRow(TrayIconService.FocusIconResource)]
    public void TrayIcon_LoadsAtEveryTrayScale(string resourceName)
    {
        // 화면 배율 100%·125%·150%·200%·250%에서 트레이가 쓰는 크기.
        foreach (int size in new[] { 16, 20, 24, 32, 40 })
        {
            using var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream(resourceName);
            Assert.IsNotNull(stream, resourceName);
            using var icon = new Icon(stream, size, size);
            Assert.AreEqual(size, icon.Width, $"{resourceName}에 {size}px 그림이 없습니다.");
        }

        using var loaded = TrayIconService.LoadTrayIcon(resourceName);
        Assert.IsTrue(loaded.Width >= 16);
    }

    [TestMethod]
    public void FocusTrayIcon_IsGold_WhileIdleTrayIcon_IsMutedGray()
    {
        using var idle = TrayBitmap(TrayIconService.IdleIconResource);
        using var focus = TrayBitmap(TrayIconService.FocusIconResource);

        Assert.IsTrue(CountGoldPixels(focus) > focus.Width * focus.Height / 10, "집중 아이콘은 골드 T 심볼이어야 합니다.");
        Assert.AreEqual(0, CountGoldPixels(idle), "대기 아이콘에는 골드 색상이 없어야 합니다.");
        Assert.IsTrue(CountNeutralGrayPixels(idle) > idle.Width * idle.Height / 10, "대기 아이콘은 무채색 그레이 T 심볼이어야 합니다.");
    }

    [TestMethod]
    public void AppExecutable_HasAltongIcon()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Altong.Client.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("테스트 출력 폴더에 Altong.Client.exe가 없습니다.");
            return;
        }

        using var icon = Icon.ExtractAssociatedIcon(exe);
        Assert.IsNotNull(icon);
        using var bitmap = icon.ToBitmap();

        // Windows 기본 실행 파일 아이콘이 아니라 노란 T가 있어야 한다.
        int yellow = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.A > 200 && c.R > 200 && c.G > 160 && c.B < 120) yellow++;
        }

        Assert.IsTrue(yellow > bitmap.Width * bitmap.Height / 20, $"노란 픽셀 {yellow}개");
    }

    [TestMethod]
    [DataRow("Square150x150Logo.scale-400.png", 600)]
    [DataRow("StoreLogo.scale-400.png", 200)]
    [DataRow("Square44x44Logo.scale-400.png", 176)]
    [DataRow("Square44x44Logo.targetsize-24_altform-unplated.png", 24)]
    [DataRow("Square44x44Logo.targetsize-48_altform-unplated.png", 48)]
    public void PackageLogo_IsCopiedNextToExecutable_AtItsSize(string fileName, int size)
    {
        // sparse 패키지는 로고를 실행 파일 폴더의 Assets에서 찾는다. 없으면 작업 표시줄에 회색 빈 아이콘이 뜬다.
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

        Assert.IsTrue(File.Exists(path), path);
        using var logo = new Bitmap(path);
        Assert.AreEqual(size, logo.Width);
        Assert.AreEqual(size, logo.Height);
    }

    [TestMethod]
    public void PackageResourceIndex_IsCopiedNextToExecutable()
    {
        // 배율·크기별 로고 목록. 없으면 Windows가 작은 그림을 늘려 써서 시작 메뉴·작업 표시줄 아이콘이 흐려진다.
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "resources.pri")));
    }

    private static Bitmap TrayBitmap(string resourceName)
    {
        using var stream = typeof(TrayIconService).Assembly.GetManifestResourceStream(resourceName)!;
        using var icon = new Icon(stream, 32, 32);
        return icon.ToBitmap();
    }

    private static int CountGoldPixels(Bitmap bitmap)
    {
        int gold = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.A > 200 && c.R > 200 && c.G > 160 && c.B < 120) gold++;
        }

        return gold;
    }

    private static int CountNeutralGrayPixels(Bitmap bitmap)
    {
        int gray = 0;
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.A > 200 && Math.Abs(c.R - c.B) < 35 && c.R is >= 100 and <= 200) gray++;
        }

        return gray;
    }
}

# 앱 아이콘 원본(assets/icon/*.png, 1024x1024)으로 앱·트레이 아이콘과 sparse 패키지 로고를 만든다.
#
#   powershell -ExecutionPolicy Bypass -File tools/generate-assets.ps1
#
# 32px 이하에서는 메인 아이콘의 종·말풍선·편지가 점처럼 뭉개져서, 그 크기는 기호 없는 골드 T(트레이 집중 아이콘)로 만든다.
# 결과물은 저장소에 커밋되어 있으므로 원본 그림을 바꿨을 때만 다시 실행하면 된다.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class AltongIconWriter
{
    // 큰 원본을 한 번에 줄이면 가는 선이 깨지므로 절반씩 줄여 가다가 마지막에 목표 크기로 맞춘다.
    public static Bitmap Resize(Bitmap source, int size)
    {
        Bitmap current = new Bitmap(source);
        while (current.Width / 2 >= size * 2)
        {
            current = Draw(current, current.Width / 2, true);
        }
        return Draw(current, size, true);
    }

    private static Bitmap Draw(Bitmap source, int size, bool disposeSource)
    {
        Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(result))
        using (ImageAttributes attributes = new ImageAttributes())
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        if (disposeSource) source.Dispose();
        return result;
    }

    public static void SavePng(string sourcePath, int size, string targetPath)
    {
        SavePng(sourcePath, size, targetPath, false);
    }

    /// <summary>trimMargin이면 그림 둘레의 투명한 여백을 잘라 내고 줄여, 같은 크기 안에서 아이콘이 더 크게 보이게 한다.</summary>
    public static void SavePng(string sourcePath, int size, string targetPath, bool trimMargin)
    {
        using (Bitmap loaded = new Bitmap(sourcePath))
        using (Bitmap source = trimMargin ? TrimTransparentMargin(loaded) : new Bitmap(loaded))
        using (Bitmap resized = Resize(source, size))
        {
            resized.Save(targetPath, ImageFormat.Png);
        }
    }

    private static Bitmap TrimTransparentMargin(Bitmap source)
    {
        int left = source.Width, top = source.Height, right = -1, bottom = -1;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).A > 8)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
        }
        if (right < 0) return new Bitmap(source);

        // 정사각형을 유지한다.
        int side = Math.Max(right - left + 1, bottom - top + 1);
        int cx = (left + right + 1) / 2, cy = (top + bottom + 1) / 2;
        Rectangle area = new Rectangle(cx - side / 2, cy - side / 2, side, side);
        Bitmap trimmed = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(trimmed))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle(0, 0, side, side), area, GraphicsUnit.Pixel);
        }
        return trimmed;
    }

    /// <summary>sizes[i] 크기 그림을 sources[i] 원본으로 만든 .ico를 쓴다. 256px은 PNG, 나머지는 32비트 BMP로 넣는다.</summary>
    public static void SaveIco(string[] sources, int[] sizes, string targetPath)
    {
        List<byte[]> images = new List<byte[]>();
        for (int i = 0; i < sizes.Length; i++)
        {
            using (Bitmap source = new Bitmap(sources[i]))
            using (Bitmap resized = Resize(source, sizes[i]))
            {
                images.Add(sizes[i] >= 256 ? ToPng(resized) : ToDib(resized));
            }
        }

        using (FileStream stream = File.Create(targetPath))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) writer.Write(image);
        }
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    // .ico 안의 BMP: 높이를 두 배로 적은 BITMAPINFOHEADER + 아래에서 위로 쌓은 BGRA + 1비트 AND 마스크.
    private static byte[] ToDib(Bitmap bitmap)
    {
        int size = bitmap.Width;
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[data.Stride * size];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        int stride = data.Stride;
        bitmap.UnlockBits(data);

        int maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(size * size * 4 + maskStride * size);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            for (int y = size - 1; y >= 0; y--)
            {
                writer.Write(pixels, y * stride, size * 4);
            }
            for (int y = size - 1; y >= 0; y--)
            {
                byte[] row = new byte[maskStride];
                for (int x = 0; x < size; x++)
                {
                    if (pixels[y * stride + x * 4 + 3] == 0)
                    {
                        row[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }
                writer.Write(row);
            }
            return stream.ToArray();
        }
    }
}
"@

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceDir = Join-Path $root "assets\icon"
$app = Join-Path $sourceDir "altong_app_icon.png"
$idle = Join-Path $sourceDir "altong_tray_idle.png"
$focus = Join-Path $sourceDir "altong_tray_focus.png"

$appAssets = Join-Path $root "src\Altong.Client\Assets"
$packageAssets = Join-Path $root "packaging\sparse\Assets"
New-Item -ItemType Directory -Force -Path $appAssets, $packageAssets | Out-Null

# exe·작업 표시줄·Alt+Tab 아이콘. 32px 이하는 기호 없는 골드 T($focus).
$appSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$appSources = $appSizes | ForEach-Object { if ($_ -le 32) { $focus } else { $app } }
[AltongIconWriter]::SaveIco([string[]]$appSources, [int[]]$appSizes, (Join-Path $appAssets "AppIcon.ico"))

# 트레이 아이콘. 배율 100~250%에서 쓰는 16~40px과 여유분.
$traySizes = @(16, 20, 24, 32, 40, 48, 64)
[AltongIconWriter]::SaveIco([string[]]($traySizes | ForEach-Object { $idle }), [int[]]$traySizes, (Join-Path $appAssets "TrayIdle.ico"))
[AltongIconWriter]::SaveIco([string[]]($traySizes | ForEach-Object { $focus }), [int[]]$traySizes, (Join-Path $appAssets "TrayFocus.ico"))

# sparse 패키지 로고 (작업 표시줄, 시작 메뉴·검색, Windows 알림 설정에 표시).
# Windows는 화면 배율과 표시 크기에 맞는 파일을 resources.pri에서 골라 쓴다.
#  - scale-100~400: 배율 100~400%용. 44px 로고라면 44·55·66·88·176px.
#  - targetsize-N: 작업 표시줄·시작 메뉴 목록처럼 정해진 크기로 그릴 때 쓰는 N px 그림.
#  - altform-unplated: 작업 표시줄용. 없으면 Windows가 로고를 판 위에 작게 얹어 그린다.
Get-ChildItem $packageAssets -Filter "*.png" | Remove-Item
$scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }
$logos = @(
    @{ Name = "Square44x44Logo"; Size = 44; Source = $focus },
    @{ Name = "Square150x150Logo"; Size = 150; Source = $app },
    @{ Name = "StoreLogo"; Size = 50; Source = $focus }
)
foreach ($logo in $logos) {
    foreach ($scale in $scales.Keys) {
        $px = [int][Math]::Round($logo.Size * $scales[$scale])
        [AltongIconWriter]::SavePng($logo.Source, $px, (Join-Path $packageAssets "$($logo.Name).scale-$scale.png"))
    }
}
# Microsoft 권장 크기만 만든다. 그 사이 크기는 Windows가 가까운 큰 그림을 줄여 쓴다.
foreach ($target in 16, 24, 32, 48, 256) {
    $source = if ($target -le 32) { $focus } else { $app }
    [AltongIconWriter]::SavePng($source, $target, (Join-Path $packageAssets "Square44x44Logo.targetsize-$target.png"))
    # 작업 표시줄용은 원본의 투명 여백(약 5%)을 잘라 내 다른 앱 아이콘과 비슷한 크기로 보이게 한다.
    [AltongIconWriter]::SavePng($source, $target, (Join-Path $packageAssets "Square44x44Logo.targetsize-${target}_altform-unplated.png"), $true)
}

# resources.pri: 위 파일 목록. Windows SDK의 makepri가 있어야 다시 만들 수 있다(결과물은 저장소에 있다).
$makepri = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makepri.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($makepri) {
    $packageRoot = Join-Path $root "packaging\sparse"
    Get-ChildItem $packageRoot -Filter "resources*.pri" | Remove-Item
    $config = Join-Path $env:TEMP "altong-priconfig.xml"
    & $makepri.FullName createconfig /cf $config /dq lang-en-US /pv 10.0.0 /o | Out-Null
    # 기본 설정은 배율별 그림을 별도 리소스 팩으로 나눠 scale-100만 남긴다. 등록만 하는 패키지라 하나로 둔다.
    [xml]$configXml = Get-Content $config
    $packaging = $configXml.SelectSingleNode("//packaging")
    if ($packaging) { [void]$packaging.ParentNode.RemoveChild($packaging) }
    $configXml.Save($config)
    & $makepri.FullName new /pr $packageRoot /cf $config /mn (Join-Path $packageRoot "AppxManifest.xml") /of (Join-Path $packageRoot "resources.pri") /o | Out-Null
    Remove-Item $config
    if ($LASTEXITCODE -ne 0) { throw "makepri 실패 ($LASTEXITCODE)" }
} else {
    Write-Warning "Windows SDK의 makepri.exe가 없어 resources.pri를 다시 만들지 못했습니다."
}

Write-Host "아이콘을 만들었습니다: $appAssets, $packageAssets"

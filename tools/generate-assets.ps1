Add-Type -AssemblyName System.Drawing

$assets = @(
    @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150 },
    @{ Name = "Square44x44Logo.png"; Width = 44; Height = 44 },
    @{ Name = "StoreLogo.png"; Width = 50; Height = 50 }
)

$targetDir = Join-Path $PSScriptRoot "..\packaging\sparse\Assets"
if (!(Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

foreach ($a in $assets) {
    $filePath = Join-Path $targetDir $a.Name
    $bmp = New-Object System.Drawing.Bitmap $a.Width, $a.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.Clear([System.Drawing.Color]::FromArgb(40, 120, 240)) # Altong Blue
    $graphics.Dispose()
    $bmp.Save($filePath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created asset: $filePath"
}

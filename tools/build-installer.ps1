# 알통 설치 파일(dist\AltongSetup-<버전>.exe)을 만든다.
#
#   powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1 [-Version 1.0.0]
#
# 필요한 것
#   - .NET 9 SDK
#   - Windows SDK (makeappx.exe, signtool.exe)
#   - Inno Setup 6 (ISCC.exe):  winget install --id JRSoftware.InnoSetup -e
#   - 모델 파일 3개 (README "모델 준비")
#
# 하는 일
#   1. 앱을 .NET 런타임 포함(self-contained)으로 publish 한다. 받는 PC에 .NET이 없어도 된다.
#   2. 알림 권한용 sparse 패키지(Altong.msix)를 만들고 서명한다.
#      서명 인증서(CN=AltongDev)는 이 PC의 현재 사용자 인증서 저장소에 없으면 새로 만든다.
#      설치 프로그램이 이 인증서를 받는 PC에 신뢰시키고 패키지를 등록한다.
#   3. Inno Setup으로 설치 파일을 만든다.

param(
    [string]$Version = "1.0.0",
    [string]$IsccPath,
    [switch]$KeepIntermediate
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dist = Join-Path $root "dist"
$publishDir = Join-Path $dist "publish"
$packageDir = Join-Path $dist "package"
$certFile = Join-Path $dist "Altong.cer"
$publisher = "CN=AltongDev"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "버전은 1.0.0 형식이어야 합니다: $Version" }

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# 0. 준비물 확인
Step "준비물 확인"
$models = @(
    "models\filtering\klue-base-safety-v1\model-fp32.onnx",
    "models\briefing\qwen_briefing_v8_q8_0.gguf",
    "models\ocr\ppocr-v5-ko\korean_PP-OCRv5_mobile_rec.onnx"
)
$missing = $models | Where-Object { -not (Test-Path (Join-Path $root $_)) }
if ($missing) { throw "모델 파일이 없습니다. README '모델 준비'를 따라 받아 주세요:`n  $($missing -join "`n  ")" }

$sdkTool = {
    param($name)
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "Windows SDK의 $name 를 찾지 못했습니다." }
    $found.FullName
}
$makeappx = & $sdkTool "makeappx.exe"
$signtool = & $sdkTool "signtool.exe"

if (-not $IsccPath) {
    $IsccPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
}
if (-not $IsccPath) {
    $inno = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "Inno Setup*" -and $_.InstallLocation } | Select-Object -First 1
    if ($inno) { $IsccPath = Join-Path $inno.InstallLocation "ISCC.exe" }
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw "Inno Setup(ISCC.exe)을 찾지 못했습니다. winget install --id JRSoftware.InnoSetup -e 로 설치하거나 -IsccPath로 지정하세요."
}
Write-Host "makeappx: $makeappx"
Write-Host "signtool: $signtool"
Write-Host "ISCC:     $IsccPath"

# 1. publish
Step "앱 publish (Release, win-x64, .NET 포함)"
foreach ($dir in $publishDir, $packageDir) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
& dotnet publish (Join-Path $root "src\Altong.Client") -c Release -r win-x64 --self-contained true `
    -o $publishDir -p:DebugType=none -p:DebugSymbols=false --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 ($LASTEXITCODE)" }

# 2. 서명 인증서
Step "패키지 서명 인증서"
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
        ($_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3") } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName "Altong 패키지 서명 (자체 서명)" -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Write-Host "새 인증서를 만들었습니다: $($cert.Thumbprint)"
} else {
    Write-Host "기존 인증서를 씁니다: $($cert.Thumbprint) (만료 $($cert.NotAfter.ToString('yyyy-MM-dd')))"
}
Export-Certificate -Cert $cert -FilePath $certFile -Force | Out-Null

# 3. sparse 패키지 (Altong.msix)
Step "알림 권한용 패키지 만들기와 서명"
New-Item -ItemType Directory -Force -Path (Join-Path $packageDir "Assets") | Out-Null
$manifest = Get-Content (Join-Path $root "packaging\sparse\AppxManifest.xml") -Raw -Encoding UTF8
$manifest = $manifest -replace '(<Identity[^>]*?Version=")[^"]+(")', "`${1}$Version.0`${2}"
[IO.File]::WriteAllText((Join-Path $packageDir "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))
Copy-Item (Join-Path $root "packaging\sparse\Assets\*.png") (Join-Path $packageDir "Assets")
Copy-Item (Join-Path $root "packaging\sparse\resources.pri") $packageDir

$msix = Join-Path $publishDir "Altong.msix"
# 실행 파일은 패키지 밖(설치 폴더)에 있으므로 내용 검사(/nv)는 끈다.
& $makeappx pack /d $packageDir /p $msix /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx 실패 ($LASTEXITCODE)" }
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signtool 실패 ($LASTEXITCODE)" }

# 4. 설치 파일
Step "설치 파일 만들기 (Inno Setup)"
& $IsccPath /Q `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDir" `
    "/DCertFile=$certFile" `
    "/DCertThumbprint=$($cert.Thumbprint)" `
    "/DOutputDir=$dist" `
    "/DRepoRoot=$root" `
    (Join-Path $root "packaging\installer\Altong.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 실패 ($LASTEXITCODE)" }

if (-not $KeepIntermediate) {
    # publish 폴더만 1GB가 넘어서 지운다. 설치 파일 안에 다 들어 있다.
    Remove-Item $publishDir, $packageDir, $certFile -Recurse -Force
}

$setup = Get-Item (Join-Path $dist "AltongSetup-$Version.exe")
Step "완료"
Write-Host "$($setup.FullName)  ($([Math]::Round($setup.Length / 1MB)) MB)" -ForegroundColor Green

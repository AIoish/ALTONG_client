# 알통 개발과 설치 파일 빌드에 필요한 모델 파일을 받는다. 모델 파일은 Git에 넣지 않는다(*.onnx, *.gguf).
#
#   powershell -ExecutionPolicy Bypass -File tools/download-models.ps1             # 없거나 다른 파일만 받기
#   powershell -ExecutionPolicy Bypass -File tools/download-models.ps1 -CheckOnly  # 받지 않고 확인만 (설치 파일 빌드에서 사용)
#
# 해시가 이미 맞는 파일은 건너뛴다. 모델이 바뀌면 아래 표의 주소와 해시만 고친다.
# 필터 모델의 해시는 models/filtering/klue-base-safety-v1/manifest.json을 따른다.
# manifest가 바뀌었는데 예전 onnx가 남아 있으면 앱은 필터를 끄고 모든 알림을 통과시킨다. 화면에는 오류가 보이지 않는다.

param([switch]$CheckOnly)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$filterDir = "models\filtering\klue-base-safety-v1"
$filterManifest = Get-Content (Join-Path $root "$filterDir\manifest.json") -Raw -Encoding UTF8 | ConvertFrom-Json

$models = @(
    [pscustomobject]@{
        Name           = "필터 모델 (filtering-v3)"
        Path           = "$filterDir\model-fp32.onnx"
        Sha256         = $filterManifest.model_sha256
        Url            = "https://github.com/AIoish/ALTONG_ai/releases/download/filtering-v3/ALTONG_filtering_joint_2026-11-19.zip"
        DownloadSha256 = "A9E383B710D207B67A8528AAE3629E1DE0179FB1385F119D3D94AB040E295AD4"
        ZipEntry       = "models/filtering/klue-base-safety-v1/model-fp32.onnx"
    },
    [pscustomobject]@{
        # ggml-org(llama.cpp 개발팀) Gemma 4 E2B Q4_0, Apache 2.0. 주소는 특정 커밋으로 고정한다.
        Name           = "요약 모델 (Gemma 4 E2B Q4_0)"
        Path           = "models\briefing\gemma-4-E2B-it-Q4_0.gguf"
        Sha256         = "8E30DFF3AC4C8434C49A7036FA15564BDBB6044E42BF04550BF1A096AD7E6A52"
        Url            = "https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/2a8538c2e2d4687953a86c422c7c623cfd022880/gemma-4-E2B-it-Q4_0.gguf"
        DownloadSha256 = $null
        ZipEntry       = $null
    },
    [pscustomobject]@{
        # PaddlePaddle 공식 korean_PP-OCRv5_mobile_rec_onnx, Apache 2.0
        Name           = "카카오톡 OCR 모델 (PP-OCRv5 한국어)"
        Path           = "models\ocr\ppocr-v5-ko\korean_PP-OCRv5_mobile_rec.onnx"
        Sha256         = "92F0B7785E64FC9090106A241CF4C1EB97472824558272751B88A2A4476D3A08"
        Url            = "https://huggingface.co/PaddlePaddle/korean_PP-OCRv5_mobile_rec_onnx/resolve/5c6f574b8e2230adf4287b33e736d71b9fabd28e/inference.onnx"
        DownloadSha256 = $null
        ZipEntry       = $null
    }
)

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash }

$temp = Join-Path $env:TEMP "altong-models"
$notReady = @()
foreach ($m in $models) {
    $target = Join-Path $root $m.Path
    $expected = $m.Sha256.ToUpperInvariant()
    $exists = Test-Path -LiteralPath $target
    if ($exists -and (Get-Sha256 $target) -eq $expected) { Write-Host "[최신] $($m.Name)"; continue }

    $state = if ($exists) { "다른 버전" } else { "없음" }
    if ($CheckOnly) {
        Write-Host "[$state] $($m.Name): $($m.Path)" -ForegroundColor Yellow
        $notReady += $m.Name
        continue
    }

    Write-Host "[받는 중] $($m.Name) ($state)" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $temp | Out-Null
    $download = Join-Path $temp ([IO.Path]::GetFileName(([uri]$m.Url).AbsolutePath))
    & curl.exe -L --fail --retry 2 -o $download $m.Url
    if ($LASTEXITCODE -ne 0) { throw "다운로드에 실패했습니다: $($m.Url)" }
    $expectedDownload = if ($m.ZipEntry) { $m.DownloadSha256.ToUpperInvariant() } else { $expected }
    if ((Get-Sha256 $download) -ne $expectedDownload) {
        Remove-Item -LiteralPath $download -Force
        throw "받은 파일의 해시가 다릅니다: $($m.Url)"
    }

    $source = $download
    if ($m.ZipEntry) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($download)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $m.ZipEntry } | Select-Object -First 1
            if (-not $entry) { throw "ZIP 안에 $($m.ZipEntry) 파일이 없습니다." }
            $source = Join-Path $temp ([IO.Path]::GetFileName($m.ZipEntry))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $source, $true)
        }
        finally { $zip.Dispose() }
        Remove-Item -LiteralPath $download -Force
        if ((Get-Sha256 $source) -ne $expected) {
            throw "ZIP 안의 파일이 manifest 해시와 다릅니다. manifest에 맞는 릴리즈 주소로 이 스크립트를 고쳐야 합니다: $($m.Name)"
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Move-Item -LiteralPath $source -Destination $target -Force
    Write-Host "[완료] $($m.Name)" -ForegroundColor Green
}

if ($notReady.Count -gt 0) {
    Write-Host "`n모델 파일이 없거나 버전이 다릅니다. powershell -ExecutionPolicy Bypass -File tools/download-models.ps1 로 받아 주세요." -ForegroundColor Yellow
    exit 1
}
exit 0

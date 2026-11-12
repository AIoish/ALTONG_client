# 설치 폴더에 있는 Altong.msix(sparse 패키지)를 현재 사용자에게 등록한다.
# 설치 프로그램이 마지막 단계에서 관리자가 아닌 원래 사용자 권한으로 실행한다.
# 등록해야 앱이 패키지 정체성을 얻어 Windows 알림을 읽고, 작업 표시줄·시작 메뉴에 아이콘이 보인다.

$ErrorActionPreference = "Stop"
$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $env:TEMP "altong-register-package.log"

try {
    # 개발용으로 등록해 둔 패키지나 예전 설치본이 있으면 같은 이름이라 새로 등록할 수 없다.
    Get-AppxPackage -Name "Altong.Client" | Remove-AppxPackage
    Add-AppxPackage -Path (Join-Path $installDir "Altong.msix") -ExternalLocation $installDir
    "OK $(Get-Date -Format s)" | Out-File $log -Encoding utf8
    exit 0
}
catch {
    "FAIL $(Get-Date -Format s)`r`n$($_ | Out-String)" | Out-File $log -Encoding utf8
    exit 1
}

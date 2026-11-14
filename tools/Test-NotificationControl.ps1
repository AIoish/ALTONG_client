# Run inside the Windows build being verified, from an interactive logged-on desktop.
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory,
    [string]$ExpectedWindowsVersion
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$ResultsDirectory) {
    $current = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $run = "$($current.CurrentBuild).$($current.UBR)-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $ResultsDirectory = Join-Path $repoRoot ('verify-build\native-notification-control\' + $run)
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$testProject = Join-Path $repoRoot 'tests\Altong.Client.Tests\Altong.Client.Tests.csproj'
$mockProject = Join-Path $repoRoot 'tools\Altong.MockGenerator\Altong.MockGenerator.csproj'
$mockPath = Join-Path $repoRoot "tools\Altong.MockGenerator\bin\$Configuration\net9.0-windows10.0.17763.0\Altong.MockGenerator.exe"
$variables = @('ALTONG_NATIVE_NOTIFICATION_TESTS','ALTONG_NATIVE_NOTIFICATION_WRITE_TESTS','ALTONG_NOTIFICATION_MOCK_PATH')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$resultPath = Join-Path $ResultsDirectory 'result.json'
try {
    $windows = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $dll = Join-Path ([Environment]::SystemDirectory) 'NotificationController.dll'
    $elevated = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    [pscustomobject]@{
        WindowsVersion = $windows.DisplayVersion
        WindowsBuild = "$($windows.CurrentBuild).$($windows.UBR)"
        ProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        OSArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        ModuleSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
        SessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        Elevated = $elevated
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'environment.json') -Encoding utf8
    if ($ExpectedWindowsVersion -and $windows.DisplayVersion -ne $ExpectedWindowsVersion) {
        throw "Expected Windows $ExpectedWindowsVersion, found $($windows.DisplayVersion). No write tests were started."
    }
    if ($elevated -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        throw 'Run from an interactive desktop without administrator elevation.'
    }
    if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Altong\notification-recovery.json')) {
        throw 'Finish the existing focus/recovery operation first. The existing recovery file has been preserved.'
    }
    & dotnet build $testProject --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & dotnet build $mockProject --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Mock generator build failed.' }
    $mock = Start-Process -FilePath $mockPath -ArgumentList '--notification-control-test' -WindowStyle Hidden -PassThru
    if (!$mock.WaitForExit(90000)) { $mock.Kill(); throw 'Owned mock registration timed out before write tests started.' }
    if ($mock.ExitCode -ne 0) { throw 'Mock notification registration failed.' }
    [Environment]::SetEnvironmentVariable('ALTONG_NATIVE_NOTIFICATION_TESTS', '1', 'Process')
    [Environment]::SetEnvironmentVariable('ALTONG_NATIVE_NOTIFICATION_WRITE_TESTS', '1', 'Process')
    [Environment]::SetEnvironmentVariable('ALTONG_NOTIFICATION_MOCK_PATH', $mockPath, 'Process')
    & dotnet test $testProject --configuration $Configuration --no-build --no-restore `
        --filter 'FullyQualifiedName~ReadRegisteredSendersOnValidatedWindows|FullyQualifiedName~SuppressAndRestoreOnlyOwnedMockSender' `
        --results-directory $ResultsDirectory --logger 'trx;LogFileName=notification-control.trx' --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native notification control validation failed. Review the TRX results.' }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $ResultsDirectory 'notification-control.trx') -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($counters.executed -ne '2' -or $counters.passed -ne '2') {
        throw 'Both native tests must execute and pass; skipped tests do not validate this Windows build.'
    }
    [pscustomobject]@{Outcome='Passed';Executed=2;Passed=2;
        WindowsVersion=$windows.DisplayVersion;WindowsBuild="$($windows.CurrentBuild).$($windows.UBR)"} |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output "PASS: read, suppress, and restore checks passed. Evidence: $ResultsDirectory"
} catch {
    [pscustomobject]@{Outcome='Failed';Error=$_.Exception.Message} |
        ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    throw
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}

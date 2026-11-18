# Run on the ONE central server. Tester PCs never run this script or enter Slack tokens.
[CmdletBinding()]
param([switch] $ValidateOnly)

$ErrorActionPreference = 'Stop'
$required = @(
    'ALTONG_SLACK_PUBLIC_URL', 'ALTONG_SLACK_CLIENT_ID', 'ALTONG_SLACK_CLIENT_SECRET',
    'ALTONG_SLACK_APP_TOKEN', 'ALTONG_SLACK_ENCRYPTION_KEY', 'ALTONG_SLACK_DATA_DIR',
    'ALTONG_SLACK_RETENTION_HOURS'
)
# Public app ID supplied by the owner. Secrets are never read from the repository.
if (-not $env:ALTONG_SLACK_APP_ID) { $env:ALTONG_SLACK_APP_ID = 'A0CK8B7QPSL' }
if (-not $env:ALTONG_SLACK_CLIENT_ID) { $env:ALTONG_SLACK_CLIENT_ID = '12062444213687.12654381839904' }
$missing = @($required | Where-Object { -not [Environment]::GetEnvironmentVariable($_) })
if ($missing.Count -gt 0) {
    throw ('Missing server setting names: ' + ($missing -join ', ') + '. See docs/SLACK_INTEGRATION.md. Do not paste secret values into chat.')
}
if ($ValidateOnly) {
    Write-Host 'Required Slack server setting names are present. Values are hidden.'
    exit 0
}
$projectPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Altong.Slack.Server\Altong.Slack.Server.csproj'
& dotnet run --project $projectPath --no-launch-profile
exit $LASTEXITCODE

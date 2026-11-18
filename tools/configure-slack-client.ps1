# Build-time public configuration. End users receive this file with the app and enter no tokens.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][Uri] $ServerUrl)

$ErrorActionPreference = 'Stop'
if (-not $ServerUrl.IsAbsoluteUri -or $ServerUrl.Scheme -ne 'https' -or $ServerUrl.UserInfo -or
    $ServerUrl.Query -or $ServerUrl.Fragment -or $ServerUrl.AbsolutePath -ne '/') {
    throw 'ServerUrl must be an HTTPS origin, without a path, query, fragment or credentials.'
}
$root = Split-Path $PSScriptRoot -Parent
$configPath = Join-Path $root 'src\Altong.Client\slack-service.json'
@{ ServerUrl = $ServerUrl.AbsoluteUri } | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
Write-Host 'Public Slack relay URL configured. Build/publish Altong to include it. No credentials were written.'

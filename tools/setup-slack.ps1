# Personal development setup. Tokens never enter command history or the repository.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function Read-SlackSecret([string] $Prompt, [string] $Prefix) {
    $secret = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    try {
        $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer).Trim()
        if (-not $value.StartsWith($Prefix) -or $value -match '\s') {
            throw "Wrong token type. Expected prefix: $Prefix"
        }
        return $value
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        $secret.Dispose()
    }
}

$appToken = Read-SlackSecret 'App-level token (xapp-...)' 'xapp-'
$userToken = Read-SlackSecret 'User OAuth token (xoxp-...)' 'xoxp-'
$plain = [Text.Encoding]::UTF8.GetBytes((@{ AppToken = $appToken; UserToken = $userToken } | ConvertTo-Json -Compress))
$tokenDirectory = Join-Path $env:LOCALAPPDATA 'Altong'
$tokenPath = Join-Path $tokenDirectory 'slack-tokens.dat'
$temporaryPath = Join-Path $tokenDirectory ('slack-tokens-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.Directory]::CreateDirectory($tokenDirectory) | Out-Null
    [IO.File]::WriteAllBytes($temporaryPath, $encrypted)
    Move-Item -LiteralPath $temporaryPath -Destination $tokenPath -Force
}
finally {
    [Array]::Clear($plain, 0, $plain.Length)
    $appToken = $null
    $userToken = $null
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath }
}
Write-Host 'Saved encrypted Slack credentials for this Windows user.'
Write-Host 'Restart Altong. Look for [Slack] status in the debug console.'
Write-Host 'Local development only: set ALTONG_SLACK_LOCAL_DEVELOPMENT=1 before launching.'
Write-Host 'Do not use this setup on tester PCs or alongside the central Slack receiver.'
Write-Host 'Only incoming 1:1 DM text is collected. Set ALTONG_ENABLE_SLACK=0 before launching to disable.'

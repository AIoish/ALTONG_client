param (
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

$packageFamilyName = "Altong.Client"
$manifestPath = (Resolve-Path (Join-Path $PSScriptRoot "..\packaging\sparse\AppxManifest.xml")).Path
$externalLocation = (Resolve-Path (Join-Path $PSScriptRoot "..\src\Altong.Client\bin\Debug\net9.0-windows10.0.26100.0")).Path

Write-Host "=== Altong Sparse Package Tool ==="
Write-Host "Manifest: $manifestPath"
Write-Host "External Location: $externalLocation"

# Check if already registered
$existing = Get-AppxPackage -Name $packageFamilyName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Found existing package: $($existing.PackageFullName)"
    Write-Host "Removing existing package..."
    Remove-AppxPackage -Package $existing.PackageFullName
    Write-Host "Existing package removed."
}

if ($Unregister) {
    Write-Host "Unregistration complete."
    exit 0
}

if (!(Test-Path (Join-Path $externalLocation "Altong.Client.exe"))) {
    Write-Error "Altong.Client.exe not found at $externalLocation. Please build the project first."
    exit 1
}

Write-Host "Registering Sparse Package..."
try {
    Add-AppxPackage -Register $manifestPath -ExternalLocation $externalLocation
    Write-Host "Sparse Package registered successfully!" -ForegroundColor Green
    
    $pkg = Get-AppxPackage -Name $packageFamilyName
    Write-Host "Registered Package: $($pkg.PackageFullName)"
    Write-Host "Package Status: $($pkg.Status)"
} catch {
    Write-Host "Registration failed: $_" -ForegroundColor Red
    throw $_
}

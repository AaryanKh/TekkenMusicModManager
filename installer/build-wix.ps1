<#
.SYNOPSIS
  Command-line equivalent of building installer\wix\TekkenMusicModManager.Installer.sln in Visual
  Studio: publishes the app framework-dependent, builds the MSI, downloads the .NET 8 Desktop
  Runtime installer once, and emits the single TekkenMusicModManager-Setup.exe bootstrapper.

.EXAMPLE
  .\installer\build-wix.ps1
  .\installer\build-wix.ps1 -Version 0.2.0 -SkipTests

.NOTES
  Needs the .NET 8 SDK only — WiX v5 comes from NuGet (WixToolset.Sdk). Visual Studio is optional;
  with the free HeatWave extension the .wixproj files open there too.
  Output: installer\Output\TekkenMusicModManager-Setup-<version>.exe (bootstrapper, ~55 MB because the
  runtime installer is embedded) and TekkenMusicModManager-<version>.msi (for admins / silent installs).
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $PSScriptRoot "Output"
function Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    if ($props -match "<Version>([^<]+)</Version>") { $Version = $Matches[1] } else { $Version = "0.1.0" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK was not found. Install it with: winget install Microsoft.DotNet.SDK.8" }

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $root "TekkenMusicModManager.sln") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Step "Building bundle (publish -> MSI -> runtime download -> Setup.exe)"
$bundleProj = Join-Path $PSScriptRoot "wix\Tmm.Bundle\Tmm.Bundle.wixproj"
dotnet build $bundleProj -c Release -p:ProductVersion=$Version --nologo
if ($LASTEXITCODE -ne 0) { throw "WiX build failed" }

New-Item -ItemType Directory -Force $outDir | Out-Null
$setup = Get-ChildItem (Join-Path $PSScriptRoot "wix\Tmm.Bundle\bin") -Recurse -Filter "TekkenMusicModManager-Setup.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$msi   = Get-ChildItem (Join-Path $PSScriptRoot "wix\Tmm.Msi\bin") -Recurse -Filter "TekkenMusicModManager.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "bundle output not found under installer\wix\Tmm.Bundle\bin" }
Copy-Item $setup.FullName (Join-Path $outDir "TekkenMusicModManager-Setup-$Version.exe") -Force
if ($msi) { Copy-Item $msi.FullName (Join-Path $outDir "TekkenMusicModManager-$Version.msi") -Force }

Write-Host ""
Write-Host ("Setup: {0} ({1:N0} MB)" -f (Join-Path $outDir "TekkenMusicModManager-Setup-$Version.exe"), ($setup.Length / 1MB)) -ForegroundColor Green
if ($msi) { Write-Host ("MSI:   {0}" -f (Join-Path $outDir "TekkenMusicModManager-$Version.msi")) }
Write-Host "Unsigned: sign both the MSI and the Setup.exe before distribution (see README)."

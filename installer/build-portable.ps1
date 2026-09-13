<#
.SYNOPSIS
  Builds the portable edition: one self-contained TekkenMusicModManager.exe (plus tmm.exe) that runs
  from any folder with no installation and keeps its settings, catalog and mods in a UserData folder
  next to the exe (because portable.txt is present).

.EXAMPLE
  .\installer\build-portable.ps1
  .\installer\build-portable.ps1 -SkipTests -Version 0.2.0

.NOTES
  Output: installer\Output\TekkenMusicModManager-<version>-portable-win-x64.zip
  Single-file publish: managed code is loaded straight from the exe; native runtime libraries are
  extracted to %TEMP%\.net on first start (a second or two), so first launch is slower than later ones.
  ffmpeg and UnrealPak are still external — set them once in Settings (or use "Install with winget").
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "src\Tmm.App\bin\publish\portable-$Runtime"
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

Step "Publishing single-file, self-contained ($Runtime)"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$common = @(
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishReadyToRun=true", "-p:Version=$Version", "-p:DebugType=none", "-p:DebugSymbols=false",
    "-o", $stage
)
dotnet publish (Join-Path $root "src\Tmm.App\Tmm.App.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (app) failed" }
dotnet publish (Join-Path $root "src\Tmm.Cli\Tmm.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (cli) failed" }

# Marker + docs. The marker is what switches the app into portable mode (Tmm.Core Constants.PortableMarker).
Copy-Item (Join-Path $PSScriptRoot "portable\portable.txt") $stage
Copy-Item (Join-Path $PSScriptRoot "portable\README-portable.txt") $stage
Copy-Item (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt") $stage
Copy-Item (Join-Path $root "README.md") $stage
New-Item -ItemType Directory -Force (Join-Path $stage "UserData") | Out-Null

# Empty dependency folders. ffmpeg and UnrealPak are not bundled (licensing); drop them here and point
# Settings at them. A placeholder note keeps the folders in the zip (Compress-Archive drops empty dirs).
foreach ($tool in @("ffmpeg", "UnrealPak")) {
    $toolDir = Join-Path $stage "tools\$tool"
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "portable\tools\$tool\README.txt") $toolDir
}

foreach ($must in @("TekkenMusicModManager.exe", "tmm.exe", "data\jukebox_slots.csv", "data\stock_catalog.json", "portable.txt", "tools\ffmpeg", "tools\UnrealPak")) {
    if (-not (Test-Path (Join-Path $stage $must))) { throw "portable stage is missing $must" }
}

Step "Zipping"
New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir "TekkenMusicModManager-$Version-portable-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Zip the folder itself so extracting yields TekkenMusicModManager\... rather than loose files.
$wrap = Join-Path ([System.IO.Path]::GetTempPath()) "tmm-portable-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force (Join-Path $wrap "TekkenMusicModManager") | Out-Null
Copy-Item (Join-Path $stage "*") (Join-Path $wrap "TekkenMusicModManager") -Recurse
Compress-Archive -Path (Join-Path $wrap "TekkenMusicModManager") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $wrap -Recurse -Force

$size = (Get-Item $zip).Length / 1MB
Write-Host ""
Write-Host ("Portable zip: {0} ({1:N0} MB)" -f $zip, $size) -ForegroundColor Green
Write-Host "Unsigned: SmartScreen may warn on first run of an unzipped exe until it is code-signed."

<#
.SYNOPSIS
  Builds the production installer: runs the tests, publishes the app + CLI self-contained for
  win-x64, then compiles installer\TekkenMusicModManager.iss with Inno Setup.

.EXAMPLE
  .\installer\build-installer.ps1
  .\installer\build-installer.ps1 -SkipTests
  .\installer\build-installer.ps1 -Version 0.2.0

.NOTES
  Needs the .NET 8 SDK and Inno Setup 6.3+. If Inno Setup is missing the script offers to install
  it with `winget install JRSoftware.InnoSetup`.
  Output: installer\Output\TekkenMusicModManager-Setup-<version>.exe
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Version,
    [string]$Runtime = "win-x64",
    # ReadyToRun precompiles IL to native code: faster startup, larger output. On by default.
    [switch]$NoReadyToRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "src\Tmm.App\bin\publish\$Runtime"

function Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

# --- version --------------------------------------------------------------------------------
if (-not $Version) {
    $props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
    if ($props -match "<Version>([^<]+)</Version>") { $Version = $Matches[1] } else { $Version = "0.1.0" }
}
Write-Host "Tekken Music Mod Manager $Version ($Runtime)"

# --- prerequisites ---------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found. Install it with: winget install Microsoft.DotNet.SDK.8"
}
$sdk = (dotnet --list-sdks | Where-Object { $_ -match "^8\." } | Select-Object -First 1)
if (-not $sdk) { throw "No .NET 8 SDK installed. Install it with: winget install Microsoft.DotNet.SDK.8" }

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $reg = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )
    foreach ($k in $reg) {
        if (Test-Path $k) {
            $loc = (Get-ItemProperty $k -ErrorAction SilentlyContinue).InstallLocation
            if ($loc -and (Test-Path (Join-Path $loc "ISCC.exe"))) { return (Join-Path $loc "ISCC.exe") }
        }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Warning "Inno Setup 6 was not found."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $answer = Read-Host "Install it now with winget? [Y/n]"
        if ($answer -eq "" -or $answer -match "^[Yy]") {
            winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
            $iscc = Find-Iscc
        }
    }
    if (-not $iscc) { throw "Inno Setup 6 is required: https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup)" }
}
Write-Host "Inno Setup: $iscc"

# --- tests ----------------------------------------------------------------------------------
if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $root "TekkenMusicModManager.sln") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not building an installer from a red build." }
}

# --- publish --------------------------------------------------------------------------------
Step "Publishing app ($Runtime, self-contained)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
$r2r = if ($NoReadyToRun) { "false" } else { "true" }
$common = @(
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-p:PublishReadyToRun=$r2r", "-p:Version=$Version", "-p:DebugType=none", "-p:DebugSymbols=false",
    "-o", $publishDir
)
dotnet publish (Join-Path $root "src\Tmm.App\Tmm.App.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (app) failed" }

Step "Publishing CLI into the same folder"
# Same runtime files; the CLI just adds tmm.exe/tmm.dll next to the app.
dotnet publish (Join-Path $root "src\Tmm.Cli\Tmm.Cli.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (cli) failed" }

if (-not (Test-Path (Join-Path $publishDir "TekkenMusicModManager.exe"))) { throw "publish output is missing TekkenMusicModManager.exe" }
if (-not (Test-Path (Join-Path $publishDir "data\jukebox_slots.csv"))) { throw "publish output is missing data\jukebox_slots.csv" }

$size = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Publish folder: {0:N0} MB" -f $size)

# --- installer ------------------------------------------------------------------------------
Step "Compiling installer"
$iss = Join-Path $PSScriptRoot "TekkenMusicModManager.iss"
& $iscc "/DPublishDir=$publishDir" "/DMyAppVersion=$Version" "/O$(Join-Path $PSScriptRoot 'Output')" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-ChildItem (Join-Path $PSScriptRoot "Output") -Filter "TekkenMusicModManager-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ""
Write-Host ("Installer: {0} ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
Write-Host "Unsigned: Windows SmartScreen will warn on first run until the exe is code-signed (see README)."

# build.ps1 — produces redistributable installers for PC Monitor.
#
# Steps:
#   1. Publish sensors.exe (.NET self-contained single-file)
#   2. Build monitor.exe (PyInstaller, --noconsole)
#   3. Stage both sidecars under pc-monitor/src-tauri/binaries/ with the
#      Tauri-required `-x86_64-pc-windows-msvc` triple suffix
#   4. Run the Tauri bundler (frontend build runs as beforeBuildCommand)
#
# Output: pc-monitor/src-tauri/target/release/bundle/{msi,nsis}/...
#
# Run from repo root:    pwsh -File build.ps1
# Optional flags:        -SkipSensors, -SkipMonitor, -SkipFrontend
[CmdletBinding()]
param(
    [switch]$SkipSensors,
    [switch]$SkipMonitor,
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$binaryStage = Join-Path $repo "pc-monitor\src-tauri\binaries"

function Write-Step($msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

# 1. sensors.exe (.NET)
if (-not $SkipSensors) {
    Write-Step "Publishing sensors.exe (.NET, self-contained)"
    Push-Location (Join-Path $repo "backend\sensors-cs")
    try {
        dotnet publish -c Release -o publish | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    } finally { Pop-Location }
}

# 2. monitor.exe (PyInstaller)
if (-not $SkipMonitor) {
    Write-Step "Building monitor.exe (PyInstaller)"
    Push-Location (Join-Path $repo "backend")
    try {
        pyinstaller --noconfirm monitor.spec | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "pyinstaller failed" }
    } finally { Pop-Location }
}

# 3. Stage sidecars with Tauri's required triple suffix
Write-Step "Staging sidecars into $binaryStage"
New-Item -ItemType Directory -Force -Path $binaryStage | Out-Null
$sensorsSrc = Join-Path $repo "backend\sensors-cs\publish\sensors.exe"
$monitorSrc = Join-Path $repo "backend\dist\monitor.exe"
foreach ($pair in @(
    @{ src = $sensorsSrc; dst = "sensors-x86_64-pc-windows-msvc.exe" },
    @{ src = $monitorSrc; dst = "monitor-x86_64-pc-windows-msvc.exe" }
)) {
    if (-not (Test-Path $pair.src)) {
        throw "Missing build output: $($pair.src). Did the previous step fail?"
    }
    Copy-Item -Force $pair.src (Join-Path $binaryStage $pair.dst)
    Write-Host "  staged $($pair.dst)" -ForegroundColor DarkGray
}

# 4. Tauri bundle
if (-not $SkipFrontend) {
    Write-Step "Bundling Tauri app (npm + cargo)"
    Push-Location (Join-Path $repo "pc-monitor")
    try {
        # npm install is idempotent; safe to run.
        npm install --no-audit --no-fund | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        npm run tauri -- build | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "tauri build failed" }
    } finally { Pop-Location }
}

# Locate produced installers
$bundleRoot = Join-Path $repo "pc-monitor\src-tauri\target\release\bundle"
$installers = Get-ChildItem -Path $bundleRoot -Recurse -Include *.msi, *-setup.exe -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
if ($installers) {
    Write-Host "Installers produced:" -ForegroundColor Green
    foreach ($i in $installers) {
        $sizeMb = [Math]::Round($i.Length / 1MB, 1)
        Write-Host "  $($i.FullName)  ($sizeMb MB)"
    }
} else {
    Write-Warning "No installers found under $bundleRoot. Check the Tauri output above."
}

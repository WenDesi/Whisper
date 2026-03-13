#Requires -Version 5.1
<#
.SYNOPSIS
    Builds WhisperDesk as a self-contained, single-file executable.

.DESCRIPTION
    Produces a portable WhisperDesk.exe that bundles the .NET 9 runtime,
    all managed assemblies, and native DLLs into one file.
    Users do not need .NET installed -- just run the exe.

    Output:
      publish/
        WhisperDesk.exe       (single-file, self-contained)
        appsettings.json      (editable config -- not embedded)
        Assets/app.ico        (tray icon)

.PARAMETER OutputDir
    Directory for publish output. Default: ./publish

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER Dev
    Copy appsettings.Development.json into publish output so the exe
    picks up your local Azure keys. For personal use only -- do NOT
    distribute the output when this flag is set.

.PARAMETER NoBuild
    Skip restore/build, just publish (useful after a prior build).

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -Dev
    .\build-release.ps1 -OutputDir "C:\dist\WhisperDesk"
#>

[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "publish"),
    [string]$Configuration = "Release",
    [switch]$Dev,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectFile = Join-Path $PSScriptRoot "src\WhisperDesk\WhisperDesk.csproj"

# ── Preflight checks ─────────────────────────────────────────────
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet CLI not found. Install the .NET 9 SDK: https://dot.net/download"
    exit 1
}

$sdkVersion = & dotnet --version
Write-Host "Using .NET SDK $sdkVersion" -ForegroundColor Cyan

if (-not (Test-Path $ProjectFile)) {
    Write-Error "Project file not found: $ProjectFile"
    exit 1
}

# ── Clean previous output ────────────────────────────────────────
if (Test-Path $OutputDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $OutputDir
}

# ── Publish ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "Publishing WhisperDesk (self-contained, single-file, win-x64)..." -ForegroundColor Green
Write-Host "  Project:       $ProjectFile"
Write-Host "  Configuration: $Configuration"
Write-Host "  Output:        $OutputDir"
Write-Host ""

$publishArgs = @(
    "publish"
    $ProjectFile
    "-c", $Configuration
    "-r", "win-x64"
    "-o", $OutputDir
    "--self-contained", "true"
    "/p:PublishSingleFile=true"
    "/p:IncludeNativeLibrariesForSelfExtract=true"
    "/p:IncludeAllContentForSelfExtract=true"
    "/p:PublishReadyToRun=true"
    "/p:EnableCompressionInSingleFile=true"
)

if ($NoBuild) {
    $publishArgs += "--no-build"
}

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ── Copy dev config (if -Dev) ───────────────────────────────────
if ($Dev) {
    $devConfig = Join-Path $PSScriptRoot "src\WhisperDesk\appsettings.Development.json"
    if (Test-Path $devConfig) {
        Copy-Item $devConfig -Destination $OutputDir
        Write-Host "Copied appsettings.Development.json (local dev keys)" -ForegroundColor Magenta
    } else {
        Write-Warning "appsettings.Development.json not found at: $devConfig"
        Write-Warning "The exe will run without dev overrides."
    }
}

# ── Verify output ────────────────────────────────────────────────
Write-Host ""
Write-Host "Verifying output..." -ForegroundColor Cyan

$exePath = Join-Path $OutputDir "WhisperDesk.exe"
$configPath = Join-Path $OutputDir "appsettings.json"

$errors = @()

if (-not (Test-Path $exePath)) {
    $errors += "MISSING: WhisperDesk.exe"
} else {
    $exeSize = (Get-Item $exePath).Length
    $exeSizeMB = [math]::Round($exeSize / 1MB, 1)
    Write-Host "  WhisperDesk.exe  ($exeSizeMB MB)" -ForegroundColor White
}

if (-not (Test-Path $configPath)) {
    $errors += "MISSING: appsettings.json (should be alongside exe for user configuration)"
} else {
    Write-Host "  appsettings.json (editable config)" -ForegroundColor White
}

# List all files in publish directory
Write-Host ""
Write-Host "Published files:" -ForegroundColor Cyan
Get-ChildItem $OutputDir -Recurse -File | ForEach-Object {
    $relativePath = $_.FullName.Substring($OutputDir.Length + 1)
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $relativePath  ($sizeMB MB)" -ForegroundColor Gray
}

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "WARNINGS:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "The build completed but some expected files are missing." -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Distribute the '$OutputDir' folder. Users just run WhisperDesk.exe." -ForegroundColor Green
    if ($Dev) {
        Write-Host ""
        Write-Host "DEV MODE: appsettings.Development.json included -- do NOT distribute this build." -ForegroundColor Magenta
    } else {
        Write-Host ""
        Write-Host "IMPORTANT: Users must edit appsettings.json with their Azure keys before first run." -ForegroundColor Yellow
    }
}

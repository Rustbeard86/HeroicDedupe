#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build script for HeroicDedupe release.
.DESCRIPTION
    Builds a single-file release executable for Windows x64.
.PARAMETER Clean
    Clean before building.
.PARAMETER Runtime
    Target runtime identifier (default: win-x64).
.EXAMPLE
    ./build-release.ps1
    ./build-release.ps1 -Clean
    ./build-release.ps1 -Runtime linux-x64
#>

param(
    [switch]$Clean,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir "HeroicDedupe.csproj"
$ReleaseDir = Join-Path $ProjectDir "release"

Write-Host "=== HeroicDedupe Release Build ===" -ForegroundColor Cyan
Write-Host "Runtime: $Runtime"
Write-Host "Output:  $ReleaseDir"
Write-Host ""

# Clean if requested
if ($Clean) {
    Write-Host "[1/3] Cleaning..." -ForegroundColor Yellow
    dotnet clean $ProjectFile -c Release -v quiet
    if (Test-Path $ReleaseDir) {
        Remove-Item $ReleaseDir -Recurse -Force
    }
} else {
    Write-Host "[1/3] Skipping clean (use -Clean to force)" -ForegroundColor DarkGray
}

# Restore
Write-Host "[2/3] Restoring packages..." -ForegroundColor Yellow
dotnet restore $ProjectFile -v quiet

# Publish single-file
Write-Host "[3/3] Publishing single-file executable..." -ForegroundColor Yellow
dotnet publish $ProjectFile `
    -c Release `
    -r $Runtime `
    -o $ReleaseDir `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Show output
Write-Host ""
Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host "Output files:" -ForegroundColor Cyan
Get-ChildItem $ReleaseDir | ForEach-Object {
    $size = "{0:N2} MB" -f ($_.Length / 1MB)
    Write-Host "  $($_.Name) ($size)"
}

Write-Host ""
Write-Host "To run: .\release\HeroicDedupe.exe" -ForegroundColor DarkGray

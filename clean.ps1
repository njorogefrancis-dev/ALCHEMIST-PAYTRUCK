# ============================================================================
# ALCHEMIST PAYTRACK - CLEAN SCRIPT (PowerShell)
# Removes all build/publish output so the next build starts fresh.
#
# Usage:
#   .\clean.ps1
#   .\clean.ps1 -ResetAppData   # also deletes local app database/settings
# ============================================================================

param(
    [switch]$ResetAppData = $false
)

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "ALCHEMIST PAYTRACK - CLEAN" -ForegroundColor Cyan
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host ""

$foldersToRemove = @(
    "publish",
    "installer_output",
    "AlchemistPayTrack\bin",
    "AlchemistPayTrack\obj",
    "AlchemistPayTrack.Tests\bin",
    "AlchemistPayTrack.Tests\obj"
)

foreach ($folder in $foldersToRemove) {
    if (Test-Path $folder) {
        Remove-Item -Path $folder -Recurse -Force
        Write-Host "[OK] Removed $folder" -ForegroundColor Green
    }
    else {
        Write-Host "[--] Not found: $folder" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Running dotnet clean..." -ForegroundColor Cyan
dotnet clean AlchemistPayTrack.sln -p:Platform="Any CPU" | Out-Null
Write-Host "[OK] dotnet clean complete" -ForegroundColor Green

if ($ResetAppData) {
    $appDataPath = Join-Path $env:APPDATA "AlchemistPayTrack"
    if (Test-Path $appDataPath) {
        Remove-Item -Path $appDataPath -Recurse -Force
        Write-Host "[OK] Removed app data: $appDataPath" -ForegroundColor Green
    }
    else {
        Write-Host "[--] No app data folder at $appDataPath" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Clean complete. Run .\build.ps1 to build a fresh publish folder." -ForegroundColor Yellow
Write-Host ""

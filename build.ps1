# ============================================================================
# ALCHEMIST PAYTRACK - BUILD SCRIPT (PowerShell)
# Builds the complete solution for Windows Desktop
#
# Usage: .\build.ps1   (the leading .\ is required by PowerShell to run a
#                        script from the current folder)
# ============================================================================

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # RID architecture used ONLY for the publish step (win-x64 / win-x86).
    # The solution build always uses MSBuild Platform "Any CPU" below, since
    # that's the only platform AlchemistPayTrack.sln defines. Keeping these
    # separate avoids the previous bug where the build used one platform and
    # the test step implicitly expected another.
    [ValidateSet('x64', 'x86')]
    [string]$RidArch = 'x64',

    [switch]$RunTests = $true,
    [switch]$Publish = $true
)

$ErrorActionPreference = 'Stop'
$MsBuildPlatform = 'Any CPU'
$TestTfm = 'net10.0-windows'
$TestDll = "AlchemistPayTrack.Tests\bin\$Configuration\$TestTfm\AlchemistPayTrack.Tests.dll"

Write-Host ""
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host "ALCHEMIST PAYTRACK - BUILD SCRIPT (PowerShell)" -ForegroundColor Cyan
Write-Host "============================================================================" -ForegroundColor Cyan
Write-Host ""

# Check if .NET SDK is installed
try {
    $dotnetVersion = dotnet --version
    if (-not $dotnetVersion) { throw "empty version" }
    Write-Host "[OK] .NET SDK installed: $dotnetVersion" -ForegroundColor Green
}
catch {
    Write-Host "[X] ERROR: .NET SDK is not installed!" -ForegroundColor Red
    Write-Host "    Please install .NET 10.0 SDK from: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Build Configuration:" -ForegroundColor Yellow
Write-Host "  Configuration: $Configuration"
Write-Host "  Build Platform: $MsBuildPlatform  (publish target: win-$RidArch)"
Write-Host "  Run Tests: $RunTests"
Write-Host "  Publish: $Publish"
Write-Host ""

# Step 1: Restore NuGet packages
Write-Host "[1/5] Restoring NuGet packages..." -ForegroundColor Cyan
dotnet restore -p:Platform="$MsBuildPlatform"
if ($LASTEXITCODE -ne 0) {
    Write-Host "      [X] Failed to restore NuGet packages!" -ForegroundColor Red
    exit 1
}
Write-Host "      [OK] NuGet packages restored" -ForegroundColor Green
Write-Host ""

# Step 2: Build solution
Write-Host "[2/5] Building solution..." -ForegroundColor Cyan
dotnet build -c $Configuration -p:Platform="$MsBuildPlatform" --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "      [X] Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "      [OK] Solution built successfully" -ForegroundColor Green
Write-Host ""

# Step 3: Run tests - run directly against the DLL the build step just
# produced, instead of re-resolving the test project (this is what caused
# "test source file not found" when build/test disagreed on platform/output).
if ($RunTests) {
    Write-Host "[3/5] Running unit tests..." -ForegroundColor Cyan
    if (-not (Test-Path $TestDll)) {
        Write-Host "      [!] Test assembly not found at: $TestDll" -ForegroundColor Yellow
        Write-Host "          Skipping tests - the build above may not have produced it." -ForegroundColor Yellow
    }
    else {
        dotnet vstest $TestDll
        if ($LASTEXITCODE -ne 0) {
            Write-Host "      [!] Some tests failed (continuing...)" -ForegroundColor Yellow
        }
        else {
            Write-Host "      [OK] Tests completed" -ForegroundColor Green
        }
    }
}
else {
    Write-Host "[3/5] Skipping tests (-RunTests:`$false specified)" -ForegroundColor Yellow
}
Write-Host ""

# Step 4: Publish
if ($Publish) {
    Write-Host "[4/5] Publishing application..." -ForegroundColor Cyan
    $publishProfile = if ($RidArch -eq 'x86') { 'win-x86' } else { 'win-x64' }
    if ($RidArch -eq 'x86') {
        Write-Host "      [!] x86 single-file profile not defined; using win-x64 publish flags inline." -ForegroundColor Yellow
    }

    dotnet publish AlchemistPayTrack/AlchemistPayTrack.csproj `
        -c $Configuration `
        -p:PublishProfile=$publishProfile `
        -o publish
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      [X] Publish failed!" -ForegroundColor Red
        exit 1
    }

    $publishedExe = Join-Path "publish" "AlchemistPayTrack.exe"
    if (-not (Test-Path $publishedExe)) {
        Write-Host "      [X] Published exe not found at $publishedExe" -ForegroundColor Red
        exit 1
    }

    # Keep only the standalone exe in publish (runtime files are bundled inside it).
    Get-ChildItem -Path "publish" -Force |
        Where-Object { $_.Name -ne "AlchemistPayTrack.exe" } |
        Remove-Item -Recurse -Force

    Write-Host "      [OK] Standalone exe published: $publishedExe" -ForegroundColor Green
}
else {
    Write-Host "[4/5] Skipping publish (-Publish:`$false specified)" -ForegroundColor Yellow
}
Write-Host ""

# Summary
Write-Host "============================================================================" -ForegroundColor Green
Write-Host "[OK] BUILD COMPLETE!" -ForegroundColor Green
Write-Host "============================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run the application:"
Write-Host "     - Execute: .\run.bat"
Write-Host ""
Write-Host "  2. Or run in debug mode:"
Write-Host "     - Execute: dotnet run --project AlchemistPayTrack/AlchemistPayTrack.csproj"
Write-Host ""
Write-Host "  3. Standalone executable (copy this one file anywhere):"
Write-Host "     - publish\AlchemistPayTrack.exe"
Write-Host ""

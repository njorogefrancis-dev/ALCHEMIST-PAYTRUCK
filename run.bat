@echo off
REM ============================================================================
REM ALCHEMIST PAYTRACK - RUN SCRIPT
REM Runs the application
REM
REM Usage:
REM   From cmd.exe:      run.bat
REM   From PowerShell:   .\run.bat   (note the leading .\)
REM ============================================================================

setlocal enabledelayedexpansion

echo.
echo ============================================================================
echo ALCHEMIST PAYTRACK - RUNNING APPLICATION
echo ============================================================================
echo.

REM Set variables (kept in sync with build.bat's %OUTPUT_PATH%)
set PUBLISH_PATH=publish\AlchemistPayTrack.exe
set PROJECT_PATH=AlchemistPayTrack\AlchemistPayTrack.csproj

REM Prefer the published, self-contained build if it exists.
if exist "%PUBLISH_PATH%" (
    echo Running published version...
    echo Path: %PUBLISH_PATH%
    echo.
    start "" "%~dp0publish\AlchemistPayTrack.exe"
    echo Application started!
    exit /b 0
)

REM No published build yet - fall back to "dotnet run", but only if the SDK
REM is actually installed (otherwise the error below was confusing/unclear).
echo Published version not found. Attempting to run in debug mode...
echo.

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: No published build found at "%PUBLISH_PATH%", and the .NET SDK
    echo        is not installed, so "dotnet run" is not available either.
    echo.
    echo Please either:
    echo   1. Install the .NET 10.0 SDK from https://dotnet.microsoft.com/download, or
    echo   2. Build the project first using: build.bat
    pause
    exit /b 1
)

dotnet run --project "%PROJECT_PATH%"
if errorlevel 1 (
    echo ERROR: Failed to run application!
    echo Please build the project first using: build.bat
    pause
    exit /b 1
)

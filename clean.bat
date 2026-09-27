@echo off
REM ============================================================================
REM ALCHEMIST PAYTRACK - CLEAN SCRIPT
REM Removes build/publish output for a fresh rebuild.
REM
REM Usage:
REM   clean.bat
REM   clean.bat --reset-app-data
REM ============================================================================

setlocal enabledelayedexpansion

echo.
echo ============================================================================
echo ALCHEMIST PAYTRACK - CLEAN
echo ============================================================================
echo.

if /I "%~1"=="--reset-app-data" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0clean.ps1" -ResetAppData
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0clean.ps1"
)

if errorlevel 1 (
    pause
    exit /b 1
)

pause

@echo off
:: ============================================================
::  PisoNet Watchdog — Service Uninstaller
::  Run this as Administrator.
:: ============================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause
    exit /b 1
)

set SERVICE_NAME=PisoNetWatchdog

echo.
echo Removing "%SERVICE_NAME%" service...

sc query %SERVICE_NAME% >nul 2>&1
if %errorLevel% neq 0 (
    echo Service is not installed.
    pause
    exit /b 0
)

sc stop %SERVICE_NAME% >nul 2>&1
timeout /t 3 /nobreak >nul

sc delete %SERVICE_NAME%
if %errorLevel% equ 0 (
    echo Service removed successfully.
) else (
    echo ERROR: Could not remove the service.
)

echo.
pause

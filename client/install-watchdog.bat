@echo off
:: ============================================================
::  PisoNet Watchdog — Service Installer
::  Run this as Administrator from the folder where both
::  PisoNetClient.exe and PisoNetWatchdog.exe live.
:: ============================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause
    exit /b 1
)

set SERVICE_NAME=PisoNetWatchdog
set DISPLAY_NAME=PisoNet Watchdog
set DESCRIPTION=Monitors PisoNetClient.exe and automatically restarts it if the process is killed.
set EXE_PATH=%~dp0PisoNetWatchdog.exe

if not exist "%EXE_PATH%" (
    echo ERROR: PisoNetWatchdog.exe not found at %EXE_PATH%
    echo Make sure you run this script from the same folder as the executables.
    pause
    exit /b 1
)

echo.
echo Installing "%DISPLAY_NAME%" service...
echo   EXE : %EXE_PATH%
echo.

:: Remove any existing instance first
sc query %SERVICE_NAME% >nul 2>&1
if %errorLevel% equ 0 (
    echo Stopping and removing existing service...
    sc stop %SERVICE_NAME% >nul 2>&1
    timeout /t 2 /nobreak >nul
    sc delete %SERVICE_NAME% >nul 2>&1
    timeout /t 1 /nobreak >nul
)

:: Create the service
sc create %SERVICE_NAME% ^
    binPath= "\"%EXE_PATH%\"" ^
    start= auto ^
    DisplayName= "%DISPLAY_NAME%"
if %errorLevel% neq 0 (
    echo ERROR: Failed to create service.
    pause
    exit /b 1
)

:: Set description
sc description %SERVICE_NAME% "%DESCRIPTION%"

:: Auto-recovery: restart immediately on 1st and 2nd failure, then after 30 s
sc failure %SERVICE_NAME% reset= 60 actions= restart/1000/restart/1000/restart/30000

:: Start the service now
sc start %SERVICE_NAME%
if %errorLevel% neq 0 (
    echo WARNING: Service installed but could not start immediately.
    echo It will start automatically on next boot.
) else (
    echo Service started successfully.
)

echo.
echo Done! "%DISPLAY_NAME%" is now installed and running.
echo It will start automatically with Windows.
echo.
pause

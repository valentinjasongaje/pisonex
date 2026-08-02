@echo off
:: ============================================================
::  Pisonex Client — Full Uninstaller
::
::  Run this from the installed folder (next to PisoNetClient.exe)
::  as Administrator. Unlike just deleting the Program Files
::  folder, this removes EVERYTHING so the next install is a true
::  fresh install:
::    - PisoNetClient.exe / pnxsystem.exe processes (stopped)
::    - pnxsystem Windows Service (stopped + deleted)
::    - HKCU Run startup entry
::    - HKLM\SOFTWARE\PisoNet and HKCU\SOFTWARE\PisoNet (ServerUrl,
::      PCNumber, ApiKey, IsConfigured, all UI/lock-screen prefs —
::      both the client and the watchdog read/write the same key)
::    - %ProgramData%\PisoNet\crash.log, client.log, client.log.old,
::      shutdown.flag
::    - This program folder itself
::
::  Deliberately KEPT: %ProgramData%\PisoNet\license.dat — DPAPI-
::  encrypted store for the local admin PIN hash (AdminPinHash).
::  Wiping it would reset the admin panel PIN back to the "1234"
::  default on every test cycle. Delete it by hand if you actually
::  want that reset too.
:: ============================================================

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

set "INSTALL_DIR=%~dp0"
if "%INSTALL_DIR:~-1%"=="\" set "INSTALL_DIR=%INSTALL_DIR:~0,-1%"

echo.
echo ============================================================
echo  Uninstalling Pisonex Client (full clean)
echo  Folder: %INSTALL_DIR%
echo ============================================================
echo.

echo [1/6] Stopping running processes...
taskkill /f /im PisoNetClient.exe >nul 2>&1
taskkill /f /im pnxsystem.exe >nul 2>&1

echo [2/6] Removing watchdog service...
sc query pnxsystem >nul 2>&1
if %errorLevel% equ 0 (
    sc stop pnxsystem >nul 2>&1
    timeout /t 3 /nobreak >nul
    sc delete pnxsystem >nul 2>&1
)

echo [3/6] Removing Windows startup entry...
reg delete "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v PisoNetClient /f >nul 2>&1

echo [4/6] Removing registry settings (server URL, PC number, license flags, UI prefs)...
reg delete "HKLM\SOFTWARE\PisoNet" /f >nul 2>&1
reg delete "HKCU\SOFTWARE\PisoNet" /f >nul 2>&1

echo [5/6] Removing log files (keeping license.dat / admin PIN)...
del /f /q "%ProgramData%\PisoNet\crash.log" >nul 2>&1
del /f /q "%ProgramData%\PisoNet\client.log" >nul 2>&1
del /f /q "%ProgramData%\PisoNet\client.log.old" >nul 2>&1
del /f /q "%ProgramData%\PisoNet\shutdown.flag" >nul 2>&1

echo [6/6] Scheduling removal of program files...
:: Detached + delayed so this script's own file handle (it lives inside
:: INSTALL_DIR, same convention as install-watchdog.bat) has released
:: before the delete runs — a running .bat can't delete its own folder.
start "" /min cmd /c "timeout /t 2 /nobreak >nul & rmdir /s /q ""%INSTALL_DIR%"" 2>nul"

echo.
echo ============================================================
echo  Done. This folder will finish removing itself in a moment.
echo  The next install will be a completely fresh install:
echo    - No saved server URL / PC number / API key
echo    - No leftover logs
echo    - Admin PIN kept as-is (license.dat retained)
echo    - Watchdog service removed
echo    - This program folder deleted
echo ============================================================
echo.
pause

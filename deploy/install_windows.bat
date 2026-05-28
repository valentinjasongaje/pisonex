@echo off
REM ===========================================================================
REM  Pisonex Server — Windows installer
REM
REM  Self-elevates to Administrator, then installs PisonexServer as a Windows
REM  Service in DELAYED AUTO start mode with crash auto-restart, opens the
REM  firewall, and starts the service.
REM
REM  Usage:  double-click this file, or run from an elevated cmd prompt.
REM ===========================================================================
setlocal

REM Resolve the directory that contains PisonexServer.exe (same folder as
REM this script, by convention).
set "INSTALL_DIR=%~dp0"
if "%INSTALL_DIR:~-1%"=="\" set "INSTALL_DIR=%INSTALL_DIR:~0,-1%"

set "EXE=%INSTALL_DIR%\PisonexServer.exe"
if not exist "%EXE%" (
    echo ERROR: PisonexServer.exe not found at:
    echo   %EXE%
    echo Place this script next to PisonexServer.exe and try again.
    pause
    exit /b 1
)

REM ---------------------------------------------------------------------------
REM  Self-elevate if not already running as Administrator
REM ---------------------------------------------------------------------------
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

echo.
echo ============================================================
echo  Installing Pisonex Server as a Windows Service
echo ============================================================
echo.

REM ---------------------------------------------------------------------------
REM  Remove any previous install (ignore failure if not installed)
REM ---------------------------------------------------------------------------
"%EXE%" remove >nul 2>&1

REM ---------------------------------------------------------------------------
REM  Install (windows_service.py defaults to --startup delayed when 'install'
REM  is present, and runs sc.exe failure recovery configuration afterwards)
REM ---------------------------------------------------------------------------
"%EXE%" install
if %errorlevel% neq 0 (
    echo.
    echo Install failed.  See the output above for details.
    pause
    exit /b %errorlevel%
)

REM ---------------------------------------------------------------------------
REM  Open the firewall for the server port (default 80).  The lifespan also
REM  adds this rule, but doing it here means the rule exists before the very
REM  first start of the service.
REM ---------------------------------------------------------------------------
netsh advfirewall firewall show rule name="Pisonex Server (TCP 80)" >nul 2>&1
if %errorlevel% neq 0 (
    netsh advfirewall firewall add rule name="Pisonex Server (TCP 80)" dir=in action=allow protocol=TCP localport=80 >nul
)

REM ---------------------------------------------------------------------------
REM  Start the service
REM ---------------------------------------------------------------------------
net start PisonexServer
if %errorlevel% neq 0 (
    echo.
    echo Service installed but did not start.  Check pisonet.log next to the exe.
    pause
    exit /b %errorlevel%
)

echo.
echo ============================================================
echo  Pisonex Server installed and running.
echo  Dashboard:  http://localhost/dashboard
echo ============================================================
echo.
pause
endlocal

@echo off
REM ========================================================================
REM  Builds the PisoNet client with ConfuserEx obfuscation applied.
REM  ASCII-only - cmd is sensitive to non-ANSI characters in REM lines.
REM ========================================================================

setlocal enableextensions

set "SCRIPT_DIR=%~dp0"
set "PROJECT_FILE=%SCRIPT_DIR%PisoNetClient\PisoNetClient.vbproj"
set "CONFUSER_CLI=%SCRIPT_DIR%tools\ConfuserEx\Confuser.CLI.exe"

for %%I in ("%SCRIPT_DIR%..\..\build\client-publish")    do set "PUBLISH_DIR=%%~fI"
for %%I in ("%SCRIPT_DIR%..\..\build\client-obfuscated") do set "OBFUSCATED_DIR=%%~fI"
set "CONFUSER_PROJ=%TEMP%\pisonex-confuse.crproj"

if not exist "%PROJECT_FILE%" (
    echo [ERROR] PisoNetClient.vbproj not found at:
    echo         %PROJECT_FILE%
    exit /b 1
)

if not exist "%CONFUSER_CLI%" (
    echo [ERROR] Confuser.CLI.exe not found at:
    echo         %CONFUSER_CLI%
    exit /b 1
)

if exist "%PUBLISH_DIR%"     rmdir /s /q "%PUBLISH_DIR%"
if exist "%OBFUSCATED_DIR%"  rmdir /s /q "%OBFUSCATED_DIR%"
mkdir "%OBFUSCATED_DIR%" 2>nul

if exist "%CONFUSER_PROJ%" del "%CONFUSER_PROJ%"
>>"%CONFUSER_PROJ%" echo ^<?xml version="1.0" encoding="utf-8"?^>
>>"%CONFUSER_PROJ%" echo ^<project outputDir="%OBFUSCATED_DIR%" baseDir="%PUBLISH_DIR%" xmlns="http://confuser.codeplex.com"^>
>>"%CONFUSER_PROJ%" echo   ^<rule pattern="true" preset="none" inherit="false"^>
>>"%CONFUSER_PROJ%" echo     ^<protection id="anti debug" /^>
>>"%CONFUSER_PROJ%" echo     ^<protection id="anti ildasm" /^>
>>"%CONFUSER_PROJ%" echo     ^<protection id="constants" /^>
>>"%CONFUSER_PROJ%" echo     ^<protection id="ctrl flow" /^>
>>"%CONFUSER_PROJ%" echo     ^<protection id="rename"^>
>>"%CONFUSER_PROJ%" echo       ^<argument name="renPublic" value="true" /^>
>>"%CONFUSER_PROJ%" echo     ^</protection^>
>>"%CONFUSER_PROJ%" echo   ^</rule^>
>>"%CONFUSER_PROJ%" echo   ^<module path="PisoNetClient.dll"^>
REM JSON DTOs deserialized by System.Text.Json via reflection.  The VB
REM <Obfuscation(Exclude:=True)> attribute on these classes is NOT honored
REM by the "rename" protection above -- it still renames them, which breaks
REM member login/logout/change-password/heartbeat deserialization at
REM runtime. These explicit rules are the only thing that actually excludes
REM them; keep in sync with client/PisoNetClient/Services/ApiService.vb and
REM client/PisoNetClient/Services/MemberService.vb.
>>"%CONFUSER_PROJ%" echo     ^<rule pattern="name() = 'HeartbeatResponse'" preset="none" inherit="false" /^>
>>"%CONFUSER_PROJ%" echo     ^<rule pattern="name() = 'MemberLoginResponse'" preset="none" inherit="false" /^>
>>"%CONFUSER_PROJ%" echo     ^<rule pattern="name() = 'MemberLogoutResponse'" preset="none" inherit="false" /^>
>>"%CONFUSER_PROJ%" echo     ^<rule pattern="name() = 'MemberChangePasswordResponse'" preset="none" inherit="false" /^>
>>"%CONFUSER_PROJ%" echo   ^</module^>
>>"%CONFUSER_PROJ%" echo ^</project^>

echo.
echo [1/3] Publishing PisoNetClient to %PUBLISH_DIR% ...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=none -p:DebugSymbols=false -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo [ERROR] dotnet publish failed.
    exit /b 1
)

echo.
echo [2/3] Running ConfuserEx ...
"%CONFUSER_CLI%" -n "%CONFUSER_PROJ%"
if errorlevel 1 (
    echo [ERROR] ConfuserEx failed.
    exit /b 1
)

echo.
echo [3/3] Assembling final ship folder ...
set "TEMP_OBF_DLL=%TEMP%\PisoNetClient.obfuscated.dll"
if exist "%TEMP_OBF_DLL%" del "%TEMP_OBF_DLL%"
copy /Y "%OBFUSCATED_DIR%\PisoNetClient.dll" "%TEMP_OBF_DLL%" >nul
if errorlevel 1 (
    echo [ERROR] ConfuserEx output PisoNetClient.dll not found.
    echo         Re-run by hand:  "%CONFUSER_CLI%" -n "%CONFUSER_PROJ%"
    exit /b 1
)

xcopy "%PUBLISH_DIR%\*" "%OBFUSCATED_DIR%\" /E /Y /I /Q >nul
copy /Y "%TEMP_OBF_DLL%" "%OBFUSCATED_DIR%\PisoNetClient.dll" >nul
del "%TEMP_OBF_DLL%" >nul 2>&1

echo.
echo Build complete.  Obfuscated build at: %OBFUSCATED_DIR%

endlocal

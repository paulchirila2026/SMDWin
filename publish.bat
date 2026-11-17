@echo off
title SMD Win - Publish standalone EXE
color 0B
echo.
echo  ============================================
echo   SMD Win - Publish standalone EXE
echo   Result: one .exe, no .NET install needed
echo  ============================================
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  [ERROR] The .NET 8 SDK is not installed.
    echo  https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

cd /d "%~dp0"

:: MSBuild reads environment variables as properties: a variable named OUTDIR would become the
:: build output folder. The publish folder variable is therefore PUBDIR, and these are cleared.
set "OUTDIR="
set "OUTPUTPATH="
set "TMPOUT="

echo  [0/3] Checking whether SMDWin is running...
tasklist /FI "IMAGENAME eq SMDWin.exe" 2>nul | find /I "SMDWin.exe" >nul
if %errorlevel% equ 0 (
    echo  [INFO] SMDWin.exe is running - closing it before publishing...
    taskkill /F /IM SMDWin.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
    echo  [OK] Closed.
) else (
    echo  [OK] SMDWin is not running.
)
echo.

echo  [1/3] Cleaning obj, bin and publish...
if exist obj rmdir /s /q obj
if exist bin rmdir /s /q bin

:: Only the CONTENTS of publish\ are deleted: the folder itself may be open in Explorer.
:: Locked files (e.g. SMDWin.exe still running) are retried 10 times, then the output goes to
:: publish_DATE_TIME. Old publish_* folders are removed.
for /d %%D in (publish_*) do rmdir /s /q "%%D" >nul 2>&1
if exist publish-lista.txt del /f /q publish-lista.txt >nul 2>&1
if exist dotnet-info.txt del /f /q dotnet-info.txt >nul 2>&1
set "PUBDIR=publish"
set /a TRIES=0
:retry_clean
if not exist publish goto clean_ok
del /f /q "publish\*" >nul 2>&1
for /d %%D in ("publish\*") do rmdir /s /q "%%D" >nul 2>&1
dir /b /a "publish" 2>nul | findstr . >nul || goto clean_ok
set /a TRIES+=1
if %TRIES% geq 10 goto clean_fallback
echo  [INFO] A file in publish\ is still in use - retry %TRIES%/10...
taskkill /F /IM SMDWin.exe >nul 2>&1
timeout /t 1 /nobreak >nul
goto retry_clean
:clean_fallback
for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%I"
set "PUBDIR=publish_%STAMP%"
echo  [WARNING] publish\ is locked - the new exe goes to %PUBDIR%\
:clean_ok
echo  [OK] Clean.
echo.

echo  [2/3] Restoring NuGet packages...
dotnet restore SMDWin.csproj -r win-x64
if %errorlevel% neq 0 (
    echo.
    echo  [ERROR] NuGet restore failed.
    pause
    exit /b 1
)
echo.

echo  [3/3] Publishing self-contained (1-2 minutes)...
echo.

:: One line, no ^ continuations. The same options are also set in SMDWin.csproj (win-x64).
:: The full publish log is saved to publish-log.txt next to publish.bat.
if exist publish-log.txt del /f /q publish-log.txt >nul 2>&1
dotnet publish SMDWin.csproj -c Release -r win-x64 -p:SmdPublish=true --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:SatelliteResourceLanguages=en -o "%PUBDIR%" "-flp:logfile=publish-log.txt;verbosity=normal"

if %errorlevel% neq 0 (
    echo.
    echo  [ERROR] Publish failed. Details in publish-log.txt
    pause
    exit /b 1
)

if not exist "%PUBDIR%\SMDWin.dll" goto check_done
:: Not a single file: save diagnostic information.
dir /b /s "%PUBDIR%" > "%~dp0publish-lista.txt"
dotnet --info > "%~dp0dotnet-info.txt" 2>&1
echo.
echo  [WARNING] The publish did not produce a single file (SMDWin.dll is present).
echo            See publish-log.txt and dotnet-info.txt next to publish.bat.
:check_done

echo.
echo  ============================================
echo   [OK] Output: %PUBDIR%
set /p SMDBUILD=<build-number.txt
echo   Build %SMDBUILD%
echo   Copy SMDWin.exe to any PC
echo   Windows 10/11 x64 - no .NET needed
echo  ============================================
echo.

:: Open the output folder
explorer "%~dp0%PUBDIR%"
pause

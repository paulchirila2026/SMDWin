@echo off
title SMD Win - Build and Run
color 0A
echo.
echo  ============================================
echo   SMD Win - Build and Run (Release)
echo  ============================================
echo.

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  [ERROR] The .NET 8 SDK is not installed.
    echo  Download: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)
echo  [OK] .NET SDK:
dotnet --version
echo.

cd /d "%~dp0"
if not exist SMDWin.csproj (
    echo  [ERROR] SMDWin.csproj was not found next to build.bat.
    pause
    exit /b 1
)

echo  [1/3] Restoring NuGet packages...
dotnet restore SMDWin.csproj
if %errorlevel% neq 0 (
    echo  [ERROR] Restore failed. Check the internet connection.
    pause
    exit /b 1
)
echo.

echo  [2/3] Building...
dotnet build SMDWin.csproj -c Release --no-restore
if %errorlevel% neq 0 (
    echo  [ERROR] Build failed. See the errors above.
    pause
    exit /b 1
)
echo.

echo  [3/3] Looking for the executable...
set EXE=
for /r "%~dp0bin\Release" %%f in (SMDWin.exe) do (
    if not defined EXE set EXE=%%f
)
if not defined EXE (
    echo  [ERROR] SMDWin.exe was not found in bin\Release\
    pause
    exit /b 1
)
echo  [OK] %EXE%
echo.

echo  Starting SMD Win as administrator...
powershell -NoProfile -Command "Start-Process '%EXE%' -Verb RunAs"
echo.
pause

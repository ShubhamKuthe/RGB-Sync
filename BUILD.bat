@echo off
setlocal
title Building RGB Sync...
cd /d "%~dp0"

echo.
echo  ===================================
echo    Building RGB Sync  (one moment)
echo  ===================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto nosdk
dotnet --list-sdks > build.log 2>&1
findstr /r "^[89]\. ^1[0-9]\." build.log >nul
if errorlevel 1 goto nosdk

taskkill /im "RGB Sync.exe" /f >nul 2>&1
dotnet publish "src\RGBSync\RGBSync.csproj" -c Release -o "build" >> build.log 2>&1
if errorlevel 1 goto failed

copy /y "build\RGBSync.exe" "RGB Sync.exe" >nul
echo BUILD OK>> build.log

rem ---- put the project folder on the Desktop (works with OneDrive Desktops too) ----
set "DESK="
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"`) do set "DESK=%%D"
set "TARGET=%~dp0"
if defined DESK (
  if /i not "%~dp0"=="%DESK%\RGBSync\" (
    robocopy "%~dp0." "%DESK%\RGBSync" /E /XD bin obj build /NFL /NDL /NJH /NJS /NP >nul
    set "TARGET=%DESK%\RGBSync\"
    echo COPIED TO %DESK%\RGBSync>> build.log
  )
)

echo  Done!  "RGB Sync.exe" is ready in %TARGET%
start "" "%TARGET%RGB Sync.exe"
timeout /t 4 >nul
exit /b 0

:nosdk
echo  The .NET 8 SDK is not installed.
echo  Get it here (pick "SDK x64"):  https://dotnet.microsoft.com/download/dotnet/8.0
echo  Then double-click BUILD.bat again.
echo NO SDK>> build.log
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
timeout /t 15 >nul
exit /b 1

:failed
echo.
echo  Build failed - details are in build.log
echo BUILD FAILED>> build.log
timeout /t 15 >nul
exit /b 1

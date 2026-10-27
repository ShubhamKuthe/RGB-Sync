@echo off
setlocal
title Installing OpenRGB...
cd /d "%~dp0"
echo.
echo  Installing OpenRGB (free, open source) - one moment...
echo.
echo START> openrgb-setup.log

set "ORGB="
call :find
if defined ORGB goto found

where winget >> openrgb-setup.log 2>&1
if errorlevel 1 goto nowinget
winget install --id CalcProgrammer1.OpenRGB -e --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity >> openrgb-setup.log 2>&1
echo WINGET EXIT %errorlevel%>> openrgb-setup.log
call :find
if defined ORGB goto found
echo NOT FOUND AFTER INSTALL>> openrgb-setup.log
echo  Couldn't find OpenRGB after installing - details in openrgb-setup.log
timeout /t 15 >nul
exit /b 1

:found
echo FOUND %ORGB%>> openrgb-setup.log
tasklist /fi "imagename eq OpenRGB.exe" | find /i "OpenRGB.exe" >nul
if errorlevel 1 start "" "%ORGB%" --server --startminimized
echo STARTED>> openrgb-setup.log
echo  OpenRGB is installed and running with its SDK server on.
timeout /t 4 >nul
exit /b 0

:nowinget
echo NO WINGET>> openrgb-setup.log
echo  winget is not available on this PC.
timeout /t 15 >nul
exit /b 1

:find
if exist "%LOCALAPPDATA%\Microsoft\WinGet\Links\OpenRGB.exe" set "ORGB=%LOCALAPPDATA%\Microsoft\WinGet\Links\OpenRGB.exe" & exit /b 0
if exist "%ProgramFiles%\OpenRGB\OpenRGB.exe" set "ORGB=%ProgramFiles%\OpenRGB\OpenRGB.exe" & exit /b 0
for /f "delims=" %%F in ('where /r "%LOCALAPPDATA%\Microsoft\WinGet\Packages" OpenRGB.exe 2^>nul') do (
  set "ORGB=%%F"
  exit /b 0
)
exit /b 0

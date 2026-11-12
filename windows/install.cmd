@echo off
rem Installs SlithyLens: copies publish\SlithyLens.exe into "C:\Program Files\SlithyLens".
rem Run build.cmd first. Program Files needs administrator rights, so Windows will ask
rem for permission (the script reopens itself as administrator in a second window).
setlocal
cd /d "%~dp0"
set "APP=SlithyLens"
set "SELF=%~f0"
set "SRC=%~dp0publish\%APP%.exe"
set "PF=%ProgramW6432%"
if not defined PF set "PF=%ProgramFiles%"
set "DEST=%PF%\%APP%"

if not exist "%SRC%" goto :nobuild
fltmc >nul 2>nul && goto :install
if /i "%~1"=="elevated" goto :noadmin

echo Asking Windows for permission to copy into "%DEST%" ...
powershell -NoProfile -Command "Start-Process -FilePath $env:SELF -ArgumentList 'elevated' -Verb RunAs" 2>nul
if errorlevel 1 goto :noadmin
echo The install continues in the window that just opened.
exit /b 0

:install
if not exist "%DEST%" mkdir "%DEST%"
if not exist "%DEST%" goto :copyfail
copy /y "%SRC%" "%DEST%\%APP%.exe" >nul
if errorlevel 1 goto :copyfail
echo.
echo Installed: %DEST%\%APP%.exe
echo Open it once from there so "Open with" and "Edit with" point at this copy.
goto :finish

:copyfail
echo.
echo Could not copy to "%DEST%".
echo If %APP% is open, close every %APP% window and run install.cmd again.
set RC=1
goto :finish

:nobuild
echo "%SRC%" was not found.
echo Run build.cmd first, then install.cmd again.
exit /b 1

:noadmin
echo.
echo Not installed: administrator permission is needed to write to "%DEST%".
echo Run install.cmd again and choose Yes when Windows asks.
set RC=1
goto :finish

:finish
if /i "%~1"=="elevated" pause
if defined RC exit /b 1
exit /b 0

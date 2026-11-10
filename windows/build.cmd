@echo off
rem Builds SlithyLens.exe into the "publish" folder.
rem   build.cmd             smaller exe; needs the .NET 10 Desktop Runtime (installed with the SDK)
rem   build.cmd standalone  larger exe that runs on any 64-bit Windows 10/11 PC
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || goto :nosdk
dotnet --list-sdks | findstr /b "10." >nul || goto :nosdk
set SC=--self-contained false
if /i "%1"=="standalone" set SC=--self-contained true -p:EnableCompressionInSingleFile=true
dotnet publish SlithyLens\SlithyLens.csproj -c Release -r win-x64 %SC% -p:PublishSingleFile=true -o publish
if errorlevel 1 exit /b 1
echo.
echo Done: %CD%\publish\SlithyLens.exe
echo First time only: open SlithyLens, then ... menu, "Add SlithyLens to Open with and Edit with".
echo Later builds take those entries over automatically the first time you open them.
exit /b 0

:nosdk
echo The .NET 10 SDK was not found.
echo Install it with:   winget install Microsoft.DotNet.SDK.10
echo or download "SDK 10.0.x, Windows x64" from https://dotnet.microsoft.com/download/dotnet/10.0
echo then open a NEW command window and run build.cmd again.
exit /b 1

@echo off
setlocal
cd /d "%~dp0"
title HamTech M0FXB Yaesu FT857D Controller
if exist "publish\HamTech-M0FXB-FT857D-Controller.exe" goto launch
where dotnet >nul 2>nul
if %errorlevel%==0 goto build
echo The free Microsoft .NET 8 SDK is required for the first build.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
pause
exit /b 1
:build
echo Building HamTech M0FXB Yaesu FT857D Controller...
dotnet restore ".\FT857DControl.csproj"
if errorlevel 1 goto failed
dotnet publish ".\FT857DControl.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ".\publish"
if errorlevel 1 goto failed
:launch
start "" ".\publish\HamTech-M0FXB-FT857D-Controller.exe"
exit /b 0
:failed
echo Build failed. Check the message above.
pause
exit /b 1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host "The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow; exit 1 }
Write-Host "Restoring and testing HamTech M0FXB FT857D Controller..." -ForegroundColor Cyan
dotnet restore .\HamTech-M0FXB-FT857D.sln
dotnet test .\FT857DControl.Tests.csproj -c Release --no-restore
Write-Host "Publishing Windows x64 executable..." -ForegroundColor Cyan
dotnet publish .\FT857DControl.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
Write-Host "Done: $root\publish\HamTech-M0FXB-FT857D-Controller.exe" -ForegroundColor Green

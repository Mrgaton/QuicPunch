@echo off
setlocal
cd /d "%~dp0"
if exist "QuicNet.Geolocation.App.exe" (
  QuicNet.Geolocation.App.exe
) else (
  dotnet run --project "%~dp0QuicNet.Geolocation.App.csproj" -c Release
)
if errorlevel 1 pause

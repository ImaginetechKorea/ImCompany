@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "publish\ImCompany.exe" (
  start "" /d "%~dp0publish" "%~dp0publish\ImCompany.exe"
  exit /b
)
set "DOTNET_CLI_HOME=%~dp0.dotnet"
dotnet run --project "%~dp0ImCompany.csproj"
pause

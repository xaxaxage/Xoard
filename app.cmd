@echo off
rem Builds and starts Xoard (WinUI 3). .NET does not need to be in PATH.
setlocal
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
  set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet build "%~dp0src\Xoard.App" -v q -nologo || exit /b 1
start "" "%~dp0artifacts\bin\Xoard.App\debug_win-x64\Xoard.exe"

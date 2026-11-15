@echo off
rem Runs Soundboard.Poc (stage 0). Works without .NET in PATH:
rem the SDK may live in %USERPROFILE%\.dotnet (user-local install).
setlocal
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
  set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet run --project "%~dp0src\Soundboard.Poc" -v q -- %*

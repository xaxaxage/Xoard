@echo off
rem Publishes the MCP server as one self-contained exe: publish\mcp\Xoard.Mcp.exe
rem (Claude Desktop runs it; .NET does not need to be installed or in PATH).
setlocal
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
  set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet publish "%~dp0src\Xoard.Mcp" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish\mcp" -v q -nologo || exit /b 1
echo.
echo MCP server: %~dp0publish\mcp\Xoard.Mcp.exe

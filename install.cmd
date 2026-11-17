@echo off
chcp 65001 >nul
rem Устанавливает Xoard для себя (без прав администратора):
rem   install.cmd            — собрать Release и установить/обновить в %LOCALAPPDATA%\Programs\Xoard,
rem                            ярлыки в «Пуске» и на рабочем столе, сервер для Claude — в mcp\
rem   install.cmd uninstall  — удалить программу, ярлыки и автозапуск (звуки и настройки остаются)
setlocal
set "DEST=%LOCALAPPDATA%\Programs\Xoard"
set "STAGE=%~dp0publish\install"

tasklist /FI "IMAGENAME eq Xoard.exe" 2>nul | find /I "Xoard.exe" >nul && (
  echo Xoard сейчас запущен. Закройте его: значок в трее, правая кнопка, «Выход». Потом запустите install.cmd снова.
  exit /b 1
)
tasklist /FI "IMAGENAME eq Xoard.Mcp.exe" 2>nul | find /I "Xoard.Mcp.exe" >nul && (
  echo Claude Desktop сейчас использует сервер Xoard. Закройте Claude Desktop и запустите install.cmd снова.
  exit /b 1
)

if /I "%~1"=="uninstall" goto uninstall

if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
  set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

echo Сборка Xoard (Release), около минуты...
if exist "%STAGE%" rmdir /s /q "%STAGE%"
dotnet publish "%~dp0src\Xoard.App" -c Release -r win-x64 --self-contained true -o "%STAGE%" -v q -nologo || goto failed
dotnet publish "%~dp0src\Xoard.Mcp" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%STAGE%\mcp" -v q -nologo || goto failed

echo Установка в %DEST%...
robocopy "%STAGE%" "%DEST%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto failed

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$w = New-Object -ComObject WScript.Shell; $exe = Join-Path $env:DEST 'Xoard.exe';" ^
  "foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {" ^
  "  $s = $w.CreateShortcut((Join-Path $dir 'Xoard.lnk')); $s.TargetPath = $exe; $s.WorkingDirectory = $env:DEST;" ^
  "  $s.IconLocation = $exe + ',0'; $s.Description = 'Xoard'; $s.Save() }" || goto failed

echo.
echo Готово: Xoard в меню «Пуск» и на рабочем столе.
echo Сервер для Claude Desktop: %DEST%\mcp\Xoard.Mcp.exe
start "" "%DEST%\Xoard.exe"
exit /b 0

:uninstall
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) { Remove-Item (Join-Path $dir 'Xoard.lnk') -ErrorAction SilentlyContinue }" ^
  "Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Xoard' -ErrorAction SilentlyContinue;" ^
  "Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name 'Xoard' -ErrorAction SilentlyContinue"
if exist "%DEST%" rmdir /s /q "%DEST%"
echo Xoard удалён. Звуки остались в «Документы\Xoard\Sounds», настройки — в %LOCALAPPDATA%\Xoard.
exit /b 0

:failed
echo.
echo Не получилось собрать или установить Xoard — сообщения об ошибке выше.
exit /b 1

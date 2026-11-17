@echo off
chcp 65001 >nul
rem Xoard теперь ставится установщиком и обновляется сам:
rem   https://github.com/xaxaxage/Xoard/releases/latest/download/XoardApp-win-Setup.exe
rem Этот скрипт только убирает старую ручную установку (%LOCALAPPDATA%\Programs\Xoard) —
rem запустите его ОДИН раз ПЕРЕД установщиком: install.cmd uninstall
setlocal
set "DEST=%LOCALAPPDATA%\Programs\Xoard"

if /I not "%~1"=="uninstall" (
  echo Xoard ставится установщиком и обновляется сам:
  echo   https://github.com/xaxaxage/Xoard/releases/latest/download/XoardApp-win-Setup.exe
  echo Старая ручная установка: install.cmd uninstall
  exit /b 0
)

tasklist /FI "IMAGENAME eq Xoard.exe" 2>nul | find /I "Xoard.exe" >nul && (
  echo Xoard сейчас запущен. Закройте его: значок в трее, правая кнопка, «Выход». Потом запустите снова.
  exit /b 1
)

if not exist "%DEST%" (
  echo Старой ручной установки нет — ничего не делаю.
  exit /b 0
)

rem Ярлыки старой установки (у установщика свои). Автозапуск не трогаем: новая версия сама поправит путь.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {" ^
  "  $p = Join-Path $dir 'Xoard.lnk'; if (Test-Path $p) { $t = (New-Object -ComObject WScript.Shell).CreateShortcut($p).TargetPath;" ^
  "  if ($t -like ($env:DEST + '*')) { Remove-Item $p } } }"
rmdir /s /q "%DEST%"
echo Старая установка удалена. Звуки и настройки на месте. Теперь запустите XoardApp-win-Setup.exe.
exit /b 0

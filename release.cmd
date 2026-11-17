@echo off
chcp 65001 >nul
rem Выпуск новой версии Xoard: собрать, упаковать (Velopack) и выложить в GitHub Releases.
rem   release.cmd 1.2.0
rem Перед выпуском: всё закоммичено и запушено (git push). Нужен вход в GitHub CLI (gh auth login).
rem У всех, кто поставил Xoard, появится «Вышла версия 1.2.0 — Обновить».
setlocal
set "V=%~1"
if "%V%"=="" (
  echo Укажите версию, больше прошлой: release.cmd 1.2.0
  exit /b 1
)
set "REPO=https://github.com/xaxaxage/Xoard"
set "ROOT=%~dp0"
set "PUB=%ROOT%publish\app"
set "REL=%ROOT%publish\releases"

if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
  set "PATH=%USERPROFILE%\.dotnet;%PATH%"
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set "GH_TOKEN="
for /f "delims=" %%t in ('gh auth token 2^>nul') do set "GH_TOKEN=%%t"
if "%GH_TOKEN%"=="" (
  echo Нужен вход в GitHub: gh auth login
  exit /b 1
)

echo Сборка Xoard %V%...
if exist "%PUB%" rmdir /s /q "%PUB%"
dotnet tool restore -v q || goto failed
dotnet publish "%ROOT%src\Xoard.App" -c Release -r win-x64 --self-contained true -p:Version=%V% -o "%PUB%" -v q -nologo || goto failed
dotnet publish "%ROOT%src\Xoard.Mcp" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=%V% -o "%PUB%\mcp" -v q -nologo || goto failed

echo Прошлый выпуск (для маленьких обновлений; у первого выпуска его нет — это нормально)...
dotnet tool run vpk -- download github --repoUrl %REPO% --token %GH_TOKEN% -o "%REL%"

echo Упаковка...
dotnet tool run vpk -- pack -u XoardApp -v %V% -r win-x64 -p "%PUB%" -e Xoard.exe --packTitle Xoard --packAuthors xaxaxage -i "%ROOT%src\Xoard.App\Assets\Xoard.ico" -o "%REL%" || goto failed

echo Загрузка на GitHub...
dotnet tool run vpk -- upload github --repoUrl %REPO% --token %GH_TOKEN% -o "%REL%" --publish --releaseName "Xoard %V%" --tag v%V% || goto failed

echo.
echo Готово: %REPO%/releases/tag/v%V%
echo Ссылка для друзей (всегда последняя версия): %REPO%/releases/latest/download/XoardApp-win-Setup.exe
exit /b 0

:failed
echo.
echo Не получилось выпустить версию %V% — сообщения об ошибке выше.
exit /b 1

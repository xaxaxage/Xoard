@echo off
chcp 65001 >nul
rem Собирает rnnoise.dll (шумоподавление голоса) из официального выпуска RNNoise 0.2 (Xiph, BSD-3).
rem Готовый файл уже лежит рядом; пересобирать нужно только при смене версии RNNoise.
rem Нужен Zig (https://ziglang.org, собрано на 0.17.1): zig.exe в PATH или полный путь в переменной ZIG.
rem   set ZIG=C:\tools\zig\zig.exe
rem   build-rnnoise.cmd
setlocal
if not defined ZIG set "ZIG=zig"
set "WORK=%TEMP%\xoard-rnnoise"
set "SRC=%WORK%\rnnoise-0.2"
if exist "%WORK%" rmdir /s /q "%WORK%"
mkdir "%WORK%" || exit /b 1

echo Исходники RNNoise 0.2...
curl -sSfL https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz -o "%WORK%\rnnoise.tar.gz" || exit /b 1
powershell -NoProfile -Command "if ((Get-FileHash '%WORK%\rnnoise.tar.gz' -Algorithm SHA256).Hash -ne '90FCE4B00B9FF24C08DBFE31B82FFD43BAE383D85C5535676D28B0A2B11C0D37') { Write-Error 'SHA-256 mismatch: not the official RNNoise 0.2 archive'; exit 1 }" || exit /b 1
tar -xzf "%WORK%\rnnoise.tar.gz" -C "%WORK%" || exit /b 1

echo Сборка (x86-64 без особых инструкций — работает на любом ПК с Windows 10/11)...
rem Флаги — как у ./configure по умолчанию: без x86 RTCD, DISABLE_DEBUG_FLOAT; экспорт через RNNOISE_EXPORT
"%ZIG%" cc -target x86_64-windows-gnu -mcpu=baseline -shared -O2 -fno-sanitize=undefined -s ^
  -DWIN32 -DRNNOISE_BUILD -DDLL_EXPORT -DDISABLE_DEBUG_FLOAT -D_USE_MATH_DEFINES ^
  -I"%SRC%\include" -I"%SRC%\src" ^
  "%SRC%\src\denoise.c" "%SRC%\src\rnn.c" "%SRC%\src\pitch.c" "%SRC%\src\kiss_fft.c" "%SRC%\src\celt_lpc.c" ^
  "%SRC%\src\nnet.c" "%SRC%\src\nnet_default.c" "%SRC%\src\parse_lpcnet_weights.c" "%SRC%\src\rnnoise_data.c" ^
  "%SRC%\src\rnnoise_tables.c" -o "%WORK%\rnnoise.dll" || exit /b 1

copy /y "%WORK%\rnnoise.dll" "%~dp0rnnoise.dll" >nul || exit /b 1
copy /y "%SRC%\COPYING" "%~dp0RNNoise-COPYING.txt" >nul || exit /b 1
powershell -NoProfile -Command "'SHA-256: ' + (Get-FileHash '%~dp0rnnoise.dll' -Algorithm SHA256).Hash"
echo Готово: %~dp0rnnoise.dll

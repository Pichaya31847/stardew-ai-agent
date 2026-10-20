@echo off
rem Double-click or run from anywhere: record_day.bat [days] [out]
set DAYS=%~1
if "%DAYS%"=="" set DAYS=1
set OUT=%~2
if "%OUT%"=="" set OUT=runs-ep01
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0record_day.ps1" -Days %DAYS% -Out "%OUT%"
rem keep the window only when something went wrong: one window per recorded day piled up otherwise
if errorlevel 1 pause

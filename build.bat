@echo off
setlocal

echo Building self-contained Codex Usage...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" %*
set "buildExitCode=%ERRORLEVEL%"

if not "%buildExitCode%"=="0" echo Build failed with exit code %buildExitCode%.
exit /b %buildExitCode%

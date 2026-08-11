@echo off
setlocal

echo Building smaller Codex Usage for PCs with the .NET 10 Desktop Runtime...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" -FrameworkDependent %*
set "buildExitCode=%ERRORLEVEL%"

if not "%buildExitCode%"=="0" echo Build failed with exit code %buildExitCode%.
exit /b %buildExitCode%

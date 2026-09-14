@echo off
setlocal EnableExtensions
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"
set "buildExitCode=%ERRORLEVEL%"
pause
exit /b %buildExitCode%

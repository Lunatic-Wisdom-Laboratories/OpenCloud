@echo off
rem Builds the Windows port: build.bat [windows-x64-release^|windows-x64]. Release is the default.
setlocal
where pwsh >nul 2>&1 || (echo build.bat needs PowerShell 7 ^(pwsh^) on PATH & exit /b 1)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\windows\opencloud.ps1" build %*
exit /b %errorlevel%

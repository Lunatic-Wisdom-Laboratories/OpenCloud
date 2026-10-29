@echo off
rem Builds and runs the Windows port: run.bat [windows-x64-release^|windows-x64] [game arguments].
setlocal
where pwsh >nul 2>&1 || (echo run.bat needs PowerShell 7 ^(pwsh^) on PATH & exit /b 1)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\windows\opencloud.ps1" run %*
exit /b %errorlevel%

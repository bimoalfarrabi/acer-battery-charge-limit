@echo off
rem One-time (re-runnable) installer for battctl. Will prompt for UAC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0battctl-install.ps1" %*
exit /b %ERRORLEVEL%

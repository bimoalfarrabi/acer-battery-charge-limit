@echo off
rem battctl - control the Acer battery charge limit (80%) from any shell.
rem Usage:  battctl [status|on|off|toggle]     (default: status)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0battctl.ps1" %*
exit /b %ERRORLEVEL%

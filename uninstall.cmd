@echo off
rem Removes battctl (task + files). Will prompt for UAC.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
exit /b %ERRORLEVEL%

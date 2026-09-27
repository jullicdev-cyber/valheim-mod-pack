@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update-Windows.ps1" %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%

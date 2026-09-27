@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Set-GamePath.ps1" %*
set "RESULT=%ERRORLEVEL%"
pause
exit /b %RESULT%

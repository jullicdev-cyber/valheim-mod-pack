@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Backup-Windows.ps1" %*
set "BACKUP_RESULT=%ERRORLEVEL%"
pause
exit /b %BACKUP_RESULT%

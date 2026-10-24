@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo. & echo [build.cmd] build.ps1 failed, exit code %RC%.
rem Pause only when launched by double-click (CMDCMDLINE then contains this file name).
echo %CMDCMDLINE% | find /i "%~f0" >nul && pause
exit /b %RC%

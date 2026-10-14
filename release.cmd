@echo off
rem Builds a release: installer, portable zip, update feed (artifacts\<version>). Double-click to run.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release.ps1 %*
if errorlevel 1 (pause & exit /b 1)
pause

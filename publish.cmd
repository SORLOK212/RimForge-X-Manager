@echo off
rem Builds RimForge.exe into the "publish" folder. Double-click to run.
cd /d "%~dp0"
dotnet publish src\RimForge.App -c Release -o publish
if errorlevel 1 (pause & exit /b 1)
echo.
echo Done: %~dp0publish\RimForge.exe
pause

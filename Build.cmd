@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
if errorlevel 1 (echo Build failed.) else (echo TaskHarbor is ready in the dist folder.)
pause

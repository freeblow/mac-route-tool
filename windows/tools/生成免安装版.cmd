@echo off
rem SRM: build a .NET-free single-file SRM.exe (first run downloads ~90 MB runtime pack)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Standalone.ps1" %*
echo.
pause
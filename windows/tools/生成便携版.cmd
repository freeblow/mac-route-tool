@echo off
rem SRM: build the "portable" folder (bundles the .NET runtime already installed on this PC; no internet needed)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-PortableFromLocalRuntime.ps1" %*
echo.
pause
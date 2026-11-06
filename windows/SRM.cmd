@echo off
setlocal
start "SRM" powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0SRM.ps1"

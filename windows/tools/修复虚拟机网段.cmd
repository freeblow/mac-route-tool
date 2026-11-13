@echo off
rem SRM: change a conflicting VMware virtual subnet (needs admin; will prompt UAC)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-VMnet-Subnet.ps1" %*

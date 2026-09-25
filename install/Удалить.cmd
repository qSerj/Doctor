@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Doctor.ps1" -Uninstall %* & exit /b

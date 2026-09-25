@echo off
chcp 65001 >nul
where pwsh.exe >nul 2>nul || (echo Нужен PowerShell 7.6 или новее ^(pwsh^). Установите его и запустите снова. & pause & exit /b 2)
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Doctor.ps1" %*

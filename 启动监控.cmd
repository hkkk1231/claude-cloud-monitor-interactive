@echo off
cd /d "%~dp0"
if not exist config.json (powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Configure.ps1" & pause & exit /b)
start "" "%~dp0ClaudeMonitor.exe"

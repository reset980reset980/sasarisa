@echo off
setlocal
cd /d "%~dp0"
if not exist "app\VoiceAssistant.Desktop\bin\Release\net10.0-windows\VoiceAssistant.exe" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1"
  if errorlevel 1 exit /b 1
)
start "" "%~dp0app\VoiceAssistant.Desktop\bin\Release\net10.0-windows\VoiceAssistant.exe" %*

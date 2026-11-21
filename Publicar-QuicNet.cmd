@echo off
cd /d "%~dp0"
pwsh -NoProfile -File "%~dp0scripts\release.ps1"
if errorlevel 1 (echo. & echo La publicacion se ha detenido. Revisa el mensaje anterior.) else (echo. & echo Publicacion terminada.)
pause

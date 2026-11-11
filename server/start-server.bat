@echo off
rem Big Ambitions server console (no game window, no sound).
rem Steam must be running and signed in with an account that owns the game.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0server.ps1" %*
pause

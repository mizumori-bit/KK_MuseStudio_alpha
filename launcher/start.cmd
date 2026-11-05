@echo off
rem MetaMuse Stage: one-click start (Irodori check/start, bridge, game window, Muse panel, then the resident panel manager).
cd /d "%~dp0"
python -B panel_manager.py %*

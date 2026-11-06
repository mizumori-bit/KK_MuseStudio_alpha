@echo off
rem MuseStudio: one-click start (bridge, MuseStudio, Muse panel, then the resident panel manager; Irodori only when the voice is on).
cd /d "%~dp0"
python -B panel_manager.py %*

@echo off
chcp 65001 >nul
cd /d "%~dp0"
python batch_flash_ui.py
if errorlevel 1 pause

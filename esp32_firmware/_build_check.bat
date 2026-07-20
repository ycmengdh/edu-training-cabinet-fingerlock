@echo off
set PIO=%USERPROFILE%\.platformio\penv\Scripts\pio.exe
"%PIO%" run -d "%~dp0cabinet_node" > "%~dp0cabinet_build.log" 2>&1
echo CABINET_EXIT=%ERRORLEVEL% >> "%~dp0cabinet_build.log"
"%PIO%" run -d "%~dp0root_node" > "%~dp0root_build.log" 2>&1
echo ROOT_EXIT=%ERRORLEVEL% >> "%~dp0root_build.log"
echo DONE > "%~dp0_build_done.flag"

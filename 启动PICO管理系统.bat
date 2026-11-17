@echo off
title PICO VR System Launcher
cd /d "%~dp0"

echo ====================================================================
echo        PICO VR Management System (Desktop Client + Server)
echo ====================================================================
echo.

set "SERVER_DIR=%~dp0PicoVR_Server_Deploy\server"
set "SERVER_EXE=%SERVER_DIR%\PicoManager.Server.exe"

echo [1/2] Starting PICO Core Service in dedicated console...
if exist "%SERVER_EXE%" (
    pushd "%SERVER_DIR%"
    start "PICO VR Server Console [Port 8999]" "%SERVER_EXE%"
    popd
)

echo [2/2] Launching PICO VR Desktop Client...
set "DESKTOP_EXE=%~dp0PicoVR_??????.exe"
if not exist "%DESKTOP_EXE%" (
    set "DESKTOP_EXE=%~dp0src-tauri\target\release\pico-manager-desktop.exe"
)

if exist "%DESKTOP_EXE%" (
    start "" "%DESKTOP_EXE%"
) else (
    start "" "http://localhost:8999"
)

echo.
echo ====================================================================
echo  System launched successfully!
echo  - Server Console : Running in separate window (close it to stop server)
echo  - Desktop App    : Holographic Glassmorphism UI active
echo  - One-click Stop : Double click [??????.bat]
echo ====================================================================
ping 127.0.0.1 -n 3 >nul

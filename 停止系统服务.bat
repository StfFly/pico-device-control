@echo off
title Stop PICO VR Services
cd /d "%~dp0"

echo ====================================================================
echo        Stopping all PICO VR Services and Desktop App...
echo ====================================================================
echo.

taskkill /F /IM "PicoManager.Server.exe" 2>nul
if %ERRORLEVEL% equ 0 (
    echo [OK] PICO Server [PicoManager.Server.exe] has been stopped.
) else (
    echo [Notice] PICO Server was not running.
)

taskkill /F /IM "pico-manager-desktop.exe" 2>nul
taskkill /F /IM "PicoVR_??????.exe" 2>nul
taskkill /F /IM "PicoVR_?????.exe" 2>nul
echo [OK] Desktop client processes cleaned up.

echo.
echo ====================================================================
echo  All PICO services stopped. Ports 8999 and 8998 are now free.
echo ====================================================================
ping 127.0.0.1 -n 3 >nul

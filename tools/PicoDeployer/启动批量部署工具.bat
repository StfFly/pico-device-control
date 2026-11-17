@echo off
title PicoDeployer - PICO VR Batch Deploy Tool
echo ====================================================================
echo        PICO VR Device Batch Deploy Tool (PicoDeployer)
echo ====================================================================
echo.
echo Starting PicoDeployer Web Service on http://localhost:9005 ...
echo.

cd /d "%~dp0"

start "" "http://localhost:9005"
PicoDeployer.exe

pause

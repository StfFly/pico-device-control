@echo off
chcp 65001 >nul
title PICO Business Suite 服务模式一键配置工具

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\Set-PicoServiceMode.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo 执行过程中发生提示，按任意键退出...
    pause >nul
)
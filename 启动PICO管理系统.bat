@echo off
title PICO VR 主控日常播控管理系统 (PicoManager)
cd /d "%~dp0"

echo ====================================================================
echo        PICO VR 门店统一设备管理控制系统 (PicoManager)
echo ====================================================================
echo.

set "SERVER_DIR=%~dp0PicoVR_Server_Deploy\server"
set "SERVER_EXE=%SERVER_DIR%\PicoManager.Server.exe"

echo [1/2] 正在启动 PICO 核心后台控制服务...
if exist "%SERVER_EXE%" (
    pushd "%SERVER_DIR%"
    start "PICO VR 核心服务控制台 [端口 8999]" "%SERVER_EXE%"
    popd
)

echo [2/2] 正在唤起客户端界面...
set "DESKTOP_EXE=%~dp0PicoVR_桌面客户端.exe"
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
echo  系统启动指令已发出！
echo  - 服务控制台: 后台窗口常驻运行 (支持原生 SDK 设备秒级发现与控制)
echo  - 运营工作台: 浏览器 / 桌面客户端已打开
echo  - 一键停止服务: 双击运行 [停止系统服务.bat]
echo ====================================================================
ping 127.0.0.1 -n 3 >nul

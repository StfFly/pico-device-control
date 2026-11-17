@echo off
title PICO VR 桌面客户端一键编译脚本
cd /d "%~dp0"

echo ====================================================================
echo  正在编译 PICO VR 全息控制中心桌面端 (Tauri v2 + Rust Release)...
echo ====================================================================
echo.

call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "PATH=C:\Users\jack\.rustup\toolchains\stable-x86_64-pc-windows-msvc\bin;C:\Users\jack\.cargo\bin;C:\Users\jack\.node22;%PATH%"

cd /d "%~dp0src-tauri"
cargo build --release

if %ERRORLEVEL% neq 0 (
    echo.
    echo [错误] 编译失败，请检查上方日志。
    pause
    exit /b 1
)

copy /y "%~dp0src-tauri\target\release\pico-manager-desktop.exe" "%~dp0PicoVR_全息控制中心.exe" >nul
copy /y "%~dp0src-tauri\target\release\pico-manager-desktop.exe" "%~dp0PicoVR_Server_Deploy\PicoVR_桌面客户端.exe" >nul

echo.
echo ====================================================================
echo [成功] Release 桌面端程序已生成！
echo 主运行程序: %~dp0PicoVR_全息控制中心.exe
echo 部署端程序: %~dp0PicoVR_Server_Deploy\PicoVR_桌面客户端.exe
echo ====================================================================
pause
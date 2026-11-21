# PICO VR 场馆运营与多设备统一管理控制中心 (PicoManager)

PicoManager 是面向 PICO VR 场馆运营场景的 Windows 一体化桌面管理系统，已基于 **Rust + Tauri v2 + Axum** 完成全栈原生重构，单可执行文件内置完整后台服务与现代控制界面，无需 `.bat` 脚本、无需安装 .NET 运行时，双击即用。

当前版本：**v2.0.0（Rust 原生一体版）**

## 核心特性

- **一体化单进程架构**：`PicoManager.exe` 同进程内嵌桌面客户端（WebView2 + 系统托盘）、HTTP/WebSocket 后台服务（默认 `8999/TCP`）、UDP 外部中控服务（默认 `8998/UDP`）与原生 PICO SDK 通信层。
- **原生 FFI 对接 PICO SDK**：通过 Rust `libloading` 动态加载 `PXREAClientSDK.dll`，实现设备秒级发现、自动锁定、电量/手柄/前台应用遥测与播控指令下发。
- **实时画面监看与链路诊断**：无阻塞有界队列解耦 SDK 回调与纯 Rust JPEG 转码，支持按需 MJPEG/帧快照拉流，内置 `PICOBusinessSuite` 进程与 `60061` gRPC 端口健康诊断与一键重连。
- **统一异步无线 ADB 运维**：全网段并发扫描连接（TCP `5555`）、大文件/APK 批量上传及本地直推安装（含大包两阶段兜底与 Zip-Slip 安全校验）、应用卸载与目录管理。
- **局域网平板联动**：启动 `PicoManager.exe` 后自动在 `0.0.0.0:8999` 提供相同内嵌前端，场馆内平板/手机浏览器可直接访问联动控制。
- **设备分组与项目配置**：支持设备分组分区播控、头显别名绑定、UDP 机号默认游戏映射、PICO Business Suite 服务模式一键开关与原子化配置落盘。

## 快速使用（无需 .bat 脚本）

编译整理后的发布产物位于工作区 [`Build/`](Build/) 目录：

1. 启动并登录 **PICO Business Suite**（确保开启服务模式）。
2. 直接双击运行 [`Build/PicoManager.exe`](Build/PicoManager.exe)。
3. 如需使用平板协同，可在局域网浏览器访问 `http://<本机局域网IP>:8999`。
4. 关闭主窗口会自动最小化至系统托盘继续保持后台播控与 UDP 监听；右键托盘图标可选择「彻底退出 PicoManager」。

> 首次运行前请确保 Windows 防火墙允许 `PicoManager.exe` 访问专用网络，且 `8999/TCP` 与 `8998/UDP` 未被占用。

## 从源码构建

### 前置依赖

- Rust 工具链（`rustc` / `cargo`）
- Windows MSVC 构建工具链（Visual Studio Build Tools / Enterprise）

### 一键构建发布产物到 `Build/` 目录

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

构建完成后将在 [`Build/`](Build/) 目录自动整理生成：

- `PicoManager.exe`（一体化桌面客户端 + 内嵌后台 + 静态 UI）
- `PXREAClientSDK.dll` / `PBXRPanoramaUnity_x64.dll`（PICO 原生 SDK 动态库）
- `config.json`（运行配置文件）
- `README.txt`（现场使用说明）

## 技术栈

- **桌面端与后台核心**：Rust + Tauri v2 + Axum 0.8 + Tokio + WebSocket + `rust-embed`
- **硬件原生接入**：PICO Enterprise Suite C SDK (`PXREAClientSDK.dll` FFI)
- **视频帧转码与安全解压**：Pure Rust `image` (JPEG) + `zip` (Zip-Slip 防护)
- **统一前端**：单一事实来源 [`src-tauri/ui/`](src-tauri/ui/)（原生 JS + Tailwind + 原生 WebSocket）

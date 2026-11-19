# PICO VR 场馆运营与多设备统一管理控制中心

PicoManager 是面向 PICO VR 场馆运营场景的 Windows 管理系统，用于统一发现、监看和控制局域网内的多台 PICO 设备。

当前版本：**v2.0.0**

## 核心功能

- 基于 PICO Enterprise Suite SDK 发现和控制设备
- 设备分组、项目绑定及批量操作
- 批量启动/停止应用、调节音量、亮屏/熄屏、重启和关机
- 实时画面监看、视频流状态检测与故障诊断
- 无线 ADB 扫描、连接、APK 安装和文件推送
- OTA 更新与部署包管理
- Tauri 桌面客户端和浏览器管理台

## 运行环境

- Windows 10/11 x64
- PICO Business Suite，并启用服务模式
- 管理电脑与 PICO 设备位于同一局域网
- 默认 HTTP 端口：`8999`
- 默认 UDP 端口：`8998`

## 快速开始

1. 从 [Releases](https://github.com/StfFly/pico-device-control/releases) 下载 `PicoVR_Server_Deploy.zip`。
2. 将压缩包完整解压到本地目录。
3. 启动 PICO Business Suite，并开启服务模式。
4. 运行部署包内的启动脚本，或直接运行 `PicoManager.Server.exe`。
5. 在浏览器访问 [http://localhost:8999](http://localhost:8999)。

> 首次使用前，请确认 Windows 防火墙允许程序访问专用网络，并确保 `8999/TCP` 和 `8998/UDP` 未被其他程序占用。

## 配置

主要运行参数位于 [`config.json`](config.json)，包括 Web/UDP 端口、设备别名、默认项目和项目列表。修改后请重启服务使配置生效。

建议在正式部署前根据现场网络和设备规划调整配置，并妥善备份现有配置文件。

## 从源码构建

### 前置依赖

- .NET 10 SDK
- Node.js 与 npm
- Rust 工具链
- Tauri 2 所需的 Windows 构建依赖

### 服务端

```powershell
dotnet build src\PicoManager.Server\PicoManager.Server.csproj -c Release
```

### 部署工具

```powershell
dotnet build tools\PicoDeployer\PicoDeployer.csproj -c Release
```

### Tauri 桌面端

```powershell
npm install
npm run build
```

## 技术栈

- .NET 10 / ASP.NET Core
- Tauri 2 / Rust
- PICO Enterprise Suite 原生 SDK
- SignalR、UDP 与 ADB

## 项目文档

- [开发与架构设计文档](PicoVR统一管理系统_开发与架构设计文档.md)
- [部署与运维手册](PicoVR统一管理系统_部署与运维手册.md)
- [设备文件传输与 APK 批量安装技术方案](PICO设备文件传输与APK批量安装技术方案.md)
- [Tauri v2 与 PicoVR 优化部署手册](Tauri_v2_与_PicoVR优化部署手册.md)

## 注意事项

- 当前发行包仅面向 Windows x64。
- 设备控制能力取决于 PICO 设备型号、系统版本、网络环境及 PICO Business Suite 配置。
- 建议先在测试设备和隔离网络中验证批量控制、APK 部署及 OTA 更新流程，再用于正式运营环境。


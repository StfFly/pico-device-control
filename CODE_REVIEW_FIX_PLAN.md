# Pico 硬件管理系统 — 代码修复计划文档
> 供“反重力”或后续开发者直接执行修改的结构化任务清单。
> **所有变更必须在单独分支/PR 中完成，严禁直接推主分支。**

---

## 📌 约定
- **文件路径**均相对仓库根目录 `F:\Sun\AIProgram\Pico设备控制\`
- **行号**基于当前快照（审查时刻），实际编辑时请以最新代码为准
- **优先级**：`P0`=立即修复（安全/功能阻断），`P1`=本迭代修复，`P2`=技术债清理
- **验收标准**：每条任务给出“验收方式”（单测/手工/自动化脚本）

---

## 🔴 P0 — 安全与核心功能阻断（必须优先合入）

### P0-1 统一鉴权中间件（HTTP / SignalR / UDP / Deployer）
| 目标 | 文件 | 修改点 |
|------|------|--------|
| **C# Server** | `src/PicoManager.Server/Program.cs` | 1. 新增 `ApiKeyMiddleware`（读取 `config.json` 的 `ApiKey`，校验 `X-Api-Key` Header）<br>2. `app.UseMiddleware<ApiKeyMiddleware>()` 在 `UseRouting()` 前<br>3. SignalR: `app.MapHub<PicoHub>("/hubs/pico").RequireAuthorization()` + 自定义 `AuthorizeAttribute` 读 Header |
| **C# Deployer** | `tools/PicoDeployer/Program.cs` | 同理加中间件，复用同一 `ApiKey` 配置项 |
| **UDP 8998** | `src/PicoManager.Server/Services/UdpCommandService.cs` | `ReceiveAsync` 后立即校验 `remoteEndPoint.Address` 在白名单（`config.json` 新增 `UdpAllowedIPs: string[]`）+ 令牌桶限流（每 IP 10 req/s） |
| **Rust Tauri** | `src-tauri/src/commands/server.rs` | `api_proxy` 自动在转发请求头里加上 `X-Api-Key`（从本地配置读取） |
| **前端** | `src-tauri/ui/app.js` | `apiCall` / `initSignalR` 统一在 Header 里带上 `X-Api-Key`（从 `systemConfig.apiKey` 读） |
| **配置模板** | `src/PicoManager.Server/config.json` `tools/PicoDeployer/config.json` | 新增字段 `"ApiKey": "换成强随机串"`, `"UdpAllowedIPs": ["192.168.1.0/24", "10.0.0.0/8"]` |

**验收**：无 Key 请求全部返回 401；UDP 非白名单 IP 报文被丢弃；前端正常操作无感知。

---

### P0-2 对齐 3 个 ADB 命令参数（恢复无线 ADB 功能）
| 命令 | Rust 现状 | 前端调用 | 修改方案 |
|------|-----------|----------|----------|
| `adb_connect` | `adb_connect(ip: String, port: Option<u16>)` | `invoke('adb_connect', { ipAddress: "192.168.1.50:5555" })` | **Rust 改签名**：`adb_connect(ip_address: String)` 兼容 `ip:port` 格式，内部自动拆分；去掉 `port` 参数 |
| `adb_disconnect` | `adb_disconnect(ip: String, port: Option<u16>)` | `invoke('adb_disconnect', { serial: "192.168.1.50:5555" })` | **Rust 改签名**：`adb_disconnect(serial: String)` 直接传给 `adb disconnect <serial>` |
| `adb_install_apk` | `adb_install_apk(target: String, apk_path: String)` | `invoke('adb_install_apk', { apkPath: "...", serial: null })` | **Rust 新增重载/默认参数**：`adb_install_apk(apk_path: String, target: Option<String>)`；`target=None` 时遍历 `adb devices` 逐个安装（或前端改为选中设备列表逐个调用） |

**文件**：`src-tauri/src/commands/adb.rs`（签名+实现） + `src-tauri/ui/app.js`（调用处 3 处）  
**验收**：前端“无线 ADB 弹窗”连接/断开/批量安装全部成功，控制台无 `invalid args`。

---

### P0-3 严格 CSP + 关闭 `withGlobalTauri` + 前端去 `innerHTML`
| 文件 | 修改点 |
|------|--------|
| `src-tauri/tauri.conf.json` | `"csp": "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' http://127.0.0.1:8999 ws://127.0.0.1:8999; object-src 'none'; base-uri 'none'; form-action 'none';"`<br>`"withGlobalTauri": false` |
| `src-tauri/ui/app.js` | 1. `showToast`、`renderMatrix`、`openLanModal`、`refreshAdbDevices` 等所有 `innerHTML`/`onclick=` 拼接处 → 改用 `document.createElement` + `textContent` + `addEventListener`<br>2. `dev.sn`/`dev.alias`/`url` 等不可信数据**严禁**拼入 HTML 属性或脚本 |
| `src-tauri/src/commands/adb.rs` | **`adb_shell` 彻底移除或加白名单**（见 P0-4） |

**验收**：浏览器开发者工具 Console 无 CSP 报错；注入 `<img src=x onerror=...>` 的设备别名不再执行脚本。

---

### P0-4 `adb_shell` 命令加白名单 / 彻底下线
| 方案 | 修改点 |
|------|--------|
| **方案 A（推荐）彻底移除** | 1. `src-tauri/src/commands/adb.rs` 删除 `adb_shell` 函数及 `tauri::generate_handler!` 注册<br>2. `src-tauri/ui/app.js` 删除对应调用（搜索 `adb_shell`） |
| **方案 B 仅保留只读诊断白名单** | 1. Rust 端新增 `ALLOWED_SHELL_CMDS = ["getprop", "dumpsys battery", "pm list packages"]`<br>2. `adb_shell` 入参 `command` 必须以白名单开头，否则返回 `Err("command not allowed")`<br>3. 前端调用前弹二次确认弹窗 |

**验收**：任意命令注入尝试被拦截；合法诊断命令仍可用。

---

### P0-5 任意本地文件外传漏洞修复（Deployer `/api/deploy/push-file`）
| 文件 | 修改点 |
|------|--------|
| `tools/PicoDeployer/Program.cs` | 1. `LocalFilePath` **禁止**绝对路径，仅允许相对 `wwwroot/uploads/` 下的文件名<br>2. 新增 `UploadsRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "uploads")`，规范化后 `Path.GetFullPath` 必须以 `UploadsRoot` 开头<br>3. 返回 400 “路径越界”而非把服务器任意文件推到头显 |

**验收**：尝试 `LocalFilePath: "../../../config.json"` 返回 400；合法上传文件正常推送。

---

### P0-6 UDP 8998 来源 IP 白名单 + 速率限制
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/UdpCommandService.cs` | 1. 启动时读取 `config.json:UdpAllowedIPs` 解析 CIDR 列表<br>2. `ReceiveAsync` 后立即 `if (!IsAllowed(remoteEP.Address)) return;`<br>3. 每 IP `ConcurrentDictionary<IPAddress, TokenBucket>` 令牌桶（10 req/s, burst 20） |
| `src/PicoManager.Server/Controllers/ConfigController.cs` | 新增 `UdpAllowedIPs` 配置项的 CRUD |

**验收**：非白名单 IP 发送 UDP 报文被静默丢弃；单 IP 超频返回 `rate_limited` 错误码。

---

## 🟠 P1 — 稳定性与并发正确性（本迭代完成）

### P1-1 Rust async 命令去同步阻塞
| 命令 | 现状 | 修改 |
|------|------|------|
| 全部 ADB 命令 | `Command::new("adb").output()` | `tokio::process::Command::new("adb").args(...).output().await` |
| `start_server_process` / `stop_server_process` | `std::process::Command::spawn()` / `taskkill` | `tokio::process::Command` + `spawn()` 保存 `Child` 句柄 |
| `send_udp_command` | `std::net::UdpSocket` 同步 | `tokio::net::UdpSocket` |
| `load_config_json` / `save_config_json` | `fs::read_to_string` / `fs::write` | `tokio::fs::read_to_string` / `tokio::fs::write`（并改为 **原子写：temp + rename**） |

**文件**：`src-tauri/src/commands/adb.rs` `server.rs` `network.rs` `config.rs`  
**验收**：`cargo test` 通过；并发 20 个 `adb_list_devices` 不再卡死 UI。

---

### P1-2 `start_server_process` 互斥 + Child 句柄保存 + 精准停止
| 文件 | 修改点 |
|------|--------|
| `src-tauri/src/commands/server.rs` | 1. 新增 `static SERVER_CHILD: Mutex<Option<Child>> = Mutex::new(None);`<br>2. `start_server_process` 加 `Mutex` 锁，若已有 `Child` 且 `try_wait().is_none()` 直接返回“已运行”<br>3. `spawn()` 成功后 `SERVER_CHILD.lock().unwrap().replace(child)`<br>4. `stop_server_process` 取出 `Child` 调用 `child.kill().await`，失败再 fallback `taskkill`<br>5. 移除 `SERVER_RUNNING` AtomicBool（不再需要） |

**验收**：快速点击“启动服务”/托盘“重启”后台服务”不再产生多进程；停止服务精准杀掉唯一子进程。

---

### P1-3 `api_proxy` 端口动态化 + Method 白名单 + 错误不吞
| 文件 | 修改点 |
|------|--------|
| `src-tauri/src/commands/server.rs` | 1. 新增参数 `port: Option<u16>`，默认读取配置或 8999<br>2. `method` 白名单：`["GET","POST","PUT","DELETE","PATCH"]`，其它返回 400<br>3. `res.text().await` 改为 `match res.text().await { Ok(t) => t, Err(e) => return Err(format!("read body: {e}")) }` |
| `src-tauri/ui/app.js` | `apiCall` 调用 `api_proxy` 时透传 `port: systemConfig.webPort` |

**验收**：修改配置端口后代理自动生效；非法 method 被拦截；网络读取错误被正确返回。

---

### P1-4 补全 Tauri `capabilities/` 目录
| 文件 | 内容 |
|------|------|
| `src-tauri/capabilities/default.json` | ```json<br>{<br>  "$schema": "https://schema.tauri.app/config/2/capability",<br>  "identifier": "default",<br>  "description": "默认权限集",<br>  "windows": ["main"],<br>  "permissions": [<br>    "core:default",<br>    "core:window:allow-close",<br>    "core:window:allow-show",<br>    "core:window:allow-hide",<br>    "core:window:allow-set-focus",<br>    "core:tray:allow-icon",<br>    "core:tray:allow-menu",<br>    "core:tray:allow-tooltip",<br>    "opener:default"<br>  ]<br>}<br>``` |

**验收**：`tauri dev` 启动无权限报错；后续若需 `fs`/`dialog` 等再按需增。

---

### P1-5 配置读写路径稳健化 + 原子写
| 文件 | 修改点 |
|------|--------|
| `src-tauri/src/commands/config.rs` | 1. `get_config_path()` 优先使用 `tauri::path::BaseDirectory::AppConfig`（`app_handle.path().app_config_dir()`）<br>2. `save_config_json`：写入 `config.json.tmp` → `fs::rename(tmp, target)` 原子替换<br>3. 移除硬编码 `F:\Sun\...` 候选路径 |

**验收**：便携版/安装版/开发模式下配置文件位置一致；断电中途不损坏配置。

---

### P1-6 C# UDP 自检 `ReceiveAsync` 竞态修复
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Controllers/ConfigController.cs` | `TestUdpMessage`：<br>```csharp<br>var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));<br>var receiveTask = client.ReceiveAsync(cts.Token);<br>if (await Task.WhenAny(receiveTask, Task.Delay(Timeout.Infinite, cts.Token)) == receiveTask)<br>{ var result = receiveTask.Result; ... }<br>else { return "timeout"; }<br>```<br>移除 `using var client` 包裹的 `ReceiveTimeout` 无效模式。 |

**验收**：并发 10 次“测试 UDP”不再抛 `ObjectDisposedException`。

---

### P1-7 `PicoSdkService.StopAsync` 等待遥测任务退出
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/PicoSdkService.cs` | ```csharp<br>public async Task StopAsync(CancellationToken cancellationToken)<br>{<br>    _cts?.Cancel();<br>    if (_telemetryTask != null) {<br>        try { await _telemetryTask; } catch { }<br>    }<br>    PXREAOpenApi.Deinit();<br>}<br>```<br>同时 `_telemetryTask` 内部循环 `while (!_cts.Token.IsCancellationRequested)` 配合 `await Task.Delay(100, _cts.Token)`。 |

**验收**：应用关闭时日志无 native 崩溃；`Deinit` 后无回调触发。

---

### P1-8 `DeploymentEngine` 并发互斥
| 文件 | 修改点 |
|------|--------|
| `tools/PicoDeployer/Core/DeploymentEngine.cs` | 1. `private readonly SemaphoreSlim _gate = new(1, 1);`<br>2. 每个 `RunBatchXxxAsync` 开头 `if (!await _gate.WaitAsync(0)) throw new InvalidOperationException("另一批量任务正在进行");`<br>3. `finally { _gate.Release(); }` |

**验收**：并发调用 `/api/deploy/install` + `/api/deploy/push-file` 其中一个返回 409。

---

### P1-9 屏幕帧转换移出回调线程 + 限频前置
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/ScreenMonitorService.cs` | `HandleRawFrame`：<br>1. 先 `if (_lastFrameTime + 45ms > now) return;` **限频前置**<br>2. `byte[] copy = new byte[len]; Marshal.Copy(ptr, copy, 0, len);` 拷贝指针数据<br>3. `Task.Run(() => { var jpeg = ScreenConverter.BgraToJpeg(copy, w, h); UpdateFrame(sn, jpeg); });` 交给线程池 |
| `src/PicoManager.Server/Services/ScreenConverter.cs` | `BgraToJpeg` 接受 `byte[]` 而非 `IntPtr`，去除 unsafe 指针运算。 |

**验收**：高帧率下 SDK 回调线程无阻塞；CPU 占用平滑。

---

### P1-10 移除“伪唤醒”测试逻辑
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/PicoDeviceManager.cs` | 删除 `Reboot`/`Shutdown` 中 `if (!_sdkActive || sn.StartsWith("P4E-88"))` 后的 `Task.Run` 延时伪造在线代码。保留真实 SDK 调用与 `MarkDeviceOffline`。 |

**验收**：重启/关机指令下发后，设备状态保持“离线”直到真实心跳上报。

---

### P1-11 配置对象并发保护
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/ConfigService.cs` | 1. `private readonly ReaderWriterLockSlim _lock = new();`<br>2. `CurrentConfig` 访问全部 `EnterReadLock/ExitReadLock` / `EnterWriteLock/ExitWriteLock`<br>3. `SaveConfig` 写锁 + 原子写（temp+rename） |
| `src/PicoManager.Server/Controllers/ConfigController.cs` | `Projects`/`HeadsetAliases` 增删改全部走 `ConfigService` 方法，不直接操作集合。 |
| `src/PicoManager.Server/Services/PicoDeviceManager.cs` | `UnbindHeadset` 调用 `ConfigService.RemoveAlias(sn)` 而非直接 `Remove`。 |

**验收**：并发 100 次修改配置 + SDK 回调同时写 alias，无集合异常、无脏写。

---

### P1-12 ADB 命令拼接转义 / 白名单
| 文件 | 修改点 |
|------|--------|
| `tools/PicoDeployer/Core/AdbManager.cs` | 1. `Connect`/`Install`/`Shell` 等所有拼接参数处：`ArgumentEscaper.Escape(arg)`（自行实现：包含空格/引号/分号/`$()` 时用双引号包裹并转义内部引号）<br>2. `packageName`/`apkPath` 等做正则校验 `^[a-zA-Z0-9._-]+$` / `^[a-zA-Z]:\\.*\.apk$` |
| `tools/PicoDeployer/Program.cs` | `shell "cat '{safePath}'"` → 先校验 `safePath` 无单引号/反引号/`$()`，再拼接。 |

**验收**：恶意输入 `"; rm -rf /sdcard; "` 被拒绝或安全转义。

---

### P1-13 全链路 HTTPS（自签证书 + 强制重定向）
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Program.cs` | 1. 开发证书：`dotnet dev-certs https --trust`<br>2. `builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Any, port, lo => lo.UseHttps()); o.Listen(IPAddress.Any, 8998, lo => lo.UseHttps()); })`<br>3. `app.UseHttpsRedirection();` |
| `tools/PicoDeployer/Program.cs` | 同理配置 HTTPS 端口（建议 8997） |
| `src-tauri/src/commands/network.rs` `server.rs` | `api_proxy` / `get_host_network_info` 返回的 `web_url` 改为 `https://` |
| `src-tauri/ui/app.js` | `initSignalR` `withUrl('https://...')`、所有 `fetch`/`apiCall` 改 `https://` |

**验收**：浏览器地址栏显示 🔒；抓包不再见明文指令。

---

## 🟡 P2 — 技术债与体验优化（后续迭代）

| 编号 | 组件 | 任务描述 | 文件 | 验收 |
|------|------|----------|------|------|
| P2-1 | 全链路 | 统一日志：Rust 初始化 `tracing_subscriber::fmt::init()`；C# 统一 `ILogger`，移除所有空 `catch {}` | `src-tauri/src/lib.rs` `main.rs` `Cargo.toml` + 所有 `.cs` | 启动即有结构化日志；异常可追踪 |
| P2-2 | Rust | `reqwest::Client` 单例化（`lazy_static!` 或 `OnceCell`） | `src-tauri/src/commands/server.rs` `network.rs` | 连接复用，减少握手开销 |
| P2-3 | C# | `SignalR SendAsync` 全部 `await` 或 `ContinueWith` 记录异常 | `PicoDeviceManager.cs` 8 处 | 异常出现在日志而非丢失 |
| P2-4 | C# | 伪唤醒 `Task.Run` 包 `try-catch` 记录异常 | `PicoDeviceManager.cs:514,536` | 后台任务异常可观测 |
| P2-5 | C# | UDP 停止时优雅关闭：`_udpClient?.Close()` 前 `await Task.WhenAll(_pendingTasks)` | `UdpCommandService.cs` | 关闭无 `ObjectDisposedException` |
| P2-6 | 全链路 | 硬编码外置：端口、路径、默认项目、示例 SN/IP 全迁入 `config.json` / `appsettings.json` | 所有 `.cs` `.rs` `app.js` | 换机器只改配置文件 |
| P2-7 | C# | `StartApp` 补在线检查（与 `StopApp` 对齐） | `PicoDeviceManager.cs:409` | 离线设备不再被误锁 |
| P2-8 | C# | 配置修改 UDP 端口/开关热生效：`IOptionsMonitor<UdpOptions>` + `UdpCommandService` 重新绑定 | `UdpCommandService.cs` `ConfigController.cs` | 修改配置后无需重启服务 |
| P2-9 | C# | 时间统一 UTC ISO8601（`yyyy-MM-ddTHH:mm:ssZ`） | `UdpCommandService.cs` `CreateAck` 等 | 前端不再有时区偏移 |
| P2-10 | C# | 流接口 `StartStream` 检查 SDK 返回值非 0 则返回失败；`StopMonitor` 仅在 `HasSubscribers==0` 时调用 | `StreamController.cs` | 多观众短暂断连不中断流 |
| P2-11 | C# | `StopPlayApp` 去魔法空格：定义 `const string DESKTOP = "系统桌面";` | `PicoDeviceManager.cs:446` | 语义清晰 |
| P2-12 | C# | `BgraToJpeg` 校验 `stride > 0`、支持负 stride（上下翻转） | `ScreenConverter.cs:87-94` | 兼容不同 SDK 版本 |
| P2-13 | C# | `_sdkActive` 改 `volatile` 或 `Interlocked` | `PicoDeviceManager.cs:20,37` | 可见性保证 |
| P2-14 | Rust | `lib.rs:24` `default_window_icon().unwrap_or_else(|| panic_with_friendly_msg())` | `src-tauri/src/lib.rs` | 图标缺失给友好提示而非闪退 |
| P2-15 | Rust | `tauri.conf.json` `"transparent": false`（或按平台动态决定） | `src-tauri/tauri.conf.json` | 兼容旧显卡/关闭 DWM 环境 |
| P2-16 | Rust | 清理死代码命令：前端未调用的 8 个命令要么补前端调用要么删除注册 | `src-tauri/src/commands/*.rs` `src-tauri/src/lib.rs` | `cargo build` 无未使用警告 |
| P2-17 | 前端 | `updateTopStats` 同步更新 `top-stat-online/running/offline`（现代 ID 不匹配） | `src-tauri/ui/app.js:397-400` `index.html:195-210` | 顶部统计数字实时正确 |
| P2-18 | 前端 | `stat-low` 电量告警数字在 HTML 缺失 ID → 补齐或移除 JS 写入 | `src-tauri/ui/app.js:399` `index.html` | 无 `Cannot set property of null` 报错 |
| P2-19 | 前端 | `applyGlobalVolume` 仅对在线设备下发（已做）但 UI 滑块联动 `oninput`/`onchange` 抖动 → 防抖 200ms | `src-tauri/ui/app.js:503-513` `index.html:398` | 拖动滑块不疯狂发请求 |
| P2-20 | 文档 | 更新 `PicoVR统一管理系统_开发与架构设计文档.md` 同步上述安全/架构变更 | 根目录 `.md` 文件 | 文档与代码一致 |

---

## ✅ 执行检查清单（交付前自查）

- [ ] 所有 P0 任务已合入并通过验收脚本
- [ ] `cargo test` / `dotnet test` 全绿
- [ ] 手工端到端测试：启动 Tauri → 托盘启动服务 → 前端连接 → 扫描设备 → 批量启动/暂停/停止 → 实时画面 → 无线 ADB 安装 → 修改配置 → 关闭服务 → 退出 Tauri
- [ ] 无 CSP 报错、无 401/403 意外、无控制台报错
- [ ] `config.json` 含 `ApiKey` `UdpAllowedIPs` 且未提交真实密钥（CI 用 secret 注入）
- [ ] 更新 `CHANGELOG.md` 记录破坏性变更（HTTPS 端口变更、API Key 引入、ADB 参数变更）

---

## 📎 附件：关键文件清单（供反重力批量打开）

```
src-tauri/
  tauri.conf.json
  Cargo.toml
  src/
    main.rs
    lib.rs
    commands/
      adb.rs
      server.rs
      network.rs
      config.rs
      mod.rs
  capabilities/
    default.json        (新建)
  ui/
    index.html
    app.js

src/PicoManager.Server/
  Program.cs
  Controllers/
    DevicesController.cs
    BatchController.cs
    StreamController.cs
    ConfigController.cs
    SystemController.cs
  Services/
    PicoDeviceManager.cs
    PicoSdkService.cs
    UdpCommandService.cs
    ScreenMonitorService.cs
    ScreenConverter.cs
    ConfigService.cs
    PicoServiceModeHelper.cs
  Hubs/
    PicoHub.cs
  Models/
    PicoDevice.cs
    SystemConfig.cs
  Native/
    PXREAOpenApi.cs
    NativeTypes.cs
  config.json           (需新增 ApiKey, UdpAllowedIPs)

tools/PicoDeployer/
  Program.cs
  Core/
    AdbManager.cs
    DeploymentEngine.cs
    DeviceRepository.cs
    ConfigTemplateEngine.cs
  Models/
    DeployModels.cs
  config.json           (需新增 ApiKey)
```

---

> **说明**：本文档已覆盖审查报告中所有高/中严重度问题及主要低严重度技术债。执行时建议按 **P0 → P1 → P2** 顺序分批提 PR，每批 CI 通过后再进下一批。如有歧义请回溯原始审查报告或直接阅读对应源码行。
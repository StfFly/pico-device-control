# PICO VR 门店统一设备管理系统 (PicoManager)
## 生产环境部署与现场运维手册

---

### 一、 系统架构与运行环境依赖清单 (官方下载直链)

系统已针对实际商业落地环境进行免安装封装，以下为本系统所必需与推荐的软硬件运行环境清单及官方下载地址：

### 1. 软件环境与官方组件下载直链

| 依赖组件名称 | 是否必需 | 作用与说明 | 官方获取 / 直链下载地址 |
| :--- | :--- | :--- | :--- |
| **PICO Business Suite (PC 端)** | **必需** | 头显局域网信令交互、设备加锁、SDK 调度与投屏的**核心底座软件**。 | [PICO 企业服务官方门户下载](https://business.picoxr.com/)<br/>*(开发者社区：[developer-cn.picoxr.com](https://developer-cn.picoxr.com/))* |
| **PICO VR 端企业套件客户端 (APK)** | **必需** | 运行在头显设备内部（Neo3 / PICO 4 / Ultra），用于响应后台加锁与游戏拉起。 | 由 PC 端 PICO Business Suite 连线自动推送安装，或在头显内应用商店获取 |
| **Microsoft Visual C++ 2015-2022 (x64)** | **必需** | `PXREAClientSDK.dll` (PICO 原生 C++ SDK) 及多媒体视频编解码底层运行库。 | [微软官方 VC++ 运行库直链下载 (vc_redist.x64.exe)](https://aka.ms/vs/17/release/vc_redist.x64.exe)<br/>*(套件安装目录下通常也自带备用)* |
| **.NET 10.0 / 8.0 SDK (开发选配)** | 可选 | 部署包已采用 Self-Contained 独立发布，**现场电脑无需安装**；若需修改源码二次开发需安装。 | [微软官方 .NET 下载专区](https://dotnet.microsoft.com/download) |

---

### 2. 宿主机硬件与操作系统要求 (中控电脑)
- **操作系统**：Windows 10 64位 (1809及以上版本) / Windows 11 64位 / Windows Server 2019+
- **CPU / 内存**：推荐 Intel Core i5 / AMD Ryzen 5 及以上处理器，8GB 及以上内存
- **网络接口**：必须配备千兆有线网卡（强烈建议中控电脑通过千兆网线直连商用 AP/路由器）

---

### 3. 局域网与无线网络配置要求 (重中之重)
- **Wi-Fi 路由器与频段**：建议使用 Wi-Fi 6 企业级或高性能商用路由器（如华硕、TP-Link 企业级 AP），为 PICO 头戴设备开设**独立 5GHz 频段 SSID**（将 2.4G 与 5G 分频，避免干扰）。
- **关闭 AP 隔离 (必做)**：路由器后台务必**关闭“AP 隔离” (AP Isolation / Client Isolation)**，确保中控电脑、VR 头盔、操作员 iPad/平板之间可以在局域网内相互广播和通信。
- **静态 IP 设置 (推荐)**：建议在路由器 DHCP 中将中控电脑的 MAC 地址与内网 IP 绑定（例如固定为 `192.168.1.100`），避免电脑重启后 IP 变化导致平板书签失效。
- **防火墙与通信端口放行**：
  - `TCP 8999`：PicoManager 前端 Web 控制台与 REST / SignalR 访问端口
  - `UDP 8998`：第三方中控主机、动感仿真座椅、PLC 发送 UDP 控制报文端口
  - `UDP 29888` / `TCP 63901` / `UDP 28888`：PICO 企业套件局域网广播与心跳通信端口

---

## 二、 PICO Business Suite 服务模式配置 (关键步骤)

### 1. 为什么必须配置服务模式 (`runAsService=true`)？
PICO Business Suite 安装后默认以普通桌面客户端形态运行。为了让后台管理程序（PicoManager）能够静默、稳定、不间断地通过 SDK 调度头显，**必须将 PICO Business Suite 切换为后台服务模式 (Service Mode)**：
- **避免前台窗口遮挡**：消除界面弹窗与管理员确认对话框；
- **防止误操作关闭**：转为后台常驻后台系统服务，保障 SDK 持续心跳；
- **保障多机批量指令直达**：让头戴加锁、开机、拉起应用完全无感静默执行。

### 2. 配置文件位置与字段说明
PICO 企业套件数据存放在当前 Windows 用户的 AppData 目录下（**注意：非软件安装目录**）：
- **配置文件路径**：`%LOCALAPPDATA%\PICOBusinessSuitData\key-setting.ini`
  *(例如：`C:\Users\Admin\AppData\Local\PICOBusinessSuitData\key-setting.ini`)*
- **配置内容**：在 `[Service]` 节点下，将 `runAsService` 修改为 `true`：
  ```ini
  [Service]
  runAsService=true
  ```

### 3. 一键配置工具使用说明 (两种便捷途径)
为了防止现场运维人员因找不到隐藏文件夹或记事本编码问题改错文件，系统提供了两种极速配置方式：

- **方式 A：使用独立工具（推荐，开箱即用）**
  1. 双击运行部署包中的 **`一键配置PICO服务模式.bat`**；
  2. 工具会自动探测当前用户的 `key-setting.ini`，显示当前状态；
  3. 输入 `1` 回车，即可**自动完成备份、写入 `runAsService=true` 并自动重启企业服务生效**！
- **方式 B：在网页控制台中一键配置**
  1. 打开浏览器进入 PicoManager 控制台，点击右上角「⚙️ 配置头盔与包名」；
  2. 切换至【🛠️ PICO服务模式与运行环境】专栏；
  3. 点击绿色按钮 **【🚀 一键开启服务模式 (true)】** 即可即时生效！

---

## 三、 部署包结构与准备

系统已经完成 **Self-Contained (独立免安装发布)**，目标运行电脑**无需额外安装 .NET SDK 或运行库**，拷入即用。

发布目录与压缩包：
- 完整独立部署包：[`PicoManager_v1.0_完整部署包.zip`](file:///f:/Sun/AIProgram/Pico设备控制/PicoManager_v1.0_完整部署包.zip)
- 部署目录：`PicoVR_Server_Deploy/`

### 核心目录文件说明
```text
PicoVR_Server_Deploy/
├── 启动控制台.bat                 # 一键启动脚本 (自动显示本机IP并拉起浏览器)
├── 放行防火墙端口.bat             # 一键放行 Windows 防火墙 (8999 TCP / 8998 UDP)
├── 一键配置PICO服务模式.bat        # 【一键工具】将 PICO Business Suite 切换为服务模式
├── UDP多机测试.bat               # 【双击测试】快速模拟第三方中控发送多机控制指令
├── 快速配置说明.txt               # 记事本极速配置与端口修改指引
├── config.json                   # 核心持久化配置文件 (包名、机号映射、端口)
├── PicoVR统一管理系统_部署与运维手册.md # 完整部署与技术对接手册
├── PicoManager.Server.exe        # 系统核心运行程序 (x64 免安装)
├── PXREAClientSDK.dll            # PICO 官方企业套件 PC SDK (x64)
├── PBXRPanoramaUnity_x64.dll     # 全景监控编解码加速动态库
├── wwwroot/                      # 前端工作台静态资源 (含环境中心与服务模式卡片)
└── scripts/                      # 自动化运维运维脚本目录
```

---

## 四、 详细部署步骤 (现场实施人员 4 步走)

### 第一步：准备运行目录
1. 将整包 `publish` 文件夹复制到中控电脑的目标安装位置（推荐 `D:\PicoManager\` 或 `C:\PicoManager\`，避免放在中文或含有空格的过深目录中）。

### 第二步：安装并启动 PICO 官方套件
1. 确保中控电脑已安装 **PICO Business Suite**；
2. 打开 PICO Business Suite，登录企业账号，确认头盔处于同一局域网下且在企业套件中能够看到设备在线。

### 第三步：放行防火墙端口 (局域网访问权限)
1. 进入 `publish` 目录；
2. 鼠标右键点击 **`放行防火墙端口8999.bat`**，选择 **【以管理员身份运行】**；
3. 提示“防火墙已成功放行 8999 端口”即可。

### 第四步：启动管理系统
1. 双击运行 **`启动控制台.bat`**；
2. 系统会自动启动控制台黑窗口，并检测 PICO 企业服务状态，同时自动在默认浏览器中打开控制台：
   `http://localhost:8999`
3. 控制台黑窗口会显示类似如下日志，即代表启动就绪：
   ```text
   ==================================================================
           PICO VR 门店统一设备管理控制系统 (PicoManager)
   ==================================================================
    本机访问地址:   http://localhost:8999
    平板/局域网访问: http://192.168.1.100:8999
   ==================================================================
   info: PICO Enterprise Suite SDK 初始化成功 (ret=0)！
   info: Now listening on: http://0.0.0.0:8999
   ```

---

## 四、 移动端 / iPad 平板接入与配置

现场运营人员通常手持 iPad 或 Android 平板在体验区进行巡回操作。

### 1. 连接平板
1. 将 iPad / 平板连入与中控电脑相同的店内 Wi-Fi；
2. 打开平板 Safari 浏览器（或 Chrome 浏览器）；
3. 在地址栏输入中控电脑的局域网 IP，例如：
   `http://192.168.1.100:8999`
4. 即可秒级进入极简操作工作台，无需安装任何 App。

### 2. 将网页添加至主屏幕 (强烈推荐：全屏沉浸体验)
- **iPad (Safari)**：点击浏览器右上角分享按钮 ➔ 选择 **【添加到主屏幕】**。
- **Android 平板 (Chrome)**：点击右上角菜单 ➔ 选择 **【添加到主屏幕】** / **【安装应用】**。
- 效果：桌面上会生成独立的“PICO VR 工作台”快捷图标，点击后将以**全屏无地址栏模式**打开，体验与原生 App 完全一致！

---

## 五、 开机自启动配置 (门店开门即用)

为防止中控电脑重启后服务中断，可配置随 Windows 开机自动启动：

### 方式一：Windows 启动文件夹快捷方式 (最推荐、最简便)
1. 按键盘快捷键 `Win + R`，输入 `shell:startup` 回车，将打开系统的“启动”文件夹；
2. 在 `publish` 目录中找到 `启动控制台.bat`，鼠标右键点击 ➔ 选择 **【创建快捷方式】**；
3. 将生成的快捷方式剪切并粘贴到刚才打开的“启动”文件夹中；
4. **效果**：中控电脑开机登录 Windows 后，将自动拉起 PICO 运营服务并在屏幕上显示控制台。

### 方式二：注册为 Windows 系统服务 (后台静默无弹窗运行)
若希望服务在电脑未登录桌面时即可在后台常驻运行，可使用 **NSSM** 工具：
1. 下载 NSSM（解压后使用 `win64/nssm.exe`）；
2. 管理员打开命令提示符，执行：
   ```cmd
   nssm install PicoManager "D:\PicoManager\PicoManager.Server.exe"
   nssm set PicoManager AppDirectory "D:\PicoManager"
   nssm set PicoManager Start SERVICE_AUTO_START
   nssm start PicoManager
   ```
3. 以后电脑开机即可全天候自动在后台运行服务。

---

## 六、 头戴硬件唯一 SN 码识别与机号绑定全流程规范

### 1. 为什么必须使用 SN 码作为唯一标识？
- **硬件级唯一性**：PICO 头显的 **SN 码 (Serial Number / 序列号)** 是出厂烧录在固件与物理机身内衬贴纸上的唯一编号（例如 `P4E-880001` 或 14 位字符），永不重复。
- **SDK 核心机制**：官方 PICO PC SDK 的所有底层指令（包括锁定 `LockDevice`、启动 `StartPlayApp`、关机 `ShutDownDevice`、重定位 `RecenterView`）**必须且只能使用该 SN 码作为参数**。
- **业务别名 (Alias)**：门店现场为了便于服务游客，通常在头盔上贴物理标签（如“01号机”、“02号机”）。本系统实现了 `SN 码 <---> 机号别名` 的强关联与持久化映射。

### 2. 在何处查看物理头显的 SN 码？
1. **机身贴纸**：摘下头盔面罩内衬，头盔侧梁或内侧标签上清晰印有 `SN: XXXXXXXXXXXXX` 及其对应条形码；
2. **头盔系统内**：戴上头盔打开【设置】➔【关于本机】➔【设备序列号】；
3. **包装盒标签**：外包装彩盒背面的白色出厂标签条形码处。

### 3. 如何在系统中添加与绑定头盔？

#### 方式一：新设备开机自动发现后绑定（推荐日常新加设备）
1. 将新买的 PICO 头显开机，连接到店内 5GHz Wi-Fi；
2. 只要中控电脑运行着 PICO Business Suite，系统会通过底层回调立即捕获该头显并上报其 SN 码；
3. 管理控制台右侧或顶部配置中心会自动列出该新设备，点击 **【修改机号】**；
4. 输入现场分配的贴纸号（如 `13号机`），点击保存；
5. **底层生效**：系统不仅将关系保存至 `config.json`，还会自动调用 PICO SDK 的 `SetDeviceAlias(sn, "13号机")` 写入头戴硬件内存。

#### 方式二：手动提前录入批量建档（推荐开店初期批量初始化）
1. 打开管理网页，点击右上角 **【配置头盔与包名】**；
2. 切换到 **【👓 头显设备与机号绑定 (SN 唯一码)】** 选项卡；
3. 点击右上角 **【+ 手动录入新头盔绑定】** 按钮；
4. 使用条码扫码枪扫描头盔 SN（或手动键盘输入）、填入分配的机号（如 `14号机`）并选择型号；
5. 点击 **【确认保存并绑定】**；
6. 此时该机位卡片即在矩阵看板中就绪；一旦该头显开机连入店内网络，系统瞬间对齐并同步生效！

---

## 七、 实时画面监看的省流与防网络拥塞机制

在现场有 10~30 台头戴同时运行的环境中，如果所有设备不加节制地持续向中控电脑推流，将导致店内 Wi-Fi 信道被大量 MJPEG/YUV 视频流挤占，引起游客头戴画面掉帧甚至网络波动。

- **按需拉流设计**：点击任何头戴卡片的 👁️ 图标调出详情后，画面区域默认处于 **省流待命 (Standby)** 状态，系统不会向头显下发推流请求；
- **手动启动**：仅当运营人员怀疑游客视野偏斜、游戏卡住需要排查时，点击窗口中的 **【▶ 开启实时画面监看】**，系统才启动拉流并显示实时画面；
- **随时关闭**：排查完毕后，点击 **【⏹ 停止拉流 (省流)】** 或切换到其他设备，后台自动调用 `StopMonitorScreen` 彻底释放带宽，保障局域网极致流畅。

---

## 八、 UDP 局域网控制通信协议规范 (中控/动感座椅/外部系统对接)

系统内置高性能后台 UDP 通信服务，持续监听局域网内发往中控电脑的控制数据包。外部设备（如展厅集中控制主机、动感仿真座椅、工业 PLC、独立触控按键台）无需登录浏览器，直接通过 UDP 广播或单播 JSON 数据包，即可毫秒级调度头显启动、退出或重定位，且前端网页秒级全双工同步呈现！

### 1. 通信参数
* **通信协议**：UDP (User Datagram Protocol)
* **编码格式**：UTF-8 编码 JSON 字符串
* **默认监听端口**：`UDP 8998`（可在 `config.json` 或 Web 配置中心中修改）
* **接收绑定**：`0.0.0.0:8998`（支持发往中控电脑局域网 IP 或广播地址）

### 2. 标准报文结构与字段说明

| 字段名 | 必填 | 支持的兼容别名 | 说明与示例值 |
| :--- | :--- | :--- | :--- |
| `deviceNo` / `devices` | 是 | `deviceList`, `device_list`, `device_no`, `machineNo`, `alias`, `sn`, `targets` | **目标机器号或硬件 SN 码（支持单台或多台批量）**。<br/>• **多台数组格式**：`["01", "02", "022"]`<br/>• **多台英文逗号拼接**：`"01, 02, 022"`<br/>• **单台纯数字/中文号**：`"01"`, `"022"`, `"01号机"`<br/>• **硬件唯一 SN**：`"PA9410MGJ9090528G"`<br/>• **全选全场广播**：`"all"` (同时调度所有在线机位) |
| `action` | 是 | `command`, `cmd`, `type` | **执行动作**。<br/>• 启动游戏：`"start"`, `"play"`, `"launch"`, `"1"`<br/>• 退出回桌面：`"stop"`, `"close"`, `"exit"`, `"0"`<br/>• 视角正中复位：`"recenter"`<br/>• 暂停视频：`"pause"`<br/>• 继续播放：`"resume"`<br/>• 重启头戴：`"reboot"`<br/>• 关机：`"shutdown"` |
| `packageName` | 否 | `package_name`, `package`, `app` | **指定启动的 Android 包名或项目名**。<br/>• 若显式传入包名（如 `"com.goldon.hunaochufang"`）或项目名（如 `"胡闹厨房"`），则所有目标机位均统一拉起该游戏；<br/>• **若留空或省略此字段**：系统将**智能读取各机位各自在前端配置绑定的专属默认体验项目**独立启动！ |
| `extraJson` | 否 | `extra`, `params` | 传递给 PICO 游戏/全景播放器的附加 JSON 透传参数。 |

---

### 3. 典型报文与应用场景示例

#### 场景 1：同时启动多台指定机器（各自拉起各自在网页端绑定的默认游戏）
外部第三方中控只需下发目标机器数组，省略 `packageName`，各机位自动拉起各自专属项目：
```json
{
  "devices": ["01", "02", "022"],
  "action": "start"
}
```
* **效果**：01 号机拉起《过山车》，02 号机拉起《恐龙世界》，022 号机拉起《胡闹厨房》；系统内嵌 30ms 错峰调度防网络拥塞，网页前端各卡片全双工同步呈现！

#### 场景 2：同时指定多台机器，统一下发启动指定游戏
```json
{
  "devices": ["01", "02"],
  "action": "start",
  "packageName": "com.picovr.rollercoaster"
}
```

#### 场景 3：仅凭单个机器号启动
```json
{
  "deviceNo": "022",
  "action": "start"
}
```

#### 场景 4：多机或全场一键体验结束，回桌面
```json
{
  "devices": ["01", "02", "022"],
  "action": "stop"
}
```
或全场所有在线设备统一回桌面：
```json
{
  "devices": "all",
  "action": "stop"
}
```

#### 场景 5：动感仿真座椅就位前，远程批量复位多台头显正中视角 (Recenter)
```json
{
  "devices": ["01", "02"],
  "action": "recenter"
}
```

---

### 4. 服务端 UDP ACK 回执响应
当系统收到合法 UDP 报文并调度完成后，会向发送方 IP:Port 异步回送 JSON 回执：
```json
{
  "success": true,
  "deviceNo": "01,02,022",
  "action": "start",
  "packageName": "com.vr.dino",
  "matchedCount": 3,
  "message": "成功调度 3 台设备执行指令",
  "time": "2026-09-15 15:10:27"
}
```

---

### 5. 第三方中控系统对接代码示例

#### ① Python 示例 (最通用，支持快控多台头显)
```python
import socket
import json

def control_pico(devices, action="start", package_name=None, server_ip="192.168.17.97", server_port=8998):
    client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    client.settimeout(3.0)
    
    # 构造控制报文
    payload = {
        "devices": devices,      # 支持列表 ["01", "02"]，字符串 "01, 02"，或 "all"
        "action": action         # "start", "stop", "recenter", "reboot", "shutdown"
    }
    if package_name:
        payload["packageName"] = package_name  # 省略时自动采用各设备在网页配置的默认游戏
        
    try:
        # 发送 UDP 数据包
        data = json.dumps(payload, ensure_ascii=False).encode('utf-8')
        client.sendto(data, (server_ip, server_port))
        print(f"[UDP 发送成功] -> {payload}")
        
        # 接收中控服务端 ACK 回执
        ack_data, _ = client.recvfrom(2048)
        ack = json.loads(ack_data.decode('utf-8'))
        print(f"[收到服务响应] 成功状态: {ack.get('success')}, 匹配设备数: {ack.get('matchedCount')}, 消息: {ack.get('message')}")
        return ack
    except socket.timeout:
        print("[警告] 等待 UDP 回执超时 (服务端可能未开启或处于单向模式)")
    except Exception as e:
        print(f"[错误] UDP 发送失败: {e}")
    finally:
        client.close()

if __name__ == "__main__":
    # 示例 A：同时启动 01、02、022 号机（自动拉起各自默认绑定的游戏）
    control_pico(devices=["01", "02", "022"], action="start")
    
    # 示例 B：体验结束，同时关闭 01、02 号机回到桌面
    # control_pico(devices=["01", "02"], action="stop")
    
    # 示例 C：全场所有设备一键启动指定大片
    # control_pico(devices="all", action="start", package_name="com.picovr.rollercoaster")
```

#### ② C# / .NET 示例 (快思聪 / 独立 C# 客户端 / Unity 中控)
```csharp
using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class PicoCentralController
{
    private const string ServerIp = "192.168.17.97";
    private const int ServerPort = 8998;

    public static async Task SendMultiDeviceCommandAsync(string[] devices, string action, string? packageName = null)
    {
        using var client = new UdpClient();
        client.Client.ReceiveTimeout = 3000;

        var payload = new
        {
            devices = devices,
            action = action,
            packageName = packageName
        };

        string json = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        // 异步发送 UDP 报文
        await client.SendAsync(bytes, bytes.Length, ServerIp, ServerPort);
        Console.WriteLine($"[UDP 已发送] {json}");

        // 等待 ACK 响应
        try
        {
            var res = await client.ReceiveAsync();
            string ackJson = Encoding.UTF8.GetString(res.Buffer);
            Console.WriteLine($"[UDP 收到回执] {ackJson}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP 回执提示] 未收到回执或超时: {ex.Message}");
        }
    }

    public static async Task Main()
    {
        // 同时启动 01 和 02 号机
        await SendMultiDeviceCommandAsync(new[] { "01", "02" }, "start");
    }
}
```

#### ③ Node.js / Electron 示例
```javascript
const dgram = require('dgram');

function sendPicoCommand({ devices, action, packageName, ip = '192.168.17.97', port = 8998 }) {
  const client = dgram.createSocket('udp4');
  const payload = JSON.stringify({ devices, action, packageName });

  client.send(payload, port, ip, (err) => {
    if (err) {
      console.error('发送错误:', err);
      client.close();
      return;
    }
    console.log(`[UDP 下发] -> ${payload}`);
  });

  client.on('message', (msg) => {
    console.log(`[收到 ACK] <- ${msg.toString()}`);
    client.close();
  });

  setTimeout(() => client.close(), 3000);
}

// 调用示例：同时启动 01、02、022 号机
sendPicoCommand({
  devices: ["01", "02", "022"],
  action: "start"
});
```

---

## 九、 业务配置参数说明 (`config.json`)

系统参数保存在根目录下的 `config.json` 中，可通过 Web 界面右上角「配置头盔与包名」图形化编辑，也可直接用记事本修改：

```json
{
  "WebPort": 8999,
  "UdpPort": 8998,
  "EnableUdpService": true,
  "AutoLockOnFind": true,
  "EnableSimulatorFallback": true,
  "Projects": [
    {
      "Id": "proj-580725dc",
      "Name": "胡闹厨房",
      "Type": "vr_app",
      "PackageName": "com.goldon.hunaochufang",
      "DurationMinutes": 15,
      "Icon": "🎮",
      "ExtraJson": ""
    }
  ],
  "HeadsetAliases": {
    "PA9410MGJ9090528G": "022"
  },
  "DeviceDefaultProjects": {
    "022": "com.goldon.hunaochufang"
  }
}
```

- **`WebPort`**：HTTP Web 服务端口号（默认 8999）；
- **`UdpPort`**：UDP 控制指令接收端口号（默认 8998）；
- **`EnableUdpService`**：是否开启后台 UDP 控制监听（默认 true）；
- **`AutoLockOnFind`**：当头显上线时，系统是否自动执行 `LockDevice`（必须保持 `true`）；
- **`WebPort`**：HTTP Web 服务端口号（默认 8999）；
- **`UdpPort`**：UDP 控制指令接收端口号（默认 8998）；
- **`EnableUdpService`**：是否开启后台 UDP 控制监听（默认 true）；
- **`AutoLockOnFind`**：当头显上线时，系统是否自动执行 `LockDevice`（必须保持 `true`）；
- **`EnableSimulatorFallback`**：是否启用模拟测试设备（商用现场已设为 `false`，界面仅纯净呈现真实头显）；
- **`Projects`**：体验项目库（包含包名、时长、图标等）；
- **`HeadsetAliases`**：头盔 SN 码与现场贴纸机号（如 01, 02, 022）的持久化绑定映射；
- **`DeviceDefaultProjects`**：每台机器号与默认启动体验项目的关联映射。

---

## 十、 真机在线检测与状态自动恢复机制

### 1. 纯真机架构与自动上线检测
- **启动预置卡片**：系统启动时，将根据 `config.json` 中配置的 `HeadsetAliases` 自动呈现所有已绑定的机器号（如 022 号机）；若头戴关机，卡片徽章显示灰色「⚪离线」；
- **开机即连**：头戴开机联网后，系统底层的主动遥测探测器（每 3 秒下发状态自检探针）将在数秒内捕获该设备，卡片自动点亮为绿色「🟢空闲」；
- **失联秒级判定**：若头显关机、拔电池或离开 Wi-Fi 信号，心跳看门狗（12 秒超时）将自动将其置为「离线」，无需人工刷新页面。

### 2. 游戏退出自动恢复待命状态 (桌面识别)
- **企业桌面特征包名**：在 PICO 商业套件架构中，头戴退出游戏或按 Home 键回到系统主界面时，实际运行的前台程序为 **PICO 企业套件 VR 客户端**：
  - 包名：`com.picovr.enterpriseassistant`
  - 核心桌面：`com.pico.launcher`
- **状态无缝回退**：播控系统已将上述包名与原生「系统桌面」建立映射机制。一旦遥测检测到头戴前台应用回到 `com.picovr.enterpriseassistant`，工作台卡片将立即从蓝色「体验中」恢复为绿色「空闲」，同时更新右侧监控面板与中控遥测数据，确保运营人员与外部中控能准确感知设备已经就绪，可随时接待下一位游客。

---

## 十一、 大容量文件传输与 APK 批量安装工具 (PicoDeployer 独立工具链)

日常运营中，如果直接在播控管理系统中嵌入视频推流或大体积 APK 传输，极易因高并发占用 TCP 句柄或阻塞 PICO Business Suite SDK 信令，导致播放卡顿甚至掉线。

因此，系统开发了**完全解耦的独立工具链——`PicoDeployer`**：
- **方案架构**：播控业务（`PicoManager.Server`，端口 8999/8998）与大文件分发（`PicoDeployer`，端口 9005）完全物理隔离，互不干扰；
- **技术实现**：基于 PICO 原生支持的 **无线 ADB (TCP 5555)** 架构，采用独立轻量可视化 Web 工作台 + 命令行双模，无需借助官方服务端口，不消耗播控线程；
- **独立运行入口**：双击部署包中的 **`启动批量部署工具.bat`**，自动在后台启动并在浏览器中打开 `http://localhost:9005`。

### 1. 多机并发安装 APK 操作指南
1. 进入工作台【📦 APK 批量并发安装】选项卡；
2. 输入本地 APK 安装包的绝对路径（如 `D:\Games\HunaoChufang.apk`）；
3. 调节并发执行线程数（默认 3 台并发，推荐 2~4 台，保护店内 5GHz 无线路由器不拥塞）；
4. 勾选需要安装的目标机位（支持一键全选或仅选在线机位）；
5. 点击 **【🚀 一键开始多机静默安装】**：
   - 自动下发 `-r -d -g` 参数（保留数据、允许降级、自动授予存储与传感器等全部权限，头显内零确认弹窗）；
   - 各机位卡片显示实时流式推流进度条与耗时，完成后自动变绿完成。

### 2. 差异化配置文件替换操作指南 (每台设备内容略有不同)
在实际商用场景中，每台头戴的配置文件（如 `config.json` 或 `settings.ini`）通常包含特定机号、主从角色、专属动感座椅号或配对端口。`PicoDeployer` 内置强大的**宏变量模板引擎**：

1. 进入【⚙️ 差异化配置在线替换】选项卡；
2. 在左侧编辑器中输入或粘贴配置模板，可自由使用系统占位符：
   - `{{DEVICE_NO}}`：纯机位编号（如 `01`、`022`）；
   - `{{DEVICE_ALIAS}}`：设备别名（如 `022号机`）；
   - `{{DEVICE_IP}}`：无线 ADB IP（如 `192.168.1.122`）；
   - `{{DEVICE_SN}}`：硬件序列号；
   - `{{SEAT_ID}}`、`{{ROLE}}`、`{{SERVER_PORT}}`：在设备台账中为每台设备定义的专属变量；
3. 输入头显端目标存储路径（如 `/sdcard/Android/data/<包名>/files/config.json`，系统自动 `mkdir -p` 创建父目录）；
4. **在线实时预览**：点击 **【👁️ 实时预览各机位生成效果】**，下拉选择不同机器，系统即刻高亮呈现替换后的真实文件文本，确认无误；
5. 点击 **【🚀 开始批量下发差异化配置】**，系统将为每台机器独立渲染其专属文件，并发推入头显存储中，完成后自动清理本地临时缓存！

### 3. 大文件 / 全景视频极速推流
1. 进入【📁 大文件 / 视频极速推流】选项卡；
2. 输入本地视频路径（如 `D:\Videos\8K_Panoramic.mp4`），目标路径默认为 `/sdcard/Movies/`；
3. 点击 **【🚀 一键开始多机推流传输】**，享受千兆无线满速（30~60 MB/s / 台）分发。

---

## 十二、 现场常见问题排查 (FAQ)

### Q1: 平板浏览器无法打开网页（提示连接超时）？
1. **检查 Wi-Fi**：确认平板连接的 Wi-Fi 与电脑是在同一台路由器下；
2. **检查防火墙**：中控电脑是否以管理员身份运行了 `放行防火墙端口.bat`，或在 Windows Defender 防火墙中临时关闭专用网络防火墙测试；
3. **检查 IP 是否变动**：中控电脑重新插拔网线后 IP 可能变更，可在黑窗口中查看当前打印的局域网 IP。

### Q2: 启动控制台显示 PICO SDK 返回码非 0？
- **原因**：中控电脑上的 **PICO Business Suite (企业服务客户端)** 未在后台运行。
- **解决**：先打开 PICO Business Suite 软件，登录并确保其处于托盘区正常运行，再启动 PicoManager。

### Q3: 头盔开机了，但控制台设备矩阵显示该头显“离线”？
1. 检查头盔内 Wi-Fi 连接是否成功，IP 是否处于同一网段；
2. 检查头盔内是否安装并授权了 PICO 企业套件 VR 端应用；
3. 检查路由器后台是否开启了 **AP 隔离**，如有必须关闭。

### Q4: 点击“一键启动体验”后，头盔没有打开对应游戏？
1. 检查配置中心中登记的 **Android 包名** 是否与头戴内实际安装的 APK 包名完全一致（字母大小写需严格匹配）；
2. 点击卡片上的 👁️ 监看，检查头盔当前画面是否停留在“权限申请弹窗”或被系统休眠；
3. 尝试点击单机运维工具箱中的“🎯 视角正中复位”或“🏠 强制回桌面”重置当前状态。

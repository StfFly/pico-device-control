using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PicoManager.Server.Hubs;
using PicoManager.Server.Models;

namespace PicoManager.Server.Services
{
    public class UdpCommandService : BackgroundService
    {
        private readonly ILogger<UdpCommandService> _logger;
        private readonly PicoDeviceManager _deviceManager;
        private readonly ConfigService _configService;
        private readonly IHubContext<PicoHub> _hubContext;
        private UdpClient? _udpClient;

        public UdpCommandService(
            ILogger<UdpCommandService> logger,
            PicoDeviceManager deviceManager,
            ConfigService configService,
            IHubContext<PicoHub> hubContext)
        {
            _logger = logger;
            _deviceManager = deviceManager;
            _configService = configService;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var config = _configService.CurrentConfig;
            if (!config.EnableUdpService)
            {
                _logger.LogInformation("[UDP Service] 系统配置未启用 UDP 服务，服务跳过监听。");
                return;
            }

            int port = config.UdpPort > 0 ? config.UdpPort : 8998;

            try
            {
                _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _logger.LogInformation("[UDP Service] 已成功启动 UDP 控制监听，端口: {Port} (0.0.0.0:{Port})", port, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UDP Service] 启动监听端口 {Port} 失败，可能端口被占用。", port);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var receiveResult = await _udpClient.ReceiveAsync(stoppingToken);
                    byte[] buffer = receiveResult.Buffer;
                    if (buffer.Length == 0) continue;

                    string jsonText = Encoding.UTF8.GetString(buffer).Trim();
                    _logger.LogInformation("[UDP Service] 收到来自 {Remote} 的数据包: {Json}", receiveResult.RemoteEndPoint, jsonText);

                    _ = Task.Run(async () =>
                    {
                        var response = await ProcessUdpMessageAsync(jsonText, receiveResult.RemoteEndPoint);
                        if (response != null && _udpClient != null)
                        {
                            try
                            {
                                byte[] respBytes = Encoding.UTF8.GetBytes(response);
                                await _udpClient.SendAsync(respBytes, respBytes.Length, receiveResult.RemoteEndPoint);
                            }
                            catch { }
                        }
                    }, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[UDP Service] 接收或处理数据异常: {Message}", ex.Message);
                }
            }

            _udpClient?.Close();
            _logger.LogInformation("[UDP Service] UDP 监听已停止。");
        }

        public async Task<string> ProcessUdpMessageAsync(string jsonText, IPEndPoint? remoteEndPoint = null)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                // 容错解析设备号 (支持单个字符串、逗号拼接或数组形式)
                var devList = GetDeviceList(root);
                string deviceDesc = devList.Count > 0 ? string.Join(",", devList) : "all";

                // 容错解析指令动作
                string action = GetStringProperty(root, "action", "command", "cmd", "type").ToLowerInvariant();

                // 容错解析游戏包名或项目名
                string packageName = GetStringProperty(root, "packageName", "package_name", "package", "app", "appName");

                // 额外参数
                string extraJson = GetStringProperty(root, "extraJson", "extra", "params");

                if (string.IsNullOrWhiteSpace(action))
                {
                    return CreateAck(false, deviceDesc, action, "缺少 action 动作字段 (如 start / stop)");
                }

                // 智能匹配待操作设备集群
                List<PicoDevice> targets = new();
                if (devList.Count == 0 || devList.Any(d => d.Equals("all", StringComparison.OrdinalIgnoreCase)))
                {
                    targets = _deviceManager.ResolveDevices("all");
                }
                else
                {
                    foreach (var dNo in devList)
                    {
                        var matched = _deviceManager.ResolveDevices(dNo);
                        foreach (var m in matched)
                        {
                            if (!targets.Any(t => t.SN == m.SN)) targets.Add(m);
                        }
                    }
                }

                if (targets.Count == 0)
                {
                    _logger.LogWarning("[UDP Service] 未找到对应设备: {DeviceDesc}", deviceDesc);
                    await NotifyFrontendAsync(deviceDesc, "none", action, packageName, false, $"未找到匹配设备: {deviceDesc}");
                    return CreateAck(false, deviceDesc, action, $"未找到匹配设备: {deviceDesc}");
                }

                int successCount = 0;
                string lastPackageName = "";

                switch (action)
                {
                    case "start":
                    case "play":
                    case "launch":
                    case "open":
                    case "1":
                        foreach (var dev in targets)
                        {
                            // 针对每台设备独立解析其绑定的默认包名 (若未指定 packageName)
                            string finalPackage = _deviceManager.ResolvePackageName(dev.SN, packageName);
                            lastPackageName = finalPackage;
                            _logger.LogInformation("[UDP Service] 调度启动设备 [{SN}]({Alias}) 游戏: {Package}", dev.SN, dev.DisplayName, finalPackage);
                            _deviceManager.StartApp(dev.SN, finalPackage, extraJson);
                            successCount++;
                            await NotifyFrontendAsync(dev.DisplayName, dev.SN, "start", finalPackage, true, $"已通过 UDP 启动【{dev.DisplayName}】游戏：{dev.AppTitle}");
                            if (targets.Count > 1) await Task.Delay(30); // 错峰分发
                        }
                        break;

                    case "stop":
                    case "close":
                    case "exit":
                    case "quit":
                    case "0":
                        foreach (var dev in targets)
                        {
                            _logger.LogInformation("[UDP Service] 调度关闭设备 [{SN}]({Alias}) 游戏并回桌面", dev.SN, dev.DisplayName);
                            _deviceManager.StopApp(dev.SN);
                            successCount++;
                            await NotifyFrontendAsync(dev.DisplayName, dev.SN, "stop", "系统桌面", true, $"已通过 UDP 退出【{dev.DisplayName}】回到系统桌面");
                            if (targets.Count > 1) await Task.Delay(30);
                        }
                        break;

                    case "pause":
                        foreach (var dev in targets)
                        {
                            _deviceManager.PauseVideo(dev.SN);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "pause", "", true, "已通过 UDP 下发暂停");
                        break;

                    case "resume":
                    case "continue":
                        foreach (var dev in targets)
                        {
                            _deviceManager.ContinueVideo(dev.SN);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "resume", "", true, "已通过 UDP 下发继续播放");
                        break;

                    case "recenter":
                        foreach (var dev in targets)
                        {
                            _deviceManager.Recenter(dev.SN);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "recenter", "", true, "已通过 UDP 下发视角正中复位");
                        break;

                    case "reboot":
                        foreach (var dev in targets)
                        {
                            _deviceManager.Reboot(dev.SN);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "reboot", "", true, "已通过 UDP 下发重启指令");
                        break;

                    case "screen_on":
                    case "screenon":
                    case "light_on":
                        foreach (var dev in targets)
                        {
                            await _deviceManager.SetScreenStateAsync(dev.SN, true);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "screen_on", "", true, "已通过 UDP 下发点亮屏幕");
                        break;

                    case "screen_off":
                    case "screenoff":
                    case "light_off":
                    case "sleep":
                        foreach (var dev in targets)
                        {
                            await _deviceManager.SetScreenStateAsync(dev.SN, false);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "screen_off", "", true, "已通过 UDP 下发熄灭屏幕");
                        break;

                    case "shutdown":
                        foreach (var dev in targets)
                        {
                            _deviceManager.Shutdown(dev.SN);
                            successCount++;
                        }
                        await NotifyFrontendAsync(deviceDesc, "batch", "shutdown", "", true, "已通过 UDP 下发关机指令");
                        break;

                    default:
                        return CreateAck(false, deviceDesc, action, $"不支持的动作: {action}");
                }

                return CreateAck(true, deviceDesc, action, $"成功调度 {successCount} 台设备执行指令", successCount, lastPackageName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UDP Service] 解析或执行 UDP 报文失败");
                return CreateAck(false, "", "", "报文 JSON 解析异常: " + ex.Message);
            }
        }

        private static List<string> GetDeviceList(JsonElement root)
        {
            var list = new List<string>();
            string[] propNames = { "devices", "deviceList", "device_list", "deviceNo", "device_no", "machineNo", "alias", "sn", "target", "targets" };

            if (root.ValueKind != JsonValueKind.Object) return list;

            foreach (var prop in root.EnumerateObject())
            {
                if (propNames.Any(n => string.Equals(n, prop.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            string s = item.ToString().Trim();
                            if (!string.IsNullOrEmpty(s)) list.Add(s);
                        }
                        if (list.Count > 0) return list;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string s = prop.Value.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            var parts = s.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            list.AddRange(parts);
                            return list;
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        list.Add(prop.Value.ToString());
                        return list;
                    }
                }
            }
            return list;
        }

        private async Task NotifyFrontendAsync(string deviceNo, string sn, string action, string packageName, bool success, string message)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("UdpCommandReceived", new
                {
                    deviceNo,
                    sn,
                    action,
                    packageName,
                    success,
                    message,
                    timestamp = DateTime.UtcNow.ToString("HH:mm:ss")
                });
            }
            catch { }
        }

        private static string GetStringProperty(JsonElement root, params string[] propertyNames)
        {
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            foreach (var prop in root.EnumerateObject())
            {
                foreach (var name in propertyNames)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            return prop.Value.GetString() ?? string.Empty;
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                            return prop.Value.ToString();
                        if (prop.Value.ValueKind == JsonValueKind.True)
                            return "true";
                        if (prop.Value.ValueKind == JsonValueKind.False)
                            return "false";
                    }
                }
            }
            return string.Empty;
        }

        private static string CreateAck(bool success, string deviceNo, string action, string message, int matchedCount = 0, string packageName = "")
        {
            return JsonSerializer.Serialize(new
            {
                success,
                deviceNo,
                action,
                packageName,
                matchedCount,
                message,
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        public override void Dispose()
        {
            _udpClient?.Dispose();
            base.Dispose();
        }
    }
}

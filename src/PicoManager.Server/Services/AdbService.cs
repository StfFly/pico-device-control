using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PicoManager.Server.Services
{
    public class AdbDeviceInfo
    {
        public string Target { get; set; } = string.Empty; // e.g. "192.168.17.45:5555" or "1PB4E..."
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; } = 5555;
        public string Status { get; set; } = "offline"; // "device", "offline", "unauthorized"
        public string Sn { get; set; } = string.Empty;
        public string Model { get; set; } = "PICO VR";
        public int Battery { get; set; } = -1;
        public bool IsWireless { get; set; }
        public bool IsConnected => string.Equals(Status, "device", StringComparison.OrdinalIgnoreCase);
    }

    public class AdbService
    {
        private readonly ILogger<AdbService> _logger;
        private string _adbPath;

        public AdbService(ILogger<AdbService> logger)
        {
            _logger = logger;
            _adbPath = ResolveAdbPath();
        }

        public string AdbPath => _adbPath;

        public static string ResolveAdbPath()
        {
            var candidates = new[]
            {
                @"G:\Program Files\platform-tools\adb.exe",
                @"C:\Program Files\platform-tools\adb.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tools", "PicoDeployer", "adb.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe")
            };

            foreach (var cand in candidates)
            {
                if (File.Exists(cand)) return cand;
            }

            return "adb"; // fallback to system PATH
        }

        public async Task<(int ExitCode, string Output, string Error)> RunAdbAsync(
            string arguments,
            int timeoutMs = 15000,
            CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var outSb = new StringBuilder();
            var errSb = new StringBuilder();

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                await process.WaitForExitAsync(cts.Token);
                return (process.ExitCode, outSb.ToString().Trim(), errSb.ToString().Trim());
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return (-1, outSb.ToString().Trim(), "操作超时");
            }
            catch (Exception ex)
            {
                return (-2, outSb.ToString().Trim(), $"执行 ADB 出错: {ex.Message}");
            }
        }

        public async Task<List<AdbDeviceInfo>> GetConnectedDevicesAsync()
        {
            var list = new List<AdbDeviceInfo>();
            var res = await RunAdbAsync("devices -l", 8000);
            if (string.IsNullOrWhiteSpace(res.Output)) return list;

            var lines = res.Output.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var target = parts[0];
                var status = parts[1];

                var dev = new AdbDeviceInfo
                {
                    Target = target,
                    Status = status
                };

                if (target.Contains(':'))
                {
                    dev.IsWireless = true;
                    var ipParts = target.Split(':');
                    dev.Ip = ipParts[0];
                    if (int.TryParse(ipParts[1], out int p)) dev.Port = p;
                }
                else
                {
                    dev.IsWireless = false;
                    dev.Sn = target; // USB 直连时，Target 通常就是硬件 SN
                }

                // 解析 model:xxx
                var modelMatch = Regex.Match(line, @"model:(\S+)");
                if (modelMatch.Success)
                {
                    dev.Model = modelMatch.Groups[1].Value.Replace('_', ' ');
                }

                if (string.Equals(status, "device", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(dev.Sn))
                    {
                        dev.Sn = await ReadDeviceSnAsync(target);
                    }
                    dev.Battery = await ReadDeviceBatteryAsync(target);
                }

                list.Add(dev);
            }

            return list;
        }

        public async Task<bool> ConnectAsync(string ip, int port = 5555)
        {
            var target = port == 5555 ? ip : $"{ip}:{port}";
            if (!target.Contains(':')) target = $"{target}:5555";

            var res = await RunAdbAsync($"connect {target}", 8000);
            return res.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                   res.Output.Contains("already connected", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> DisconnectAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            var res = await RunAdbAsync($"disconnect {target}", 5000);
            return res.ExitCode == 0;
        }

        public async Task<string> ReadDeviceSnAsync(string target)
        {
            var snRes = await RunAdbAsync($"-s {target} shell getprop ro.serialno", 4000);
            var sn = snRes.Output.Trim();
            if (!string.IsNullOrWhiteSpace(sn) && !sn.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return sn;
            }

            var bootSnRes = await RunAdbAsync($"-s {target} shell getprop ro.boot.serialno", 4000);
            var bootSn = bootSnRes.Output.Trim();
            if (!string.IsNullOrWhiteSpace(bootSn) && !bootSn.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return bootSn;
            }

            return string.Empty;
        }

        public async Task<int> ReadDeviceBatteryAsync(string target)
        {
            var batRes = await RunAdbAsync($"-s {target} shell \"dumpsys battery | grep level\"", 4000);
            if (!string.IsNullOrWhiteSpace(batRes.Output))
            {
                var match = Regex.Match(batRes.Output, @"level:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
                {
                    return level;
                }
            }
            return -1;
        }

        public async Task<bool> RebootAsync(string target)
        {
            var res = await RunAdbAsync($"-s {target} reboot", 8000);
            return res.ExitCode == 0;
        }

        public async Task<bool> ForceStopAppAsync(string snOrTarget, string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return false;

            var devs = await GetConnectedDevicesAsync();
            var match = devs.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;

            // am force-stop 强制杀掉进程（无弹窗、不留后台、秒级终止释放资源）
            var res = await RunAdbAsync($"-s {target} shell am force-stop {packageName}", 5000);
            return res.ExitCode == 0;
        }

        public async Task ForceStopAllGamesAsync(string snOrTarget, IEnumerable<string> packageNames)
        {
            var devs = await GetConnectedDevicesAsync();
            var match = devs.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;

            foreach (var pkg in packageNames.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                await RunAdbAsync($"-s {target} shell am force-stop {pkg.Trim()}", 3000);
            }
        }

        public List<string> DetectLanSubnets()
        {
            var subnets = new HashSet<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var props = ni.GetIPProperties();
                    foreach (var u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                        {
                            var bytes = u.Address.GetAddressBytes();
                            // 排除 APIPA (169.254.x.x) 和 Clash/TUN (198.18.x.x)
                            if (bytes[0] == 169 && bytes[1] == 254) continue;
                            if (bytes[0] == 198 && bytes[1] == 18) continue;

                            subnets.Add($"{bytes[0]}.{bytes[1]}.{bytes[2]}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "检测局域网子网失败");
            }

            if (subnets.Count == 0)
            {
                subnets.Add("192.168.1");
            }

            return subnets.ToList();
        }

        public async Task<List<AdbDeviceInfo>> ScanAndAutoConnectAsync(string? customSubnet = null, int timeoutMs = 600)
        {
            var targetSubnets = new List<string>();
            if (!string.IsNullOrWhiteSpace(customSubnet))
            {
                targetSubnets.Add(customSubnet.Trim().TrimEnd('.'));
            }
            else
            {
                targetSubnets = DetectLanSubnets();
            }

            var discoveredIps = new ConcurrentBag<string>();
            using var sem = new SemaphoreSlim(64);

            var scanTasks = new List<Task>();
            foreach (var subnet in targetSubnets)
            {
                for (int i = 1; i <= 254; i++)
                {
                    string ip = $"{subnet}.{i}";
                    scanTasks.Add(Task.Run(async () =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            using var client = new TcpClient();
                            var connectTask = client.ConnectAsync(ip, 5555);
                            var delayTask = Task.Delay(timeoutMs);

                            if (await Task.WhenAny(connectTask, delayTask) == connectTask && client.Connected)
                            {
                                discoveredIps.Add(ip);
                            }
                        }
                        catch { }
                        finally
                        {
                            sem.Release();
                        }
                    }));
                }
            }

            await Task.WhenAll(scanTasks);

            // 针对探测到 5555 端口开放的 IP，批量执行无线 ADB 连接
            var connectTasks = discoveredIps.Select(async ip =>
            {
                try
                {
                    await ConnectAsync(ip, 5555);
                }
                catch { }
            });
            await Task.WhenAll(connectTasks);

            // 获取最新已连接设备列表
            return await GetConnectedDevicesAsync();
        }

        public async Task SetScreenStateAsync(string snOrTarget, bool screenOn)
        {
            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;
            // 224: KEYCODE_WAKEUP (点亮屏幕并唤醒)
            // 223: KEYCODE_SLEEP (关闭屏幕并休眠)
            int keycode = screenOn ? 224 : 223;
            await RunAdbAsync($"-s {target} shell input keyevent {keycode}", 3000);
        }

        public async Task<(bool Success, string Message)> InstallApkAsync(
            string snOrTarget,
            string apkPath,
            Action<int, string>? onProgress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(apkPath))
                return (false, $"本地 APK 文件不存在: {apkPath}");

            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            onProgress?.Invoke(20, $"正在向 [{target}] 推流安装 APK...");
            var sw = Stopwatch.StartNew();

            // 严格带 -s "{target}" 隔离设备执行，杜绝多设备时出现 "more than one device/emulator"
            // -r: 覆盖安装 -d: 允许降级 -g: 授予所有权限
            var res = await RunAdbAsync($"-s \"{target}\" install -r -d -g \"{apkPath}\"", 300000, ct);
            sw.Stop();
            var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);

            string combined = $"{res.Output}\n{res.Error}".Trim();
            if (combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(100, $"✅ 安装成功 (耗时 {sec}s)");
                return (true, $"安装成功 (耗时 {sec}s)");
            }

            string errMsg = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
            if (errMsg.Contains("more than one device", StringComparison.OrdinalIgnoreCase))
            {
                errMsg = $"检测到多设备冲突: 请确认指定了单一目标设备SN/IP (当前目标: {target})";
            }
            onProgress?.Invoke(100, $"❌ 安装失败: {errMsg}");
            return (false, errMsg);
        }

        public async Task<(bool Success, string Message)> CreateFolderAsync(
            string snOrTarget,
            string remotePath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
                return (false, "远程文件夹路径不能为空");

            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            string normalized = remotePath.Trim().Replace('\\', '/');
            if (!normalized.StartsWith('/')) normalized = "/" + normalized;

            var res = await RunAdbAsync($"-s \"{target}\" shell mkdir -p \"{normalized}\"", 10000, ct);
            if (res.ExitCode == 0 && string.IsNullOrWhiteSpace(res.Error))
            {
                return (true, $"已成功在 [{target}] 创建远程文件夹: {normalized}");
            }

            string err = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
            return (false, $"创建文件夹失败: {err}");
        }

        public async Task<(bool Success, string Message)> UninstallAppAsync(
            string snOrTarget,
            string packageName,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return (false, "包名不能为空");

            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            var res = await RunAdbAsync($"-s \"{target}\" uninstall \"{packageName.Trim()}\"", 30000, ct);
            if (res.Output.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"卸载成功 ({packageName})");
            }

            string errMsg = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
            return (false, errMsg);
        }

        public async Task<List<string>> ListInstalledPackagesAsync(string snOrTarget, CancellationToken ct = default)
        {
            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;
            if (string.IsNullOrWhiteSpace(target))
                return new List<string>();

            var res = await RunAdbAsync($"-s \"{target}\" shell pm list packages -3", 10000, ct);
            var list = new List<string>();
            foreach (var line in res.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = line.Trim();
                if (clean.StartsWith("package:")) clean = clean.Substring("package:".Length).Trim();
                if (!string.IsNullOrWhiteSpace(clean)) list.Add(clean);
            }
            return list.OrderBy(p => p).ToList();
        }

        public async Task<(bool Success, string Message)> PushFileOrFolderAsync(
            string snOrTarget,
            string localPath,
            string remoteTargetPath,
            bool overwrite = true,
            Action<int, string>? onProgress = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(localPath) && !Directory.Exists(localPath))
                return (false, $"本地路径不存在: {localPath}");

            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = match?.Target ?? snOrTarget;
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            string normalizedRemote = remoteTargetPath.Trim().Replace('\\', '/');
            if (!normalizedRemote.StartsWith('/')) normalizedRemote = "/" + normalizedRemote;

            // 预先建立远端目标父目录
            string? remoteDir = normalizedRemote.EndsWith('/') ? normalizedRemote : Path.GetDirectoryName(normalizedRemote)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(remoteDir))
            {
                await RunAdbAsync($"-s \"{target}\" shell mkdir -p \"{remoteDir}\"", 8000, ct);
            }

            // 若开启强制覆盖且已有重名文件/目录，预先执行 rm -rf 清除避免 ADB报 File exists
            if (overwrite)
            {
                string cleanTarget = normalizedRemote.TrimEnd('/');
                // 仅当目标非根目录时清理，避免误删根系统
                if (cleanTarget.Length > 8 && cleanTarget.StartsWith("/sdcard/"))
                {
                    await RunAdbAsync($"-s \"{target}\" shell rm -rf \"{cleanTarget}\"", 10000, ct);
                }
            }

            onProgress?.Invoke(30, $"正在向 [{target}] 推送文件/目录...");
            var sw = Stopwatch.StartNew();
            var res = await RunAdbAsync($"-s \"{target}\" push \"{localPath}\" \"{normalizedRemote}\"", 600000, ct);
            sw.Stop();
            var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);

            string combined = $"{res.Output}\n{res.Error}".Trim();
            if (res.ExitCode == 0 && (combined.Contains("pushed", StringComparison.OrdinalIgnoreCase) || combined.Contains("1 file pushed") || string.IsNullOrEmpty(res.Error)))
            {
                onProgress?.Invoke(100, $"✅ 传输完成 (耗时 {sec}s)");
                return (true, $"传输成功 (耗时 {sec}s): {res.Output.Trim()}");
            }

            string err = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
            onProgress?.Invoke(100, $"❌ 传输失败: {err}");
            return (false, err);
        }
    }
}

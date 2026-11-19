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

        /// <summary>
        /// 过滤危险的 Shell 元字符，保留中文、空格、斜杠、点号、横线、下划线、冒号
        /// </summary>
        public static string SanitizeShellArg(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            char[] dangerous = new[] { ';', '&', '|', '$', '`', '\n', '\r', '"', '\'', '<', '>' };
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (!dangerous.Contains(c))
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim();
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
            string cleanPkg = SanitizeShellArg(packageName);
            if (string.IsNullOrWhiteSpace(cleanPkg)) return false;

            var devs = await GetConnectedDevicesAsync();
            var match = devs.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = SanitizeShellArg(match?.Target ?? snOrTarget);

            // am force-stop 强制杀掉进程（无弹窗、不留后台、秒级终止释放资源）
            var res = await RunAdbAsync($"-s {target} shell am force-stop {cleanPkg}", 5000);
            return res.ExitCode == 0;
        }

        public async Task ForceStopAllGamesAsync(string snOrTarget, IEnumerable<string> packageNames)
        {
            var devs = await GetConnectedDevicesAsync();
            var match = devs.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = SanitizeShellArg(match?.Target ?? snOrTarget);

            foreach (var pkg in packageNames.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                string cleanPkg = SanitizeShellArg(pkg);
                if (!string.IsNullOrWhiteSpace(cleanPkg))
                {
                    await RunAdbAsync($"-s {target} shell am force-stop {cleanPkg}", 3000);
                }
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
            if (string.IsNullOrWhiteSpace(target)) return;

            if (screenOn)
            {
                // 224: KEYCODE_WAKEUP (点亮屏幕并唤醒)
                await RunAdbAsync($"-s {target} shell input keyevent 224", 3000);
                // 82: KEYCODE_MENU (自动解锁锁屏保护)
                await RunAdbAsync($"-s {target} shell input keyevent 82", 2000);
            }
            else
            {
                // 223: KEYCODE_SLEEP (关闭屏幕并休眠)
                await RunAdbAsync($"-s {target} shell input keyevent 223", 3000);
            }
        }

        private static string ParseAdbInstallError(string output, string error)
        {
            string combined = $"{output}\n{error}".Trim();

            if (combined.Contains("INSTALL_FAILED_VERSION_DOWNGRADE", StringComparison.OrdinalIgnoreCase))
                return "设备已安装更高版本的应用，系统禁止降级覆盖安装（请先在设备上卸载旧版应用后再试）";
            if (combined.Contains("INSTALL_FAILED_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase))
                return "签名冲突或应用已存在（设备上的旧版签名与新 APK 不一致，请先卸载旧版后重装）";
            if (combined.Contains("INSTALL_FAILED_TEST_ONLY", StringComparison.OrdinalIgnoreCase))
                return "该应用为开发调试测试包（testOnly），需使用 -t 参数安装";
            if (combined.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE", StringComparison.OrdinalIgnoreCase))
                return "头显剩余存储空间不足，安装已终止，请先清理设备存储空间";
            if (combined.Contains("INSTALL_FAILED_INVALID_APK", StringComparison.OrdinalIgnoreCase))
                return "APK 安装包解析失败或文件损坏，请重新下载或检查安装包";
            if (combined.Contains("INSTALL_PARSE_FAILED_NOT_APK", StringComparison.OrdinalIgnoreCase))
                return "指定的文件并非有效的 Android APK 安装包";
            if (combined.Contains("INSTALL_FAILED_NO_MATCHING_ABIS", StringComparison.OrdinalIgnoreCase))
                return "CPU 芯片架构不匹配（该 APK 未包含适配 PICO 头显 ARM64-v8a 的本地库）";
            if (combined.Contains("INSTALL_FAILED_USER_RESTRICTED", StringComparison.OrdinalIgnoreCase))
                return "安装被头显安全设置或用户拦截";
            if (combined.Contains("INSTALL_FAILED_DEXOPT", StringComparison.OrdinalIgnoreCase))
                return "虚拟机 Dex 优化执行失败（可能系统存储不足或 APK 包含非法字节码）";
            if (combined.Contains("more than one device", StringComparison.OrdinalIgnoreCase))
                return "检测到多台 ADB 设备冲突，未能隔离目标设备";
            if (combined.Contains("device offline", StringComparison.OrdinalIgnoreCase))
                return "目标设备处于离线状态 (offline)，请确认头戴已开机并连入局域网";
            if (combined.Contains("device not found", StringComparison.OrdinalIgnoreCase))
                return "未找到目标设备，请检查无线 ADB 连接状态";

            var match = Regex.Match(combined, @"Failure\s*\[([^\]]+)\]", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return $"Android系统报错: {match.Groups[1].Value}";
            }

            var lines = combined.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();
            var lastMeaningful = lines.LastOrDefault(l => !l.Contains("Performing Streamed Install", StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(lastMeaningful) ? lastMeaningful : (!string.IsNullOrWhiteSpace(error) ? error : output);
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

            onProgress?.Invoke(20, $"正在向 [{target}] 安装 APK: {Path.GetFileName(apkPath)}...");
            var sw = Stopwatch.StartNew();

            // 1. 首选推荐参数: -r (覆盖) -d (允许降级) -t (允许测试包) -g (授予所有权限)
            // 默认超时提升至 600 秒 (10 分钟)，满足数 GB 大型 VR 游戏包安装
            var res = await RunAdbAsync($"-s \"{target}\" install -r -d -t -g \"{apkPath}\"", 600000, ct);

            string combined = $"{res.Output}\n{res.Error}".Trim();
            if (combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                sw.Stop();
                var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);
                onProgress?.Invoke(100, $"✅ 安装成功 (耗时 {sec}s)");
                return (true, $"安装成功 (耗时 {sec}s)");
            }

            // 2. 若失败且原因是 -g 参数不被支持，自动降级为 -r -d -t 重试
            if (combined.Contains("unknown option -g", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Unknown option: -g", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(40, $"正在以兼容参数重试安装...");
                res = await RunAdbAsync($"-s \"{target}\" install -r -d -t \"{apkPath}\"", 600000, ct);
                combined = $"{res.Output}\n{res.Error}".Trim();
                if (combined.Contains("Success", StringComparison.OrdinalIgnoreCase))
                {
                    sw.Stop();
                    var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    onProgress?.Invoke(100, $"✅ 兼容安装成功 (耗时 {sec}s)");
                    return (true, $"兼容安装成功 (耗时 {sec}s)");
                }
            }

            // 3. 若流式安装因无线 ADB 网络中断，且文件较大，自动启用两阶段推流安装 (push 到 /data/local/tmp 后本地 pm install)
            var fileInfo = new FileInfo(apkPath);
            bool isNetworkBreak = combined.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                                  combined.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
                                  combined.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase);

            if (isNetworkBreak && fileInfo.Length > 100L * 1024L * 1024L)
            {
                onProgress?.Invoke(50, $"检测到网络流式中断，正在启用两阶段稳定安装...");
                string remoteTmpApk = $"/data/local/tmp/pico_install_{Guid.NewGuid():N}.apk";
                var pushRes = await RunAdbAsync($"-s \"{target}\" push \"{apkPath}\" \"{remoteTmpApk}\"", 600000, ct);
                if (pushRes.ExitCode == 0)
                {
                    var pmRes = await RunAdbAsync($"-s \"{target}\" shell pm install -r -d -t -g \"{remoteTmpApk}\"", 300000, ct);
                    // 清理远程临时包
                    await RunAdbAsync($"-s \"{target}\" shell rm -f \"{remoteTmpApk}\"", 10000, ct);
                    string pmCombined = $"{pmRes.Output}\n{pmRes.Error}".Trim();
                    if (pmCombined.Contains("Success", StringComparison.OrdinalIgnoreCase))
                    {
                        sw.Stop();
                        var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);
                        onProgress?.Invoke(100, $"✅ 两阶段稳定安装成功 (耗时 {sec}s)");
                        return (true, $"两阶段稳定安装成功 (耗时 {sec}s)");
                    }
                    combined = pmCombined;
                }
            }

            sw.Stop();
            string errMsg = ParseAdbInstallError(res.Output, res.Error);
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

            string target = SanitizeShellArg(match?.Target ?? snOrTarget);
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            string normalized = SanitizeShellArg(remotePath).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized))
                return (false, "远程路径包含非法控制字符");
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
            string cleanPkg = SanitizeShellArg(packageName);
            if (string.IsNullOrWhiteSpace(cleanPkg))
                return (false, "包名不能为空或包含非法控制字符");

            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = SanitizeShellArg(match?.Target ?? snOrTarget);
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            var res = await RunAdbAsync($"-s \"{target}\" uninstall \"{cleanPkg}\"", 30000, ct);
            if (res.Output.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"卸载成功 ({cleanPkg})");
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

            string target = SanitizeShellArg(match?.Target ?? snOrTarget);
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

        private async Task TriggerMediaScannerAsync(string target, string remoteFilePath, CancellationToken ct = default)
        {
            try
            {
                string ext = Path.GetExtension(remoteFilePath).ToLowerInvariant();
                var mediaExts = new HashSet<string> { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp3", ".wav", ".ogg", ".apk" };
                if (mediaExts.Contains(ext))
                {
                    await RunAdbAsync($"-s \"{target}\" shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d \"file://{remoteFilePath}\"", 3000, ct);
                }
            }
            catch
            {
                // 广播失败不阻断传输主流程
            }
        }

        public async Task<(bool Success, string Message)> PushFileOrFolderAsync(
            string snOrTarget,
            string localPath,
            string remoteTargetPath,
            bool overwrite = true,
            bool flatten = false,
            Action<int, string>? onProgress = null,
            CancellationToken ct = default)
        {
            bool isFile = File.Exists(localPath);
            bool isDir = Directory.Exists(localPath);
            if (!isFile && !isDir)
                return (false, $"本地路径不存在: {localPath}");

            var devList = await GetConnectedDevicesAsync();
            var match = devList.FirstOrDefault(d =>
                string.Equals(d.Sn, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Target, snOrTarget, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Ip, snOrTarget, StringComparison.OrdinalIgnoreCase));

            string target = SanitizeShellArg(match?.Target ?? snOrTarget);
            if (string.IsNullOrWhiteSpace(target))
                return (false, "未指定有效的目标设备");

            string normalizedRemote = SanitizeShellArg(remoteTargetPath).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalizedRemote))
                return (false, "远程目标路径包含非法控制字符");
            if (!normalizedRemote.StartsWith('/')) normalizedRemote = "/" + normalizedRemote;

            if (isFile)
            {
                string fileName = Path.GetFileName(localPath);
                string targetDir;
                string destRemoteFile;

                bool hasExtension = Path.HasExtension(normalizedRemote);
                if (normalizedRemote.EndsWith('/') || !hasExtension)
                {
                    // 目标路径未显式指定文件名后缀，一律严格作为目录处理，保留源文件名
                    targetDir = normalizedRemote.TrimEnd('/');
                    destRemoteFile = $"{targetDir}/{fileName}";
                }
                else
                {
                    // 目标路径显式包含了文件扩展名（如 /sdcard/Download/custom.mp4）
                    destRemoteFile = normalizedRemote;
                    targetDir = Path.GetDirectoryName(destRemoteFile)?.Replace('\\', '/') ?? "/sdcard";
                }

                // 防呆自愈修复：若该目标目录此前曾被误推为一个同名无后缀普通文件（导致出现“无法识别文件类型”），先将其移除以确保能成功建立目录
                await RunAdbAsync($"-s \"{target}\" shell sh -c \"if [ -f \\\"{targetDir}\\\" ]; then rm -f \\\"{targetDir}\\\"; fi\"", 5000, ct);

                // 1. 确保目标父目录一定存在
                await RunAdbAsync($"-s \"{target}\" shell mkdir -p \"{targetDir}\"", 8000, ct);

                // 2. 若开启覆盖，仅针对该具体同名文件执行 rm -f 删除，绝不可删除父目录！
                if (overwrite)
                {
                    await RunAdbAsync($"-s \"{target}\" shell rm -f \"{destRemoteFile}\"", 8000, ct);
                }

                onProgress?.Invoke(30, $"正在向 [{target}] 推送文件: {fileName}...");
                var sw = Stopwatch.StartNew();
                var res = await RunAdbAsync($"-s \"{target}\" push \"{localPath}\" \"{destRemoteFile}\"", 600000, ct);
                sw.Stop();
                var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);

                string combined = $"{res.Output}\n{res.Error}".Trim();
                if (res.ExitCode == 0 && (combined.Contains("pushed", StringComparison.OrdinalIgnoreCase) || combined.Contains("1 file pushed") || string.IsNullOrEmpty(res.Error)))
                {
                    // 广播通知 Android 媒体扫描器刷新，使头显文件管理器即刻识别文件类型
                    _ = TriggerMediaScannerAsync(target, destRemoteFile, ct);
                    onProgress?.Invoke(100, $"✅ 传输完成 (耗时 {sec}s)");
                    return (true, $"传输成功 (耗时 {sec}s): {fileName}");
                }

                string err = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
                onProgress?.Invoke(100, $"❌ 传输失败: {err}");
                return (false, err);
            }
            else
            {
                // 目录推送
                string targetDir = normalizedRemote.TrimEnd('/');
                // 防呆自愈修复：若目标目录此前曾被误建为一个普通文件，先将其移除
                await RunAdbAsync($"-s \"{target}\" shell sh -c \"if [ -f \\\"{targetDir}\\\" ]; then rm -f \\\"{targetDir}\\\"; fi\"", 5000, ct);
                await RunAdbAsync($"-s \"{target}\" shell mkdir -p \"{targetDir}\"", 8000, ct);

                var sw = Stopwatch.StartNew();

                if (flatten)
                {
                    // 【展开传输】：将 localPath 内部所有直接文件与子文件夹，平铺推送到 targetDir 下，不再多包一层外层文件夹名
                    var dirInfo = new DirectoryInfo(localPath);
                    var files = dirInfo.GetFiles();
                    var subdirs = dirInfo.GetDirectories();
                    int totalItems = files.Length + subdirs.Length;

                    if (totalItems == 0)
                    {
                        return (true, "所选目录为空，已在头显同步确保目标目录存在");
                    }

                    onProgress?.Invoke(20, $"正在向 [{target}] 展开推送目录内 {totalItems} 项资源...");

                    int successCount = 0;
                    var failList = new List<string>();

                    // 1. 推送所有顶层文件
                    foreach (var f in files)
                    {
                        string destFile = $"{targetDir}/{f.Name}";
                        if (overwrite)
                        {
                            await RunAdbAsync($"-s \"{target}\" shell rm -f \"{destFile}\"", 5000, ct);
                        }
                        var pRes = await RunAdbAsync($"-s \"{target}\" push \"{f.FullName}\" \"{destFile}\"", 300000, ct);
                        if (pRes.ExitCode == 0)
                        {
                            successCount++;
                            _ = TriggerMediaScannerAsync(target, destFile, ct);
                        }
                        else
                        {
                            failList.Add(f.Name);
                        }
                    }

                    // 2. 推送所有顶层子目录
                    foreach (var d in subdirs)
                    {
                        var pRes = await RunAdbAsync($"-s \"{target}\" push \"{d.FullName}\" \"{targetDir}/\"", 300000, ct);
                        if (pRes.ExitCode == 0)
                        {
                            successCount++;
                        }
                        else
                        {
                            failList.Add(d.Name);
                        }
                    }

                    sw.Stop();
                    var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);

                    if (failList.Count == 0)
                    {
                        onProgress?.Invoke(100, $"✅ 展开传输完成 (共 {totalItems} 项，耗时 {sec}s)");
                        return (true, $"展开传输成功 (共 {totalItems} 项，耗时 {sec}s)");
                    }
                    else
                    {
                        string msg = $"展开传输部分成功: {successCount}/{totalItems}，失败: {string.Join(", ", failList.Take(3))}";
                        onProgress?.Invoke(100, $"⚠️ {msg}");
                        return (false, msg);
                    }
                }
                else
                {
                    // 【保留外层文件夹】：原样推送整个文件夹到目标目录下（将在 targetDir 下创建该文件夹同名目录）
                    onProgress?.Invoke(30, $"正在向 [{target}] 推送完整文件夹: {Path.GetFileName(localPath)}...");
                    var res = await RunAdbAsync($"-s \"{target}\" push \"{localPath}\" \"{targetDir}/\"", 600000, ct);
                    sw.Stop();
                    var sec = Math.Round(sw.Elapsed.TotalSeconds, 1);

                    string combined = $"{res.Output}\n{res.Error}".Trim();
                    if (res.ExitCode == 0 && (combined.Contains("pushed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(res.Error)))
                    {
                        onProgress?.Invoke(100, $"✅ 目录传输完成 (耗时 {sec}s)");
                        return (true, $"目录传输成功 (耗时 {sec}s)");
                    }

                    string err = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
                    onProgress?.Invoke(100, $"❌ 目录传输失败: {err}");
                    return (false, err);
                }
            }
        }
    }
}

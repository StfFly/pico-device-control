using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PicoDeployer.Models;

namespace PicoDeployer.Core
{
    public class AdbManager
    {
        private string _adbPath = "adb";

        public AdbManager(string adbPath = "adb")
        {
            _adbPath = ResolveAdbPath(adbPath);
        }

        public string AdbPath => _adbPath;

        public void SetAdbPath(string path)
        {
            _adbPath = ResolveAdbPath(path);
        }

        public static string ResolveAdbPath(string? customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
                return customPath;

            var candidates = new[]
            {
                customPath ?? "adb",
                @"G:\Program Files\platform-tools\adb.exe",
                @"C:\Program Files\platform-tools\adb.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools", "adb.exe")
            };

            foreach (var cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                if (File.Exists(cand)) return cand;
            }

            return "adb"; // fallback to system PATH
        }

        public async Task<(int ExitCode, string Output, string Error)> RunAdbAsync(
            string arguments, 
            int timeoutMs = 60000, 
            Action<string>? onOutputLine = null, 
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

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    outSb.AppendLine(e.Data);
                    onOutputLine?.Invoke(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    errSb.AppendLine(e.Data);
                    onOutputLine?.Invoke(e.Data);
                }
            };

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
                return (-1, outSb.ToString().Trim(), "操作超时或已被取消");
            }
            catch (Exception ex)
            {
                return (-2, outSb.ToString().Trim(), $"执行 ADB 出错: {ex.Message}");
            }
        }

        public async Task<bool> ConnectAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            var res = await RunAdbAsync($"connect {target}", 8000);
            return res.Output.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
                   res.Output.Contains("already connected", StringComparison.OrdinalIgnoreCase);
        }

        public async Task DisconnectAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            await RunAdbAsync($"disconnect {target}", 5000);
        }

        public async Task<HashSet<string>> GetConnectedAdbTargetsAsync()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = await RunAdbAsync("devices", 8000);
            var lines = res.Output.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("List of devices")) continue;
                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(parts[0]);
                }
            }
            return set;
        }

        public async Task RefreshDeviceDetailsAsync(DeviceInfo dev)
        {
            var target = dev.AdbTarget;
            if (string.IsNullOrWhiteSpace(target)) return;

            // 1. 确保已连接
            var connected = await ConnectAsync(target);
            dev.IsConnected = connected;
            dev.LastChecked = DateTime.Now;

            if (!connected) return;

            // 2. 获取型号
            var modelRes = await RunAdbAsync($"-s {target} shell getprop ro.product.model", 4000);
            if (!string.IsNullOrWhiteSpace(modelRes.Output) && modelRes.ExitCode == 0)
            {
                dev.Model = modelRes.Output.Trim();
            }

            // 3. 获取 Android 版本
            var verRes = await RunAdbAsync($"-s {target} shell getprop ro.build.version.release", 4000);
            if (!string.IsNullOrWhiteSpace(verRes.Output) && verRes.ExitCode == 0)
            {
                dev.AndroidVersion = verRes.Output.Trim();
            }

            // 4. 获取电量
            var batRes = await RunAdbAsync($"-s {target} shell \"dumpsys battery | grep level\"", 4000);
            if (!string.IsNullOrWhiteSpace(batRes.Output))
            {
                var match = Regex.Match(batRes.Output, @"level:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
                {
                    dev.Battery = level;
                }
            }

            // 5. 自动探测并读取真实硬件 SN (若此前未记录或包含非SN字符)
            if (string.IsNullOrWhiteSpace(dev.Sn) || dev.Sn.Contains(":") || dev.Sn.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                var detectedSn = await ReadDeviceSnAsync(target);
                if (!string.IsNullOrWhiteSpace(detectedSn))
                {
                    dev.Sn = detectedSn;
                }
            }
        }

        public async Task<string> ReadDeviceSnAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return string.Empty;

            // 1. Android 标准硬件 SN 属性 (PICO Neo3 / PICO 4 / Enterprise / Ultra 均支持)
            var res = await RunAdbAsync($"-s {target} shell getprop ro.serialno", 4000);
            var sn = res.Output.Trim();
            if (IsValidSn(sn)) return sn;

            // 2. Bootloader 阶段 SN 属性
            res = await RunAdbAsync($"-s {target} shell getprop ro.boot.serialno", 4000);
            sn = res.Output.Trim();
            if (IsValidSn(sn)) return sn;

            // 3. ADB 原生 get-serialno
            res = await RunAdbAsync($"-s {target} get-serialno", 4000);
            sn = res.Output.Trim();
            if (IsValidSn(sn)) return sn;

            // 4. Product SN 备选属性
            res = await RunAdbAsync($"-s {target} shell getprop ro.product.serialno", 4000);
            sn = res.Output.Trim();
            if (IsValidSn(sn)) return sn;

            return string.Empty;
        }

        private static bool IsValidSn(string? sn)
        {
            if (string.IsNullOrWhiteSpace(sn)) return false;
            if (sn.Contains("unknown", StringComparison.OrdinalIgnoreCase)) return false;
            if (sn.Contains("error", StringComparison.OrdinalIgnoreCase)) return false;
            if (sn.Contains("offline", StringComparison.OrdinalIgnoreCase)) return false;
            if (sn.Contains("device", StringComparison.OrdinalIgnoreCase)) return false;
            if (sn.Contains(":")) return false; // 排除类似 192.168.1.101:5555
            return sn.Length >= 6;
        }

        public async Task<(bool Success, string Message)> InstallApkAsync(
            string target, 
            string apkPath, 
            Action<int, string>? onProgress, 
            CancellationToken ct = default)
        {
            if (!File.Exists(apkPath))
                return (false, $"本地 APK 文件不存在: {apkPath}");

            onProgress?.Invoke(10, "正在建立无线 ADB 连接...");
            var conn = await ConnectAsync(target);
            if (!conn)
                return (false, $"无法连接到目标设备 {target}，请确认头显开机并处于同一局域网");

            ct.ThrowIfCancellationRequested();

            onProgress?.Invoke(25, "正在传输与安装 APK (流式部署)...");

            var sw = Stopwatch.StartNew();
            // -r: 覆盖安装 -d: 降级支持 -g: 授予所有运行时权限免头戴弹窗
            var res = await RunAdbAsync($"-s {target} install -r -d -g \"{apkPath}\"", 300000, line =>
            {
                if (line.Contains("Performing Streamed Install", StringComparison.OrdinalIgnoreCase))
                {
                    onProgress?.Invoke(40, "正在向头显推流写入 APK 数据...");
                }
                else if (line.Contains("%", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(line, @"(\d+)%");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
                    {
                        var scaled = 25 + (int)(pct * 0.5);
                        onProgress?.Invoke(scaled, $"正在写入 APK 数据 ({pct}%)...");
                    }
                }
            }, ct);

            sw.Stop();
            var duration = Math.Round(sw.Elapsed.TotalSeconds, 1);

            if (res.Output.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(100, $"✅ 安装成功 (耗时 {duration}s)");
                return (true, $"安装成功 (耗时 {duration}s)");
            }

            var errMsg = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
            onProgress?.Invoke(100, $"❌ 安装失败: {errMsg}");
            return (false, errMsg);
        }

        public async Task<(bool Success, string Message)> PushFileAsync(
            string target, 
            string localFilePath, 
            string remoteTargetPath, 
            Action<int, string>? onProgress, 
            CancellationToken ct = default)
        {
            if (!File.Exists(localFilePath))
                return (false, $"本地文件不存在: {localFilePath}");

            onProgress?.Invoke(10, "检查设备无线连接...");
            var conn = await ConnectAsync(target);
            if (!conn)
                return (false, $"无法连接设备 {target}");

            ct.ThrowIfCancellationRequested();

            // 确保远程父目录存在
            var remoteDir = Path.GetDirectoryName(remoteTargetPath.Replace('\\', '/'))?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(remoteDir))
            {
                onProgress?.Invoke(20, $"初始化目标存储目录 {remoteDir}...");
                await RunAdbAsync($"-s {target} shell mkdir -p \"{remoteDir}\"", 8000, null, ct);
            }

            onProgress?.Invoke(40, "正在推送文件到头显存储...");
            var sw = Stopwatch.StartNew();

            var res = await RunAdbAsync($"-s {target} push \"{localFilePath}\" \"{remoteTargetPath.Replace('\\', '/')}\"", 180000, line =>
            {
                if (line.Contains("%"))
                {
                    var match = Regex.Match(line, @"(\d+)%");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
                    {
                        var scaled = 40 + (int)(pct * 0.55);
                        onProgress?.Invoke(scaled, $"传输中 ({pct}%)...");
                    }
                }
            }, ct);

            sw.Stop();
            var duration = Math.Round(sw.Elapsed.TotalSeconds, 1);

            if (res.ExitCode == 0 && (res.Output.Contains("pushed") || res.Output.Contains("file pushed") || string.IsNullOrEmpty(res.Error)))
            {
                onProgress?.Invoke(100, $"✅ 传输完成 (耗时 {duration}s)");
                return (true, $"传输成功 ({res.Output})");
            }

            var err = !string.IsNullOrWhiteSpace(res.Error) ? res.Error : res.Output;
            onProgress?.Invoke(100, $"❌ 传输失败: {err}");
            return (false, err);
        }

        public async Task RebootAsync(string target)
        {
            await RunAdbAsync($"-s {target} reboot", 5000);
        }

        public async Task StartAppAsync(string target, string packageName)
        {
            await RunAdbAsync($"-s {target} shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1", 8000);
        }

        public async Task StopAppAsync(string target, string packageName)
        {
            await RunAdbAsync($"-s {target} shell am force-stop {packageName}", 5000);
        }
    }
}

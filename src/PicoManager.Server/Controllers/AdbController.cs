using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdbController : ControllerBase
    {
        private readonly AdbService _adbService;
        private readonly ConfigService _configService;
        private readonly PicoDeviceManager _deviceManager;

        public AdbController(AdbService adbService, ConfigService configService, PicoDeviceManager deviceManager)
        {
            _adbService = adbService;
            _configService = configService;
            _deviceManager = deviceManager;
        }

        [HttpGet("devices")]
        public async Task<IActionResult> GetDevices()
        {
            var devices = await _adbService.GetConnectedDevicesAsync();
            return Ok(new
            {
                success = true,
                count = devices.Count,
                devices
            });
        }

        [HttpGet("subnets")]
        public IActionResult GetSubnets()
        {
            var subnets = _adbService.DetectLanSubnets();
            return Ok(new { success = true, subnets });
        }

        [HttpPost("connect")]
        public async Task<IActionResult> Connect([FromBody] AdbConnectRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Ip))
            {
                return BadRequest(new { success = false, message = "IP 地址不能为空" });
            }

            int port = req.Port > 0 ? req.Port : 5555;
            bool ok = await _adbService.ConnectAsync(req.Ip.Trim(), port);
            if (ok)
            {
                // 连接成功后尝试读取 SN
                string target = port == 5555 ? req.Ip.Trim() : $"{req.Ip.Trim()}:{port}";
                string sn = await _adbService.ReadDeviceSnAsync(target);
                return Ok(new { success = true, target, sn, message = $"成功连接无线 ADB ({target})" });
            }

            return Ok(new { success = false, message = $"连接失败，请确认头戴已开机并处于同一局域网内 (TCP {port})" });
        }

        [HttpPost("disconnect")]
        public async Task<IActionResult> Disconnect([FromBody] AdbTargetRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Target)) return BadRequest();
            bool ok = await _adbService.DisconnectAsync(req.Target.Trim());
            return Ok(new { success = ok });
        }

        [HttpPost("scan")]
        public async Task<IActionResult> Scan([FromBody] AdbScanRequest? req)
        {
            var subnet = req?.Subnet?.Trim();
            var devices = await _adbService.ScanAndAutoConnectAsync(subnet, 600);
            return Ok(new
            {
                success = true,
                discoveredCount = devices.Count,
                devices
            });
        }

        [HttpPost("read-sn")]
        public async Task<IActionResult> ReadSn([FromBody] AdbTargetRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Target)) return BadRequest();
            string sn = await _adbService.ReadDeviceSnAsync(req.Target.Trim());
            return Ok(new { success = !string.IsNullOrWhiteSpace(sn), sn });
        }

        [HttpPost("reboot")]
        public async Task<IActionResult> Reboot([FromBody] AdbTargetRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Target)) return BadRequest();
            bool ok = await _adbService.RebootAsync(req.Target.Trim());
            return Ok(new { success = ok });
        }

        [HttpPost("sync-to-config")]
        public async Task<IActionResult> SyncToConfig([FromBody] AdbSyncRequest req)
        {
            var devices = await _adbService.GetConnectedDevicesAsync();
            int synced = 0;

            foreach (var dev in devices.Where(d => d.IsConnected && !string.IsNullOrWhiteSpace(d.Sn)))
            {
                var existingAlias = _configService.GetAlias(dev.Sn);
                if (existingAlias == dev.Sn)
                {
                    // 尚未配置机号，自动分配或使用默认格式
                    string newAlias = $"{synced + 1:D2}号机";
                    _configService.SetAlias(dev.Sn, newAlias);
                    _deviceManager.RegisterHeadset(dev.Sn, newAlias, dev.Model);
                    synced++;
                }
            }

            return Ok(new { success = true, syncedCount = synced });
        }

        [HttpGet("packages")]
        public async Task<IActionResult> GetPackages([FromQuery] string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return BadRequest(new { success = false, message = "目标设备不能为空" });
            var pkgs = await _adbService.ListInstalledPackagesAsync(target.Trim());
            return Ok(new { success = true, packages = pkgs });
        }

        [HttpPost("upload-install")]
        [RequestSizeLimit(1024L * 1024L * 1024L * 16L)] // 16GB 大安装包支持
        public async Task<IActionResult> UploadAndInstallApk(
            [FromForm] Microsoft.AspNetCore.Http.IFormFile? file,
            [FromForm] string? targets)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, message = "请先选择要上传安装的 APK 文件" });

            if (!file.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { success = false, message = "仅支持 .apk 安装包格式" });

            var tempDir = Path.Combine(Path.GetTempPath(), "PicoManager_Apks");
            var sessionDir = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            // 物理文件名保留原始文件名，杜绝任何哈希污染
            var tempApk = Path.Combine(sessionDir, file.FileName);

            try
            {
                using (var fs = new FileStream(tempApk, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs);
                }

                var targetList = ParseTargets(targets);
                if (targetList.Count == 0)
                {
                    var connected = await _adbService.GetConnectedDevicesAsync();
                    targetList = connected.Where(d => d.IsConnected).Select(d => d.Target).ToList();
                }

                if (targetList.Count == 0)
                {
                    return BadRequest(new { success = false, message = "未检测到可安装的已连接 ADB 设备" });
                }

                // 采用 4 线程受控并发安装，杜绝串行耗时过长与网络拥塞
                var results = new ConcurrentBag<object>();
                using var sem = new SemaphoreSlim(4);
                var installTasks = targetList.Select(async t =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var (ok, msg) = await _adbService.InstallApkAsync(t, tempApk);
                        results.Add(new { target = t, success = ok, message = msg });
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                await Task.WhenAll(installTasks);
                return Ok(new { success = true, total = targetList.Count, results = results.ToList() });
            }
            finally
            {
                try { Directory.Delete(sessionDir, true); } catch { }
            }
        }

        [HttpPost("local-install")]
        public async Task<IActionResult> LocalInstallApk([FromBody] AdbLocalInstallRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.LocalPath) || !System.IO.File.Exists(req.LocalPath))
                return BadRequest(new { success = false, message = $"服务器本地 APK 文件不存在: {req.LocalPath}" });

            if (!req.LocalPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { success = false, message = "仅支持 .apk 安装包格式" });

            var targetList = req.Targets != null && req.Targets.Count > 0
                ? req.Targets
                : (await _adbService.GetConnectedDevicesAsync()).Where(d => d.IsConnected).Select(d => d.Target).ToList();

            if (targetList.Count == 0)
            {
                return BadRequest(new { success = false, message = "未检测到可安装的已连接 ADB 设备" });
            }

            var results = new ConcurrentBag<object>();
            using var sem = new SemaphoreSlim(4);
            var installTasks = targetList.Select(async t =>
            {
                await sem.WaitAsync();
                try
                {
                    var (ok, msg) = await _adbService.InstallApkAsync(t, req.LocalPath);
                    results.Add(new { target = t, success = ok, message = msg });
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(installTasks);
            return Ok(new { success = true, total = targetList.Count, results = results.ToList() });
        }

        [HttpPost("uninstall")]
        public async Task<IActionResult> Uninstall([FromBody] AdbUninstallRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.PackageName))
                return BadRequest(new { success = false, message = "包名不能为空" });

            var targetList = req.Targets != null && req.Targets.Count > 0
                ? req.Targets
                : (await _adbService.GetConnectedDevicesAsync()).Where(d => d.IsConnected).Select(d => d.Target).ToList();

            if (targetList.Count == 0)
                return BadRequest(new { success = false, message = "未检测到可操作的已连接 ADB 设备" });

            var results = new List<object>();
            foreach (var t in targetList)
            {
                var (ok, msg) = await _adbService.UninstallAppAsync(t, req.PackageName);
                results.Add(new { target = t, success = ok, message = msg });
            }

            return Ok(new { success = true, total = targetList.Count, results });
        }

        [HttpPost("upload-push")]
        [RequestSizeLimit(1024L * 1024L * 1024L * 16L)] // 16GB 支持
        public async Task<IActionResult> UploadAndPush(
            [FromForm] Microsoft.AspNetCore.Http.IFormFile? file,
            [FromForm] string? remotePath,
            [FromForm] string? targets,
            [FromForm] bool overwrite = true,
            [FromForm] bool flatten = false)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { success = false, message = "请先选择要上传的文件" });

            if (string.IsNullOrWhiteSpace(remotePath))
                remotePath = "/sdcard/Download/";

            var tempDir = Path.Combine(Path.GetTempPath(), "PicoManager_Transfers");
            var sessionDir = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            // 物理文件名保留原始文件名，杜绝任何内存 GUID 哈希污染
            var tempFile = Path.Combine(sessionDir, file.FileName);

            try
            {
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs);
                }

                string actualPushPath = tempFile;
                string actualRemotePath = AdbService.SanitizeShellArg(remotePath.Trim());

                // 若上传的是 zip 压缩包且目标希望解压传输
                bool isZip = file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                if (isZip)
                {
                    // 若不展开，使用 ZIP 文件名作为文件夹名称，避免在头显端生成莫名其妙的 "unzipped" 目录
                    string zipStem = Path.GetFileNameWithoutExtension(file.FileName);
                    if (string.IsNullOrWhiteSpace(zipStem)) zipStem = "Archive";
                    var targetExtractDir = flatten ? Path.Combine(sessionDir, "unzipped") : Path.Combine(sessionDir, zipStem);

                    try
                    {
                        SafeZipExtractor.ExtractToDirectorySafe(tempFile, targetExtractDir, true);
                        actualPushPath = targetExtractDir;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ADB] 解压 ZIP 失败或触发安全拦截: {ex.Message}，降级为原始压缩文件直接推送");
                        actualPushPath = tempFile;
                    }
                }

                var targetList = ParseTargets(targets);
                if (targetList.Count == 0)
                {
                    var connected = await _adbService.GetConnectedDevicesAsync();
                    targetList = connected.Where(d => d.IsConnected).Select(d => d.Target).ToList();
                }

                if (targetList.Count == 0)
                {
                    return BadRequest(new { success = false, message = "未检测到可推送的已连接 ADB 设备" });
                }

                var results = new ConcurrentBag<object>();
                using var sem = new SemaphoreSlim(4);
                var pushTasks = targetList.Select(async t =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var (ok, msg) = await _adbService.PushFileOrFolderAsync(t, actualPushPath, actualRemotePath, overwrite, flatten: flatten);
                        results.Add(new { target = t, success = ok, message = msg });
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                await Task.WhenAll(pushTasks);
                return Ok(new { success = true, total = targetList.Count, results = results.ToList() });
            }
            finally
            {
                try { Directory.Delete(sessionDir, true); } catch { }
            }
        }

        [HttpPost("local-push")]
        public async Task<IActionResult> LocalPushFileOrFolder([FromBody] AdbLocalPushRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.LocalPath) || (!System.IO.File.Exists(req.LocalPath) && !System.IO.Directory.Exists(req.LocalPath)))
                return BadRequest(new { success = false, message = $"服务器本地路径不存在: {req.LocalPath}" });

            string remotePath = string.IsNullOrWhiteSpace(req.RemotePath) ? "/sdcard/Download/" : req.RemotePath.Trim();

            var targetList = req.Targets != null && req.Targets.Count > 0
                ? req.Targets
                : (await _adbService.GetConnectedDevicesAsync()).Where(d => d.IsConnected).Select(d => d.Target).ToList();

            if (targetList.Count == 0)
            {
                return BadRequest(new { success = false, message = "未检测到可推送的已连接 ADB 设备" });
            }

            var results = new ConcurrentBag<object>();
            using var sem = new SemaphoreSlim(4);
            var pushTasks = targetList.Select(async t =>
            {
                await sem.WaitAsync();
                try
                {
                    var (ok, msg) = await _adbService.PushFileOrFolderAsync(t, req.LocalPath, remotePath, req.Overwrite, flatten: req.Flatten);
                    results.Add(new { target = t, success = ok, message = msg });
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(pushTasks);
            return Ok(new { success = true, total = targetList.Count, results = results.ToList() });
        }

        [HttpPost("mkdir")]
        public async Task<IActionResult> CreateFolder([FromBody] AdbMkdirRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.RemotePath))
                return BadRequest(new { success = false, message = "远程目录路径不能为空" });

            var targetList = req.Targets != null && req.Targets.Count > 0
                ? req.Targets
                : (await _adbService.GetConnectedDevicesAsync()).Where(d => d.IsConnected).Select(d => d.Target).ToList();

            if (targetList.Count == 0)
                return BadRequest(new { success = false, message = "未检测到可操作的已连接 ADB 设备" });

            var results = new List<object>();
            foreach (var t in targetList)
            {
                var (ok, msg) = await _adbService.CreateFolderAsync(t, req.RemotePath);
                results.Add(new { target = t, success = ok, message = msg });
            }

            return Ok(new { success = true, total = targetList.Count, results });
        }

        [HttpPost("screen")]
        public async Task<IActionResult> SetScreen([FromBody] AdbScreenRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Target)) return BadRequest();
            await _adbService.SetScreenStateAsync(req.Target.Trim(), req.ScreenOn);
            return Ok(new { success = true });
        }

        private List<string> ParseTargets(string? targets)
        {
            if (string.IsNullOrWhiteSpace(targets)) return new List<string>();
            return targets.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => AdbService.SanitizeShellArg(t.Trim()))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
        }
    }

    public class AdbLocalInstallRequest
    {
        public string LocalPath { get; set; } = string.Empty;
        public List<string>? Targets { get; set; }
    }

    public class AdbLocalPushRequest
    {
        public string LocalPath { get; set; } = string.Empty;
        public string? RemotePath { get; set; }
        public List<string>? Targets { get; set; }
        public bool Overwrite { get; set; } = true;
        public bool Flatten { get; set; } = false;
    }

    public class AdbMkdirRequest
    {
        public string RemotePath { get; set; } = string.Empty;
        public List<string>? Targets { get; set; }
    }

    public class AdbUninstallRequest
    {
        public string PackageName { get; set; } = string.Empty;
        public List<string>? Targets { get; set; }
    }

    public class AdbScreenRequest
    {
        public string Target { get; set; } = string.Empty;
        public bool ScreenOn { get; set; } = true;
    }

    public class AdbConnectRequest
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; } = 5555;
    }

    public class AdbTargetRequest
    {
        public string Target { get; set; } = string.Empty;
    }

    public class AdbScanRequest
    {
        public string? Subnet { get; set; }
    }

    public class AdbSyncRequest
    {
        public bool OverwriteExisting { get; set; } = false;
    }
}

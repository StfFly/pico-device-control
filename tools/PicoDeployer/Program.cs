using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PicoDeployer.Core;
using PicoDeployer.Models;

namespace PicoDeployer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // 1. 检查是否为纯命令行执行模式 (CLI Mode)
            if (args.Length > 0 && !args[0].Equals("--web", StringComparison.OrdinalIgnoreCase))
            {
                await RunCliModeAsync(args);
                return;
            }

            // 2. Web 可视化工作台模式 (Default Mode)
            await RunWebModeAsync(args);
        }

        private static async Task RunWebModeAsync(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            var builder = WebApplication.CreateBuilder(args);

            var repo = new DeviceRepository();
            var adb = new AdbManager(repo.Settings.AdbPath);
            var engine = new DeploymentEngine(adb);

            builder.Services.AddSingleton(repo);
            builder.Services.AddSingleton(adb);
            builder.Services.AddSingleton(engine);
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
                options.SerializerOptions.WriteIndented = true;
            });

            var webPort = repo.Settings.WebPort > 0 ? repo.Settings.WebPort : 9005;
            builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");

            var app = builder.Build();

            app.UseDefaultFiles();
            app.UseStaticFiles();

            // API 路由
            app.MapGet("/api/devices", (DeviceRepository r) => Results.Ok(r.GetDevices()));

            app.MapPost("/api/devices/refresh", async (DeviceRepository r, AdbManager a) =>
            {
                var devs = r.GetDevices();
                var tasks = devs.Select(d => a.RefreshDeviceDetailsAsync(d));
                await Task.WhenAll(tasks);
                r.Save();
                return Results.Ok(devs);
            });

            app.MapPost("/api/devices/upsert", async (DeviceInfo dev, DeviceRepository r, AdbManager a) =>
            {
                if (string.IsNullOrWhiteSpace(dev.DeviceNo) && string.IsNullOrWhiteSpace(dev.Ip))
                {
                    return Results.BadRequest(new { error = "机号和 IP 地址至少需填写一项" });
                }

                // 1. 保存/更新基础配置
                r.UpsertDevice(dev);

                // 2. 若配置了无线 IP，尝试立即连接 ADB 自动探测并读取 SN 码
                string autoSnMsg = string.Empty;
                if (!string.IsNullOrWhiteSpace(dev.Ip))
                {
                    var targetDev = r.GetDevice(dev.DeviceNo) ?? r.GetDevice(dev.Ip);
                    if (targetDev != null)
                    {
                        var probeTask = Task.Run(async () =>
                        {
                            await a.RefreshDeviceDetailsAsync(targetDev);
                            r.Save();
                            return targetDev.Sn;
                        });

                        // 快速尝试 3.5 秒探测（避免头戴离线时长时间阻塞前端）
                        var completed = await Task.WhenAny(probeTask, Task.Delay(3500));
                        if (completed == probeTask)
                        {
                            var detectedSn = await probeTask;
                            if (!string.IsNullOrWhiteSpace(detectedSn))
                            {
                                autoSnMsg = $"，已通过无线 ADB 自动读取硬件 SN: {detectedSn}";
                            }
                        }
                        else
                        {
                            // 若超时（头显未连上 Wi-Fi 或正启动中），后台静默继续探测
                            _ = probeTask;
                        }
                    }
                }

                var msg = !string.IsNullOrWhiteSpace(autoSnMsg)
                    ? $"设备信息保存成功{autoSnMsg}！"
                    : "设备信息已保存！(头戴开机连入同一 Wi-Fi 后，系统将自动读取 SN)";

                return Results.Ok(new { success = true, message = msg, devices = r.GetDevices() });
            });

            // 主动触发单独读取某台设备的 SN 码
            app.MapPost("/api/devices/{id}/read-sn", async (string id, DeviceRepository r, AdbManager a) =>
            {
                var dev = r.GetDevice(id);
                if (dev == null) return Results.BadRequest(new { error = $"未找到指定设备: {id}" });

                await a.RefreshDeviceDetailsAsync(dev);
                r.Save();

                if (!string.IsNullOrWhiteSpace(dev.Sn))
                {
                    return Results.Ok(new 
                    { 
                        success = true, 
                        sn = dev.Sn, 
                        isConnected = dev.IsConnected, 
                        message = $"已成功通过无线 ADB 读取 SN 码: {dev.Sn}" 
                    });
                }

                return Results.Ok(new 
                { 
                    success = false, 
                    sn = "", 
                    isConnected = dev.IsConnected, 
                    message = dev.IsConnected ? "已连接但未能获取到有效 SN 码" : "无法连接到设备，请确认头戴已开机且连入局域网" 
                });
            });

            app.MapDelete("/api/devices/{id}", (string id, DeviceRepository r) =>
            {
                var removed = r.RemoveDevice(id);
                return Results.Ok(new { success = removed, devices = r.GetDevices() });
            });

            // 差异化配置在线预览
            app.MapPost("/api/config/preview", (PreviewConfigRequest req, DeviceRepository r) =>
            {
                var dev = r.GetDevice(req.DeviceNo);
                if (dev == null)
                {
                    return Results.BadRequest(new { error = $"未找到指定设备: {req.DeviceNo}" });
                }
                var rendered = ConfigTemplateEngine.GeneratePreview(req.TemplateContent, dev);
                return Results.Ok(new
                {
                    deviceNo = dev.DeviceNo,
                    displayName = dev.DisplayName,
                    ip = dev.Ip,
                    renderedContent = rendered
                });
            });

            // 从指定头显端读取现有配置文件内容
            app.MapPost("/api/config/pull", async (PullConfigRequest req, DeviceRepository r, AdbManager a) =>
            {
                var dev = r.GetDevice(req.DeviceNo);
                if (dev == null) return Results.BadRequest(new { error = $"未找到指定设备: {req.DeviceNo}" });
                if (string.IsNullOrWhiteSpace(req.RemoteTargetPath)) return Results.BadRequest(new { error = "目标存储路径不能为空" });

                var target = dev.AdbTarget;
                var connected = await a.ConnectAsync(target);
                if (!connected) return Results.BadRequest(new { error = $"无法连接到设备 {dev.DisplayName} ({target})，请确认头戴已开机连入同一 Wi-Fi" });

                var safePath = req.RemoteTargetPath.Replace('\\', '/');
                var res = await a.RunAdbAsync($"-s {target} shell \"cat '{safePath}'\"", 10000);
                if (res.ExitCode != 0 || res.Output.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = $"设备中未找到该配置文件 ({safePath})，可能尚未生成或路径有误" });
                }

                return Results.Ok(new
                {
                    success = true,
                    deviceNo = dev.DeviceNo,
                    displayName = dev.DisplayName,
                    content = res.Output
                });
            });

            // 批量下发差异化配置
            app.MapPost("/api/config/push", async (BatchConfigRequest req, DeviceRepository r, DeploymentEngine eng) =>
            {
                if (eng.IsBusy) return Results.BadRequest(new { error = "当前已有任务正在执行中" });
                if (string.IsNullOrWhiteSpace(req.TemplateContent)) return Results.BadRequest(new { error = "配置模板内容不能为空" });
                if (string.IsNullOrWhiteSpace(req.RemoteTargetPath)) return Results.BadRequest(new { error = "目标存储路径不能为空" });

                var allDevs = r.GetDevices();
                var targets = ResolveTargetDevices(allDevs, req.TargetDevices);
                if (targets.Count == 0) return Results.BadRequest(new { error = "未勾选任何有效设备" });

                _ = Task.Run(async () =>
                {
                    await eng.RunBatchConfigPushAsync(targets, req.TemplateContent, req.RemoteTargetPath, req.Concurrency);
                });

                return Results.Ok(new { success = true, targetCount = targets.Count });
            });

            // 批量安装 APK
            app.MapPost("/api/deploy/install", async (BatchInstallRequest req, DeviceRepository r, DeploymentEngine eng) =>
            {
                if (eng.IsBusy) return Results.BadRequest(new { error = "当前已有任务正在执行中" });
                if (string.IsNullOrWhiteSpace(req.ApkPath) || !File.Exists(req.ApkPath))
                {
                    return Results.BadRequest(new { error = $"本地 APK 文件不存在: {req.ApkPath}" });
                }

                var allDevs = r.GetDevices();
                var targets = ResolveTargetDevices(allDevs, req.TargetDevices);
                if (targets.Count == 0) return Results.BadRequest(new { error = "未勾选任何有效设备" });

                _ = Task.Run(async () =>
                {
                    await eng.RunBatchInstallAsync(targets, req.ApkPath, req.Concurrency, req.AutoLaunch, req.PackageName);
                });

                return Results.Ok(new { success = true, targetCount = targets.Count });
            });

            // 批量推流大文件
            app.MapPost("/api/deploy/push-file", async (BatchFilePushRequest req, DeviceRepository r, DeploymentEngine eng) =>
            {
                if (eng.IsBusy) return Results.BadRequest(new { error = "当前已有任务正在执行中" });
                if (string.IsNullOrWhiteSpace(req.LocalFilePath) || !File.Exists(req.LocalFilePath))
                {
                    return Results.BadRequest(new { error = $"本地文件不存在: {req.LocalFilePath}" });
                }

                var allDevs = r.GetDevices();
                var targets = ResolveTargetDevices(allDevs, req.TargetDevices);
                if (targets.Count == 0) return Results.BadRequest(new { error = "未勾选任何有效设备" });

                _ = Task.Run(async () =>
                {
                    await eng.RunBatchFilePushAsync(targets, req.LocalFilePath, req.RemoteDirPath, req.Concurrency);
                });

                return Results.Ok(new { success = true, targetCount = targets.Count });
            });

            // 查询实时任务进度
            app.MapGet("/api/deploy/progress", (DeploymentEngine eng) =>
            {
                return Results.Ok(new
                {
                    isBusy = eng.IsBusy,
                    progress = eng.CurrentProgress.Values
                });
            });

            // 取消当前任务
            app.MapPost("/api/deploy/cancel", (DeploymentEngine eng) =>
            {
                eng.CancelCurrentJob();
                return Results.Ok(new { success = true });
            });

            // 设备运维动作
            app.MapPost("/api/devices/{id}/action", async (string id, string action, string? package, DeviceRepository r, AdbManager a) =>
            {
                var dev = r.GetDevice(id);
                if (dev == null) return Results.BadRequest(new { error = "未找到设备" });

                switch (action.ToLowerInvariant())
                {
                    case "reboot":
                        await a.RebootAsync(dev.AdbTarget);
                        return Results.Ok(new { success = true, message = "已发送重启指令" });
                    case "start-app":
                        if (string.IsNullOrEmpty(package)) return Results.BadRequest(new { error = "包名不能为空" });
                        await a.StartAppAsync(dev.AdbTarget, package);
                        return Results.Ok(new { success = true, message = "已发送启动应用指令" });
                    case "stop-app":
                        if (string.IsNullOrEmpty(package)) return Results.BadRequest(new { error = "包名不能为空" });
                        await a.StopAppAsync(dev.AdbTarget, package);
                        return Results.Ok(new { success = true, message = "已发送停止应用指令" });
                    default:
                        return Results.BadRequest(new { error = "不支持的操作" });
                }
            });

            PrintBanner(webPort, adb.AdbPath);

            // 自动打开浏览器
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"http://localhost:{webPort}",
                        UseShellExecute = true
                    });
                }
                catch { }
            });

            await app.RunAsync();
        }

        private static List<DeviceInfo> ResolveTargetDevices(List<DeviceInfo> all, List<string> requested)
        {
            if (requested == null || requested.Count == 0 || requested.Contains("all", StringComparer.OrdinalIgnoreCase))
            {
                return all;
            }

            var set = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
            return all.Where(d => 
                set.Contains(d.DeviceNo) || 
                set.Contains(d.Alias) || 
                set.Contains(d.Ip) || 
                set.Contains(d.AdbTarget) || 
                set.Contains(d.Sn)).ToList();
        }

        private static void PrintBanner(int port, string adbPath)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════╗
║        PICO 设备无线 ADB 批量部署工具 (PicoDeployer v1.0)           ║
║       支持多机并发安装 · 差异化配置在线预览替换 · 大文件极速推流     ║
╚══════════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();

            Console.WriteLine($"• Web 控制台服务: http://localhost:{port} (局域网内任意平板/电脑均可访问)");
            Console.WriteLine($"• ADB 引擎路径  : {adbPath}");
            Console.WriteLine("• 状态           : 运行中 (按 Ctrl+C 可停止服务)");
            Console.WriteLine("──────────────────────────────────────────────────────────────────────");
        }

        private static async Task RunCliModeAsync(string[] args)
        {
            Console.WriteLine("=== PicoDeployer CLI 命令行执行模式 ===");
            var cmd = args[0].ToLowerInvariant();
            var repo = new DeviceRepository();
            var adb = new AdbManager(repo.Settings.AdbPath);
            var eng = new DeploymentEngine(adb);

            eng.OnProgressUpdated += p =>
            {
                Console.WriteLine($"[{p.DeviceNo}] ({p.Percent}%) {p.Message}");
            };

            if (cmd == "devices" || cmd == "list")
            {
                Console.WriteLine("正在刷新设备连接状态...");
                var devs = repo.GetDevices();
                foreach (var d in devs)
                {
                    await adb.RefreshDeviceDetailsAsync(d);
                    var status = d.IsConnected ? "[在线]" : "[离线]";
                    Console.WriteLine($"• {d.DisplayName} | IP: {d.Ip}:{d.Port} | 状态: {status} | 电量: {d.Battery}% | 型号: {d.Model}");
                }
            }
            else if (cmd == "install")
            {
                var apkPath = args.Length > 1 ? args[1] : "";
                if (string.IsNullOrEmpty(apkPath) || !File.Exists(apkPath))
                {
                    Console.WriteLine("用法: PicoDeployer.exe install <ApkFilePath> [DeviceNo1,DeviceNo2,...]");
                    return;
                }
                var devs = repo.GetDevices();
                if (args.Length > 2)
                {
                    var targets = args[2].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    devs = ResolveTargetDevices(devs, targets.ToList());
                }
                Console.WriteLine($"准备向 {devs.Count} 台设备并发安装: {apkPath}");
                await eng.RunBatchInstallAsync(devs, apkPath, 3);
            }
            else
            {
                Console.WriteLine("未知参数。输入没有参数启动 Web 控制台，或输入 'devices' / 'install <apk>' 执行命令。");
            }
        }
    }
}

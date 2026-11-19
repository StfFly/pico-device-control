using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using PicoManager.Server.Hubs;
using PicoManager.Server.Models;
using PicoManager.Server.Native;

namespace PicoManager.Server.Services
{
    public class PicoDeviceManager
    {
        private readonly ConcurrentDictionary<string, PicoDevice> _devices = new();
        private readonly IHubContext<PicoHub> _hubContext;
        private readonly ConfigService _configService;
        private readonly ScreenMonitorService _screenMonitorService;
        private readonly AdbService _adbService;
        private bool _sdkActive = false;

        public PicoDeviceManager(
            IHubContext<PicoHub> hubContext,
            ConfigService configService,
            ScreenMonitorService screenMonitorService,
            AdbService adbService)
        {
            _hubContext = hubContext;
            _configService = configService;
            _screenMonitorService = screenMonitorService;
            _adbService = adbService;
        }

        public void SetSdkActive(bool active)
        {
            _sdkActive = active;
        }

        public bool IsSdkActive => _sdkActive;

        public IEnumerable<PicoDevice> GetAllDevices()
        {
            return _devices.Values.OrderBy(d => d.Alias).ThenBy(d => d.SN);
        }

        public PicoDevice? GetDevice(string sn)
        {
            _devices.TryGetValue(sn, out var dev);
            return dev;
        }

        public List<PicoDevice> ResolveDevices(string? deviceNo)
        {
            if (string.IsNullOrWhiteSpace(deviceNo) || deviceNo.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var onlineDevs = _devices.Values.Where(d => d.Online).ToList();
                return onlineDevs.Count > 0 ? onlineDevs : _devices.Values.ToList();
            }

            var parts = deviceNo.Split(new[] { ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                var result = new List<PicoDevice>();
                foreach (var part in parts)
                {
                    var found = ResolveSingleDevice(part);
                    if (found != null && !result.Any(d => d.SN == found.SN))
                    {
                        result.Add(found);
                    }
                }
                return result;
            }

            var single = ResolveSingleDevice(deviceNo.Trim());
            return single != null ? new List<PicoDevice> { single } : new List<PicoDevice>();
        }

        public PicoDevice? ResolveSingleDevice(string cleanNo)
        {
            if (string.IsNullOrWhiteSpace(cleanNo)) return null;
            cleanNo = cleanNo.Trim();

            // 1. 直接匹配 SN
            if (_devices.TryGetValue(cleanNo, out var devBySn))
            {
                return devBySn;
            }

            // 2. 匹配 Alias 或 SuiteNo (例如 "01号机", "022", "01")
            var matchByAlias = _devices.Values.FirstOrDefault(d =>
                string.Equals(d.Alias, cleanNo, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.SuiteNo, cleanNo, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DisplayName, cleanNo, StringComparison.OrdinalIgnoreCase));
            if (matchByAlias != null)
            {
                return matchByAlias;
            }

            // 3. 模糊匹配机号数字 (例如传 "22" 匹配 "022" 或 "022号机", 传 "1" 匹配 "01号机")
            string digitsInInput = System.Text.RegularExpressions.Regex.Replace(cleanNo, @"\D", "");
            if (!string.IsNullOrEmpty(digitsInInput))
            {
                var matchByDigits = _devices.Values.FirstOrDefault(d =>
                {
                    string devDigits = System.Text.RegularExpressions.Regex.Replace(d.Alias, @"\D", "");
                    if (string.IsNullOrEmpty(devDigits)) devDigits = System.Text.RegularExpressions.Regex.Replace(d.SuiteNo, @"\D", "");
                    if (string.IsNullOrEmpty(devDigits)) devDigits = System.Text.RegularExpressions.Regex.Replace(d.SN, @"\D", "");
                    return devDigits == digitsInInput ||
                           (int.TryParse(devDigits, out int dNum) && int.TryParse(digitsInInput, out int iNum) && dNum == iNum);
                });

                if (matchByDigits != null)
                {
                    return matchByDigits;
                }
            }

            // 4. 从 config.json 的 HeadsetAliases 中查找映射
            foreach (var kvp in _configService.CurrentConfig.HeadsetAliases)
            {
                if (string.Equals(kvp.Value, cleanNo, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kvp.Key, cleanNo, StringComparison.OrdinalIgnoreCase))
                {
                    if (_devices.TryGetValue(kvp.Key, out var devFromConfig))
                    {
                        return devFromConfig;
                    }
                }
            }

            return null;
        }

        public string ResolvePackageName(string? deviceNo, string? inputPackage)
        {
            // 1. 如果显式指定了包名或项目名
            if (!string.IsNullOrWhiteSpace(inputPackage))
            {
                string cleanPkg = inputPackage.Trim();
                var projectByName = _configService.CurrentConfig.Projects.FirstOrDefault(p =>
                    p.Name.Equals(cleanPkg, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.Equals(cleanPkg, StringComparison.OrdinalIgnoreCase) ||
                    p.PackageName.Equals(cleanPkg, StringComparison.OrdinalIgnoreCase));

                if (projectByName != null)
                {
                    return projectByName.PackageName;
                }

                return cleanPkg;
            }

            // 2. 如果包名为空，尝试从机器号/SN 的默认绑定配置中查找
            if (!string.IsNullOrWhiteSpace(deviceNo))
            {
                string cleanNo = deviceNo.Trim();
                var dev = ResolveDevices(cleanNo).FirstOrDefault();

                if (dev != null)
                {
                    var mappedPkg = _configService.GetDeviceDefaultProject(dev.SN);
                    if (!string.IsNullOrWhiteSpace(mappedPkg)) return ResolvePackageName("", mappedPkg);

                    if (!string.IsNullOrWhiteSpace(dev.Alias))
                    {
                        mappedPkg = _configService.GetDeviceDefaultProject(dev.Alias);
                        if (!string.IsNullOrWhiteSpace(mappedPkg)) return ResolvePackageName("", mappedPkg);

                        string devDigits = System.Text.RegularExpressions.Regex.Replace(dev.Alias, @"\D", "");
                        if (!string.IsNullOrEmpty(devDigits))
                        {
                            mappedPkg = _configService.GetDeviceDefaultProject(devDigits);
                            if (!string.IsNullOrWhiteSpace(mappedPkg)) return ResolvePackageName("", mappedPkg);
                        }
                    }
                }

                var directMapped = _configService.GetDeviceDefaultProject(cleanNo);
                if (!string.IsNullOrWhiteSpace(directMapped)) return ResolvePackageName("", directMapped);

                string cleanDigits = System.Text.RegularExpressions.Regex.Replace(cleanNo, @"\D", "");
                if (!string.IsNullOrEmpty(cleanDigits))
                {
                    directMapped = _configService.GetDeviceDefaultProject(cleanDigits);
                    if (!string.IsNullOrWhiteSpace(directMapped)) return ResolvePackageName("", directMapped);
                }
            }

            // 3. 回退策略：取系统配置中的第一个体验项目包名
            var defaultProj = _configService.CurrentConfig.Projects.FirstOrDefault();
            return defaultProj?.PackageName ?? "com.pico.vrplayer";
        }

        public void InitializeConfiguredDevices()
        {
            foreach (var kvp in _configService.CurrentConfig.HeadsetAliases)
            {
                string sn = kvp.Key.Trim();
                string alias = kvp.Value.Trim();
                if (string.IsNullOrWhiteSpace(sn)) continue;

                _devices.GetOrAdd(sn, s => new PicoDevice
                {
                    SN = s,
                    Alias = alias,
                    Online = false, // 预置真机，默认离线，等待 SDK 探针激活
                    Battery = 0,
                    CurrentApp = "系统桌面",
                    AppTitle = "系统桌面",
                    IsDemo = false,
                    LastSeen = DateTime.MinValue
                });
            }
        }

        public PicoDevice RegisterConfiguredDevice(string sn, string alias, string? model = null)
        {
            var isNew = false;
            var dev = _devices.GetOrAdd(sn, s =>
            {
                isNew = true;
                return new PicoDevice
                {
                    SN = s,
                    Alias = alias,
                    Model = string.IsNullOrWhiteSpace(model) ? "PICO 4 Enterprise" : model,
                    Online = false,
                    Battery = 0,
                    CurrentApp = "系统桌面",
                    AppTitle = "系统桌面",
                    IsDemo = false,
                    LastSeen = DateTime.MinValue
                };
            });

            dev.Alias = alias;
            if (!string.IsNullOrWhiteSpace(model)) dev.Model = model;
            dev.IsDemo = false;

            if (isNew)
            {
                _hubContext.Clients.All.SendAsync("DeviceAdded", dev);
            }
            else
            {
                _hubContext.Clients.All.SendAsync("DeviceUpdated", dev);
            }

            return dev;
        }

        public PicoDevice AddOrUpdateDevice(string sn, Action<PicoDevice>? updateAction = null)
        {
            var isNew = false;
            var dev = _devices.GetOrAdd(sn, s =>
            {
                isNew = true;
                var configuredAlias = _configService.GetAlias(s);
                return new PicoDevice
                {
                    SN = s,
                    Alias = configuredAlias == s ? "" : configuredAlias,
                    Online = false, // 严格默认为离线，杜绝未开机被假激活
                    IsDemo = false,
                    LastSeen = DateTime.MinValue
                };
            });

            dev.IsDemo = false;

            updateAction?.Invoke(dev);

            if (isNew)
            {
                _hubContext.Clients.All.SendAsync("DeviceAdded", dev);
            }
            else
            {
                _hubContext.Clients.All.SendAsync("DeviceUpdated", dev);
            }

            return dev;
        }

        public void SetDeviceOnline(string sn)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.Online = true;
                d.LastSeen = DateTime.UtcNow;
            });
        }

        public void MarkDeviceOffline(string sn)
        {
            if (_devices.TryGetValue(sn, out var dev))
            {
                dev.Online = false;
                dev.IsStreaming = false;
                dev.LastSeen = DateTime.UtcNow;
                _hubContext.Clients.All.SendAsync("DeviceUpdated", dev);
            }
        }

        public void UpdateBattery(string sn, uint battery)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.Battery = battery;
                if (battery > 0 && d.Online)
                {
                    d.LastSeen = DateTime.UtcNow;
                }
            });
        }

        public void UpdateControllerBattery(string sn, int controllerId, uint battery)
        {
            AddOrUpdateDevice(sn, d =>
            {
                if (controllerId == 0)
                    d.ControllerLeftBattery = battery;
                else
                    d.ControllerRightBattery = battery;
            });
        }

        public void UpdateCurrentApp(string sn, string appName)
        {
            AddOrUpdateDevice(sn, d =>
            {
                if (IsTrackingLostApp(appName))
                {
                    d.TrackingLost = true;
                    d.CurrentApp = appName;
                    d.AppTitle = "大空间定位丢失 (透视找回中)";
                }
                else if (IsDesktopApp(appName))
                {
                    d.TrackingLost = false;
                    d.CurrentApp = "系统桌面";
                    d.AppTitle = "系统桌面";
                }
                else
                {
                    d.TrackingLost = false;
                    d.CurrentApp = appName;
                    d.AppTitle = GetAppTitle(appName);
                }
            });
        }

        public static bool IsTrackingLostApp(string? packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return false;
            string clean = packageName.Trim();
            return clean.Equals("com.pvr.seethrough.setting", StringComparison.OrdinalIgnoreCase) ||
                   clean.Contains("seethrough.setting", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDesktopApp(string? packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return true;
            string clean = packageName.Trim();
            return clean == "系统桌面" ||
                   clean == " " ||
                   clean.Equals("com.picovr.enterpriseassistant", StringComparison.OrdinalIgnoreCase) ||
                   clean.Equals("com.pico.launcher", StringComparison.OrdinalIgnoreCase);
        }

        public void UpdateLockState(string sn, bool locked)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.Locked = locked;
            });
        }

        public void UpdateSensorWear(string sn, bool isWorn)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.IsWorn = isWorn;
            });
        }

        public void UpdateScreenState(string sn, bool screenOn)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.ScreenOn = screenOn;
            });
        }

        public void UpdateSuiteNo(string sn, string suiteNo)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.SuiteNo = suiteNo?.Trim() ?? string.Empty;
            });
        }

        public void UpdateAlias(string sn, string alias)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.Alias = alias;
            });
            _configService.SetAlias(sn, alias);
        }

        public void UpdateModel(string sn, string model)
        {
            AddOrUpdateDevice(sn, d =>
            {
                d.Model = model;
            });
        }

        // ======================== 控制接口 ========================

        public int LockDevice(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.LockDevice(sn);
            }
            UpdateLockState(sn, true);
            return ret;
        }

        public int UnlockDevice(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.UnlockDevice(sn);
            }
            UpdateLockState(sn, false);
            return ret;
        }

        public int StartApp(string sn, string packageName, string extraJson = "")
        {
            // SDK 规范：调用播控前需锁定设备
            LockDevice(sn);

            int ret = 0;
            if (_sdkActive)
            {
                if (string.IsNullOrWhiteSpace(extraJson))
                {
                    ret = PXREAOpenApi.StartPlayApp(sn, packageName);
                }
                else
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        packageName = packageName,
                        extra = extraJson
                    });
                    ret = PXREAOpenApi.StartPlayApp(sn, payload);
                }
            }

            UpdateCurrentApp(sn, packageName);
            return ret;
        }

        public int StopApp(string sn, string packageName = "")
        {
            if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                return -1; // 离线设备不执行指令，防止伪在线

            LockDevice(sn);

            // 1. 精确解析目标游戏包名
            string targetPkg = packageName;
            if (string.IsNullOrWhiteSpace(targetPkg) || targetPkg == "系统桌面")
            {
                if (dev != null && !string.IsNullOrWhiteSpace(dev.CurrentApp) && !IsDesktopApp(dev.CurrentApp))
                {
                    targetPkg = dev.CurrentApp;
                }
                else
                {
                    targetPkg = ResolvePackageName(sn, null);
                }
            }

            int ret = 0;

            // 2. 通过 PICO SDK 退出应用 (如果获知具体包名则传入具体包名，避免传空触发 PICO 系统“是否返回桌面”弹窗)
            if (_sdkActive)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(targetPkg) && targetPkg != "系统桌面")
                    {
                        ret = PXREAOpenApi.StopPlayApp(sn, targetPkg);
                    }
                    else
                    {
                        ret = PXREAOpenApi.StopPlayApp(sn, " ");
                    }
                }
                catch { }
            }

            // 3. 核心修复：通过 ADB 下发 am force-stop 彻底强杀游戏进程，实现无弹窗秒级直接关闭游戏
            _ = Task.Run(async () =>
            {
                try
                {
                    var pkgsToKill = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(targetPkg) && targetPkg != "系统桌面")
                    {
                        pkgsToKill.Add(targetPkg);
                    }
                    if (dev != null && !string.IsNullOrWhiteSpace(dev.CurrentApp) && !IsDesktopApp(dev.CurrentApp))
                    {
                        pkgsToKill.Add(dev.CurrentApp);
                    }

                    // 将配置中已登记的所有体验项目包名同时纳入强杀列表，确保无遗漏
                    foreach (var proj in _configService.CurrentConfig.Projects)
                    {
                        if (!string.IsNullOrWhiteSpace(proj.PackageName) && proj.Type != "vr_video")
                        {
                            pkgsToKill.Add(proj.PackageName);
                        }
                    }

                    await _adbService.ForceStopAllGamesAsync(sn, pkgsToKill);
                }
                catch { }
            });

            UpdateCurrentApp(sn, "系统桌面");
            return ret;
        }

        public int PauseVideo(string sn)
        {
            if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                return -1; // 离线设备跳过

            LockDevice(sn);
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.PauseVideo(sn);
            }
            return ret;
        }

        public int ContinueVideo(string sn)
        {
            if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                return -1; // 离线设备跳过

            LockDevice(sn);
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.ContinueVideo(sn);
            }
            return ret;
        }

        public int SetVolume(string sn, int volume)
        {
            LockDevice(sn);
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.SetDeviceVolumn(sn, Math.Clamp(volume, 0, 100));
            }
            return ret;
        }

        public int Recenter(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.RecenterView(sn);
            }
            return ret;
        }

        public int Reboot(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.RestartDevice(sn);
            }
            MarkDeviceOffline(sn);
            return ret;
        }

        public int Shutdown(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.ShutDownDevice(sn);
            }
            MarkDeviceOffline(sn);
            return ret;
        }

        public int SetDeviceAlias(string sn, string alias)
        {
            // 独立业务别名：只更新系统内部配置与模型，不再调用 PXREAOpenApi.SetDeviceAlias，避免覆盖冲掉 PICO 企业套件编号
            UpdateAlias(sn, alias);
            return 0;
        }

        public void RegisterHeadset(string sn, string alias, string model = "PICO 4 Enterprise")
        {
            _configService.SetAlias(sn, alias);
            RegisterConfiguredDevice(sn, alias, model);
        }

        public async Task<int> SetScreenStateAsync(string sn, bool screenOn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                try
                {
                    // 绝不在点亮屏幕时调用 LockDevice！直接下发官方屏幕控制
                    ret = PXREAOpenApi.SetScreenState(sn, screenOn ? 1 : 0);
                }
                catch { }
            }

            // ADB 底层双保障唤醒/休眠
            try
            {
                await _adbService.SetScreenStateAsync(sn, screenOn);
            }
            catch { }

            UpdateScreenState(sn, screenOn);
            return ret;
        }

        public async Task<int> BatchSetScreenStateAsync(List<string> snList, bool screenOn)
        {
            if (snList == null || snList.Count == 0) return 0;
            // 并发下发，秒级响应所有设备
            var tasks = snList.Select(sn => SetScreenStateAsync(sn, screenOn));
            await Task.WhenAll(tasks);
            return snList.Count;
        }

        public void UnbindHeadset(string sn)
        {
            _configService.CurrentConfig.HeadsetAliases.Remove(sn);
            _configService.CurrentConfig.DeviceDefaultProjects.Remove(sn);
            _configService.SaveConfig();

            // 从当前设备列表中彻底移除，并广播 DeviceRemoved 让前端即时删除卡片与表格行
            if (_devices.TryRemove(sn, out _))
            {
                _hubContext.Clients.All.SendAsync("DeviceRemoved", sn);
            }
        }

        public int StartMonitor(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                // 官方 PICO Business Suite 最新标准接口: StartMonitorScreen (对应底层 startMonitorScreenSDK)
                ret = PXREAOpenApi.StartMonitorScreen(sn, PXREAMonitorQuality.Quality480P);
                Console.WriteLine($"[PICO SDK] 启动设备现代屏幕监控流 StartMonitorScreen(480P) SN: {sn}, ret={ret}");
                if (ret != 0)
                {
                    // 兼容旧版企业套件 StartMonitor
                    int retLegacy = PXREAOpenApi.StartMonitor(sn);
                    Console.WriteLine($"[PICO SDK] 备用调用 StartMonitor SN: {sn}, ret={retLegacy}");
                    ret = retLegacy;
                }

                if (ret == 0)
                {
                    _screenMonitorService.RecordStartRequested(sn, 0, "监看指令已下发，等待头显 ByteRTC 推流响应...", StreamPhase.WaitingFirstFrame);
                }
                else
                {
                    _screenMonitorService.RecordStartRequested(sn, ret, $"StartMonitorScreen 返回错误码 {ret}，官方套件未就绪或设备未响应", StreamPhase.Error);
                }
            }
            else
            {
                ret = -1;
                Console.WriteLine($"[PICO SDK] 启动监控流失败: PICO SDK 未激活 (官方套件 127.0.0.1:60061 未连接)");
                _screenMonitorService.RecordStartRequested(sn, -1, "官方企业应用套件未连接 (60061 端口不可达，请启动并登录套件)", StreamPhase.SuiteOffline);
            }

            AddOrUpdateDevice(sn, d => d.IsStreaming = true);
            return ret;
        }

        public int StopMonitor(string sn)
        {
            int ret = 0;
            if (_sdkActive)
            {
                ret = PXREAOpenApi.StopMonitorScreen(sn);
                try { PXREAOpenApi.StopMonitor(sn); } catch { }
                Console.WriteLine($"[PICO SDK] 停止设备监控流 SN: {sn}, ret={ret}");
            }
            _screenMonitorService.RecordStopRequested(sn);
            AddOrUpdateDevice(sn, d => d.IsStreaming = false);
            return ret;
        }

        // ======================== 批量操作 ========================

        public async Task<int> BatchStartAppAsync(List<string> snList, string packageName, string extraJson = "")
        {
            int count = 0;
            foreach (var sn in snList)
            {
                if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                    continue;

                StartApp(sn, packageName, extraJson);
                count++;
                await Task.Delay(50); // 错峰分发，避免短时间瞬间拥塞
            }
            return count;
        }

        public async Task<int> BatchStopAppAsync(List<string> snList)
        {
            int count = 0;
            foreach (var sn in snList)
            {
                if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                    continue;

                StopApp(sn);
                count++;
                await Task.Delay(50);
            }
            return count;
        }

        public async Task<int> BatchPauseVideoAsync(List<string> snList)
        {
            int count = 0;
            foreach (var sn in snList)
            {
                if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                    continue;

                PauseVideo(sn);
                count++;
                await Task.Delay(30);
            }
            return count;
        }

        public async Task<int> BatchContinueVideoAsync(List<string> snList)
        {
            int count = 0;
            foreach (var sn in snList)
            {
                if (_devices.TryGetValue(sn, out var dev) && !dev.Online)
                    continue;
                ContinueVideo(sn);
                count++;
                await Task.Delay(30);
            }
            return count;
        }

        public async Task<int> BatchSetVolumeAsync(List<string> snList, int volume)
        {
            int count = 0;
            foreach (var sn in snList)
            {
                SetVolume(sn, volume);
                count++;
                await Task.Delay(30);
            }
            return count;
        }

        public async Task<int> BatchRebootAsync(List<string> snList)
        {
            int count = 0;
            foreach (var sn in snList)
            {
                Reboot(sn);
                count++;
                await Task.Delay(100);
            }
            return count;
        }

        public async Task<int> BatchShutdownAsync(List<string> snList)
        {
            int count = 0;
            foreach (var sn in snList)
            {
                Shutdown(sn);
                count++;
                await Task.Delay(100);
            }
            return count;
        }

        public void SeedDemoDevices(int count = 12)
        {
            // 用户要求：应用端不使用模拟测试机器，纯净呈现真机
        }

        public void ClearDemoDevices()
        {
            var demoSns = _devices.Where(x => x.Value.IsDemo || x.Key.StartsWith("P4E-88")).Select(x => x.Key).ToList();
            foreach (var sn in demoSns)
            {
                _devices.TryRemove(sn, out _);
                _hubContext.Clients.All.SendAsync("DeviceRemoved", sn);
            }
        }

        private string GetAppTitle(string packageName)
        {
            if (IsDesktopApp(packageName))
                return "系统桌面";

            var project = _configService.CurrentConfig.Projects
                .FirstOrDefault(p => p.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));

            return project != null ? project.Name : packageName;
        }
    }
}

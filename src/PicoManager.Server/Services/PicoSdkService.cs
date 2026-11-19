using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PicoManager.Server.Models;
using PicoManager.Server.Native;

namespace PicoManager.Server.Services
{
    public class PicoSdkService : IHostedService, IDisposable
    {
        private readonly ILogger<PicoSdkService> _logger;
        private readonly PicoDeviceManager _deviceManager;
        private readonly ConfigService _configService;
        private readonly ScreenMonitorService _screenMonitorService;

        // 维持静态/强引用回调委托，防止被 .NET GC 回收崩溃
        private readonly PXREAOpenApi.SDKCallBack _callbackDelegate;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _telemetryTask;
        private Task? _keepAliveTask;
        private volatile bool _isSdkInitialized = false;
        private volatile bool _isServerConnected = false;

        public PicoSdkService(
            ILogger<PicoSdkService> logger,
            PicoDeviceManager deviceManager,
            ConfigService configService,
            ScreenMonitorService screenMonitorService)
        {
            _logger = logger;
            _deviceManager = deviceManager;
            _configService = configService;
            _screenMonitorService = screenMonitorService;

            _callbackDelegate = new PXREAOpenApi.SDKCallBack(OnSdkCallback);
        }

        public async Task<SuiteHealthStatus> CheckSuiteHealthAsync()
        {
            var status = new SuiteHealthStatus();
            try
            {
                status.IsSdkActive = _isSdkInitialized;
                status.IsServerConnected = _isServerConnected;

                // 如果 SDK 已经成功连接且套件服务正在通信，则直接判定为完全健康，无需再做易超时的底层 TCP 探针
                if (_isSdkInitialized && _isServerConnected)
                {
                    status.IsProcessRunning = true;
                    status.IsPortOpen = true;
                    status.StatusSummary = "官方企业套件 gRPC 通道握手正常，具备完整视频流传输能力";
                    status.Advice = "通信链路就绪，可随时开启头显屏幕画面实时监控";
                }
                else
                {
                    // 1. 检查 PICOBusinessSuite 进程
                    var procs = Process.GetProcessesByName("PICOBusinessSuite");
                    status.IsProcessRunning = procs.Length > 0;

                    // 2. 检查 127.0.0.1:60061 端口连通性 (带超时保护 1000ms)
                    try
                    {
                        using var tcp = new System.Net.Sockets.TcpClient();
                        var connectTask = tcp.ConnectAsync("127.0.0.1", 60061);
                        var delayTask = Task.Delay(1000);
                        if (await Task.WhenAny(connectTask, delayTask) == connectTask && tcp.Connected)
                        {
                            status.IsPortOpen = true;
                        }
                    }
                    catch { }

                    if (!status.IsProcessRunning)
                    {
                        status.StatusSummary = "PICO 官方企业应用套件未运行";
                        status.Advice = "请在电脑上启动并登录 PICO Business Suite 官方软件";
                    }
                    else if (!status.IsPortOpen)
                    {
                        status.StatusSummary = "套件已运行但 60061 服务端口未开启 (需登录账号并进入主界面)";
                        status.Advice = "请在 PICO Business Suite 窗口登录账号，登录后套件才会开放 gRPC 端口";
                    }
                    else
                    {
                        status.StatusSummary = "官方套件 60061 服务端口正常，但 SDK 尚未建立连接";
                        status.Advice = "系统正在自动握手重连中，或点击下方「一键重连」立刻激活";
                    }
                }

                foreach (var kvp in _screenMonitorService.GetAllStreamStatuses())
                {
                    status.Streams[kvp.Key] = kvp.Value;
                }
            }
            catch (Exception ex)
            {
                status.StatusSummary = $"诊断检测异常: {ex.Message}";
            }
            return status;
        }

        public async Task<bool> ReconnectSdkAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                _logger.LogInformation("[PICO SDK] 正在尝试连接官方企业套件 (127.0.0.1:60061)...");
                // 仅当此前已经成功 Init 过时才允许 Deinit，严禁在未初始化时盲目调用 Deinit 触发底层 C++ 空指针奔溃 (0xC0000005)
                if (_isSdkInitialized)
                {
                    try
                    {
                        PXREAOpenApi.Deinit();
                    }
                    catch { }
                    _isSdkInitialized = false;
                }

                int ret = PXREAOpenApi.Init(IntPtr.Zero, _callbackDelegate, (uint)PXREAClientCallbackType.PXREAFullMask);
                if (ret == 0)
                {
                    _isSdkInitialized = true;
                    _isServerConnected = true;
                    _deviceManager.SetSdkActive(true);
                    _logger.LogInformation("★ PICO Enterprise Suite SDK 成功连接并激活 (ret=0)！");
                    return true;
                }
                else
                {
                    _isSdkInitialized = false;
                    _deviceManager.SetSdkActive(false);
                    _logger.LogWarning("PICO Enterprise Suite SDK 连接未完成，返回码: {Ret} (请确认官方套件已登录)", ret);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行 ReconnectSdkAsync 发生异常");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = new CancellationTokenSource();

            // 1. 预置配置中心中绑定的所有真机设备（初始为离线状态）
            _deviceManager.InitializeConfiguredDevices();

            _logger.LogInformation("正在初始化 PICO Enterprise Suite SDK...");
            _ = ReconnectSdkAsync();

            // 2. 启动硬件状态遥测轮询与官方套件服务自动保活巡检
            _telemetryTask = Task.Run(() => QueryTelemetryLoop(_cts.Token));
            _keepAliveTask = Task.Run(() => SuiteKeepAliveLoop(_cts.Token));

            return Task.CompletedTask;
        }

        private async Task SuiteKeepAliveLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, token);

                    // 1. 如果 SDK 尚未激活，巡检查找官方套件并自动连接
                    if (!_isSdkInitialized)
                    {
                        var health = await CheckSuiteHealthAsync();
                        if (health.IsPortOpen || health.IsProcessRunning)
                        {
                            _logger.LogInformation("[PICO SDK 保活] 检测到官方套件就绪，正在自动连接 SDK...");
                            await ReconnectSdkAsync();
                        }
                    }
                    else
                    {
                        // 2. 如果已激活，由官方 SDK 的 PXREAServerDisconnect 原生回调主管断开
                        // 此处仅做辅助进程监测，当且仅当官方套件进程被直接杀死且 SDK 回调未收到时才清理
                        var procs = Process.GetProcessesByName("PICOBusinessSuite");
                        if (procs.Length == 0 && !_isServerConnected)
                        {
                            _logger.LogWarning("[PICO SDK 保活] 官方套件进程已退出，切换 SDK 为待命重连模式...");
                            _isSdkInitialized = false;
                            _deviceManager.SetSdkActive(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("SuiteKeepAliveLoop 巡检异常: {Message}", ex.Message);
                }
            }
        }

        private void OnSdkCallback(IntPtr context, PXREAClientCallbackType type, int status, IntPtr userData)
        {
            try
            {
                switch (type)
                {
                    case PXREAClientCallbackType.PXREAServerConnect:
                        _logger.LogInformation("[PICO SDK] 官方套件 gRPC 服务已连接 (PXREAServerConnect)");
                        _isServerConnected = true;
                        _isSdkInitialized = true;
                        _deviceManager.SetSdkActive(true);
                        break;

                    case PXREAClientCallbackType.PXREAServerDisconnect:
                        _logger.LogWarning("[PICO SDK] 官方套件 gRPC 服务已断开 (PXREAServerDisconnect)");
                        _isServerConnected = false;
                        _isSdkInitialized = false;
                        _deviceManager.SetSdkActive(false);
                        break;

                    case PXREAClientCallbackType.PXREADeviceFind:
                    case PXREAClientCallbackType.PXREADeviceConnect:
                    case PXREAClientCallbackType.PXREADeviceResume:
                        if (userData != IntPtr.Zero)
                        {
                            string sn = Marshal.PtrToStringAnsi(userData) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(sn))
                            {
                                _logger.LogInformation("[PICO SDK] 硬件设备上线/唤醒重连 (Event: {Type}) SN: {SN}", type, sn);

                                // 检测到真实硬件设备接入，自动清理模拟数据保证界面纯净
                                _deviceManager.ClearDemoDevices();

                                var dev = _deviceManager.AddOrUpdateDevice(sn, d =>
                                {
                                    d.Online = true;
                                    d.IsDemo = false;
                                });

                                if (_configService.CurrentConfig.AutoLockOnFind)
                                {
                                    _deviceManager.LockDevice(sn);
                                }

                                // 立即拉取设备基础状态
                                QueryDeviceInitialInfo(sn);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceMissing:
                        if (userData != IntPtr.Zero)
                        {
                            string sn = Marshal.PtrToStringAnsi(userData) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(sn))
                            {
                                _logger.LogInformation("[PICO SDK] 设备离线 SN: {SN}", sn);
                                _deviceManager.MarkDeviceOffline(sn);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceBattery:
                        if (userData != IntPtr.Zero)
                        {
                            var bat = Marshal.PtrToStructure<PXREADevBattery>(userData);
                            if (!string.IsNullOrWhiteSpace(bat.devID))
                            {
                                _deviceManager.UpdateBattery(bat.devID, bat.battery);
                                if (bat.battery > 0)
                                {
                                    _deviceManager.SetDeviceOnline(bat.devID);
                                }
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREAControllerBattery:
                        if (userData != IntPtr.Zero)
                        {
                            var cBat = Marshal.PtrToStructure<PXREACtrllerBattery>(userData);
                            if (!string.IsNullOrWhiteSpace(cBat.devID))
                            {
                                _deviceManager.UpdateControllerBattery(cBat.devID, cBat.controllerID, cBat.battery);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREACurrentApplication:
                        if (userData != IntPtr.Zero)
                        {
                            var app = Marshal.PtrToStructure<PXREACurrentApp>(userData);
                            if (!string.IsNullOrWhiteSpace(app.devID))
                            {
                                _deviceManager.UpdateCurrentApp(app.devID, app.appName);
                                if (!string.IsNullOrWhiteSpace(app.appName))
                                {
                                    _deviceManager.SetDeviceOnline(app.devID);
                                }
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceModel:
                        if (userData != IntPtr.Zero)
                        {
                            var model = Marshal.PtrToStructure<PXREADevModel>(userData);
                            if (!string.IsNullOrWhiteSpace(model.devID))
                            {
                                _deviceManager.UpdateModel(model.devID, model.devModel);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceAlias:
                        if (userData != IntPtr.Zero)
                        {
                            var alias = Marshal.PtrToStructure<PXREADevAlias>(userData);
                            if (!string.IsNullOrWhiteSpace(alias.devID))
                            {
                                _deviceManager.UpdateSuiteNo(alias.devID, alias.alias);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceLock:
                        if (userData != IntPtr.Zero)
                        {
                            string sn = Marshal.PtrToStringAnsi(userData) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(sn))
                            {
                                _deviceManager.UpdateLockState(sn, true);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceUnlock:
                        if (userData != IntPtr.Zero)
                        {
                            string sn = Marshal.PtrToStringAnsi(userData) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(sn))
                            {
                                _deviceManager.UpdateLockState(sn, false);
                            }
                        }
                        break;

                    case PXREAClientCallbackType.PXREAScreenState:
                        if (userData != IntPtr.Zero)
                        {
                            string stateStr = Marshal.PtrToStringAnsi(userData) ?? "1";
                            // "1": 亮屏, "0": 息屏
                            bool screenOn = stateStr == "1";
                            // Screen state 回调通常带设备 SN 或者 status，若只给状态字符串则记录日志
                            _logger.LogDebug("[PICO SDK] 屏幕状态: {State}", stateStr);
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceScreenMonitorParameter:
                    case PXREAClientCallbackType.PXREADeviceMonitorParameter:
                    case PXREAClientCallbackType.PXREADeviceStreamMonitorParameter:
                        if (userData != IntPtr.Zero)
                        {
                            var param = Marshal.PtrToStructure<PXREAMonitorPara>(userData);
                            Console.WriteLine($"[PICO SDK] ★ 监看流参数建立就绪 (Type={type}): DevID={param.devID}, Width={param.width}, Height={param.height}");
                            _logger.LogInformation("[PICO SDK] 监看参数建立成功: DevID={DevID}, Width={Width}, Height={Height}", param.devID, param.width, param.height);
                        }
                        break;

                    case PXREAClientCallbackType.PXREADeviceScreenMonitor:
                    case PXREAClientCallbackType.PXREADeviceMonitor:
                    case PXREAClientCallbackType.PXREADeviceStreamMonitor:
                        if (userData != IntPtr.Zero)
                        {
                            _screenMonitorService.HandleRawFramePointer(userData, type == PXREAClientCallbackType.PXREADeviceMonitor);
                        }
                        break;

                    default:
                        _logger.LogTrace("[PICO SDK] 接收未处理回调: {Type}, status: {Status}", type, status);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PICO SDK] 处理回调发生异常 (Type: {Type})", type);
            }
        }

        private void QueryDeviceInitialInfo(string sn)
        {
            try
            {
                PXREAOpenApi.GetBattery(sn);
                PXREAOpenApi.GetControllerBattery(sn, 0);
                PXREAOpenApi.GetControllerBattery(sn, 1);
                PXREAOpenApi.GetCurrentApp(sn);
                PXREAOpenApi.GetDeviceModel(sn);
                PXREAOpenApi.GetDeviceAlias(sn);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "查询初始设备信息出错 SN: {SN}", sn);
            }
        }

        private async Task QueryTelemetryLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, token);
                    var devices = _deviceManager.GetAllDevices();
                    foreach (var dev in devices)
                    {
                        if (!dev.IsDemo)
                        {
                            if (dev.Online)
                            {
                                PXREAOpenApi.GetBattery(dev.SN);
                                PXREAOpenApi.GetControllerBattery(dev.SN, 0);
                                PXREAOpenApi.GetControllerBattery(dev.SN, 1);
                                PXREAOpenApi.GetCurrentApp(dev.SN);

                                // 持续失联超时仲裁：超过 30 秒未收到任何真实硬件心跳或数据包，安全置为离线
                                if (dev.LastSeen != DateTime.MinValue && (DateTime.UtcNow - dev.LastSeen).TotalSeconds > 30)
                                {
                                    _logger.LogInformation("[PICO SDK] 设备硬件失联超时 (30s)，切换为离线 SN: {SN}", dev.SN);
                                    _deviceManager.MarkDeviceOffline(dev.SN);
                                }
                            }
                            else
                            {
                                // 对离线设备周期性下发探针以激活套件感知（绝不在此处直接置为在线，仅当真实回调收到硬件电量数据时才上线，彻底兼顾自动发现与防假在线！）
                                PXREAOpenApi.GetBattery(dev.SN);
                                PXREAOpenApi.GetCurrentApp(dev.SN);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("QueryTelemetryLoop 异常: {Message}", ex.Message);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();

            if (_isSdkInitialized)
            {
                try
                {
                    PXREAOpenApi.Deinit();
                    _logger.LogInformation("PICO SDK 已安全释放 (Deinit)。");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Deinit SDK 异常");
                }
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cts?.Dispose();
        }
    }
}

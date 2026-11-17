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
        private CancellationTokenSource? _cts;
        private Task? _telemetryTask;
        private bool _isSdkInitialized = false;

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

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = new CancellationTokenSource();

            // 1. 预置配置中心中绑定的所有真机设备（初始为离线状态）
            _deviceManager.InitializeConfiguredDevices();

            _logger.LogInformation("正在初始化 PICO Enterprise Suite SDK...");
            try
            {
                int ret = PXREAOpenApi.Init(IntPtr.Zero, _callbackDelegate, (uint)PXREAClientCallbackType.PXREAFullMask);
                if (ret == 0)
                {
                    _isSdkInitialized = true;
                    _deviceManager.SetSdkActive(true);
                    _logger.LogInformation("PICO Enterprise Suite SDK 初始化成功 (ret=0)！");

                    // 启动后立即对所有已登记绑定的真机下发状态探针握手
                    foreach (var dev in _deviceManager.GetAllDevices())
                    {
                        QueryDeviceInitialInfo(dev.SN);
                    }

                    // 启动真实硬件定期轮询任务
                    _telemetryTask = Task.Run(() => QueryTelemetryLoop(_cts.Token));
                }
                else
                {
                    _logger.LogWarning("PICO Enterprise Suite SDK 初始化返回码 {Ret} (请确保 PICO Business Suite 已在 PC 启动并处于服务模式)", ret);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 PICO SDK 发生异常。");
            }

            return Task.CompletedTask;
        }

        private void OnSdkCallback(IntPtr context, PXREAClientCallbackType type, int status, IntPtr userData)
        {
            try
            {
                switch (type)
                {
                    case PXREAClientCallbackType.PXREAServerConnect:
                        _logger.LogInformation("[PICO SDK] 服务已连接");
                        break;

                    case PXREAClientCallbackType.PXREAServerDisconnect:
                        _logger.LogWarning("[PICO SDK] 服务已断开");
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
                            // 无论此前是否在线，周期性通过官方接口下发主动探针
                            int retBat = PXREAOpenApi.GetBattery(dev.SN);
                            PXREAOpenApi.GetControllerBattery(dev.SN, 0);
                            PXREAOpenApi.GetControllerBattery(dev.SN, 1);
                            PXREAOpenApi.GetCurrentApp(dev.SN);

                            if (retBat == 0)
                            {
                                // 实际控制指令执行成功 (ret==0)，证明企业套件与头显链路畅通
                                if (!dev.Online)
                                {
                                    _logger.LogInformation("[PICO SDK] 真实控制探针探测到设备在线响应，自动激活上线 SN: {SN}", dev.SN);
                                    _deviceManager.AddOrUpdateDevice(dev.SN, d =>
                                    {
                                        d.Online = true;
                                        d.LastSeen = DateTime.UtcNow;
                                    });
                                }
                            }
                            else
                            {
                                // 探针失败：若此前在线，且已超过 45 秒未收到任何来自该设备的数据包与回调，判定为彻底离线
                                if (dev.Online && dev.LastSeen != DateTime.MinValue && (DateTime.UtcNow - dev.LastSeen).TotalSeconds > 45)
                                {
                                    _logger.LogInformation("[PICO SDK] 设备持续失联超时 (45s)，自动切换为离线 SN: {SN}", dev.SN);
                                    _deviceManager.MarkDeviceOffline(dev.SN);
                                }
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

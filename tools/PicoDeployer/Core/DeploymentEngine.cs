using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PicoDeployer.Models;

namespace PicoDeployer.Core
{
    public class DeploymentEngine
    {
        private readonly AdbManager _adb;
        private readonly ConcurrentDictionary<string, DeviceTaskProgress> _progressMap = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _currentCts;
        private bool _isBusy;

        public event Action<DeviceTaskProgress>? OnProgressUpdated;

        public DeploymentEngine(AdbManager adb)
        {
            _adb = adb;
        }

        public bool IsBusy => _isBusy;

        public IReadOnlyDictionary<string, DeviceTaskProgress> CurrentProgress => _progressMap;

        public void CancelCurrentJob()
        {
            _currentCts?.Cancel();
        }

        public void ResetProgress(IEnumerable<DeviceInfo> devices)
        {
            _progressMap.Clear();
            foreach (var dev in devices)
            {
                var key = !string.IsNullOrWhiteSpace(dev.DeviceNo) ? dev.DeviceNo : dev.AdbTarget;
                var p = new DeviceTaskProgress
                {
                    DeviceNo = dev.DeviceNo,
                    Target = dev.AdbTarget,
                    Stage = TaskStage.Pending,
                    Percent = 0,
                    Message = "等待队列调度..."
                };
                _progressMap[key] = p;
                OnProgressUpdated?.Invoke(p);
            }
        }

        public async Task RunBatchInstallAsync(
            List<DeviceInfo> targetDevices, 
            string apkPath, 
            int concurrency = 3, 
            bool autoLaunch = false, 
            string? packageName = null)
        {
            if (_isBusy) throw new InvalidOperationException("当前已有批量任务正在执行中，请等待完成或取消后再试。");

            _isBusy = true;
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            ResetProgress(targetDevices);
            concurrency = Math.Clamp(concurrency, 1, 10);
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);

            var tasks = new List<Task>();

            foreach (var dev in targetDevices)
            {
                var target = dev;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    var key = !string.IsNullOrWhiteSpace(target.DeviceNo) ? target.DeviceNo : target.AdbTarget;
                    var sw = Stopwatch.StartNew();

                    try
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Connecting, 10, "正在连接设备无线 ADB...");
                        ct.ThrowIfCancellationRequested();

                        var (success, msg) = await _adb.InstallApkAsync(target.AdbTarget, apkPath, (pct, statusText) =>
                        {
                            var stage = pct >= 80 ? TaskStage.Installing : TaskStage.Transferring;
                            UpdateDeviceProgress(key, target.AdbTarget, stage, pct, statusText);
                        }, ct);

                        sw.Stop();
                        var duration = Math.Round(sw.Elapsed.TotalSeconds, 1);

                        if (success)
                        {
                            if (autoLaunch && !string.IsNullOrWhiteSpace(packageName))
                            {
                                UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Verifying, 95, "安装成功，正在唤醒启动应用...");
                                await _adb.StartAppAsync(target.AdbTarget, packageName);
                            }

                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Success, 100, $"✅ 安装成功 (耗时 {duration}s)", duration);
                        }
                        else
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, $"❌ 安装失败: {msg}", duration, msg);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, "⚠️ 任务已被用户取消");
                    }
                    catch (Exception ex)
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, $"❌ 异常: {ex.Message}", error: ex.Message);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                _isBusy = false;
                _currentCts?.Dispose();
                _currentCts = null;
            }
        }

        public async Task RunBatchConfigPushAsync(
            List<DeviceInfo> targetDevices, 
            string templateContent, 
            string remoteTargetPath, 
            int concurrency = 4)
        {
            if (_isBusy) throw new InvalidOperationException("当前已有任务正在执行中。");

            _isBusy = true;
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            ResetProgress(targetDevices);
            concurrency = Math.Clamp(concurrency, 1, 10);
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);

            var tasks = new List<Task>();
            int devIndex = 1;

            foreach (var dev in targetDevices)
            {
                var target = dev;
                var currentIndex = devIndex++;

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    var key = !string.IsNullOrWhiteSpace(target.DeviceNo) ? target.DeviceNo : target.AdbTarget;
                    var sw = Stopwatch.StartNew();
                    string? tempFile = null;

                    try
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Connecting, 15, "正在为本机动态渲染差异化配置...");
                        ct.ThrowIfCancellationRequested();

                        // 渲染当前设备特有的临时配置文件
                        tempFile = ConfigTemplateEngine.CreateRenderedTempFile(templateContent, target, currentIndex);

                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Transferring, 35, "正在推入差异化配置文件...");

                        var (success, msg) = await _adb.PushFileAsync(target.AdbTarget, tempFile, remoteTargetPath, (pct, statusText) =>
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Transferring, pct, statusText);
                        }, ct);

                        sw.Stop();
                        var duration = Math.Round(sw.Elapsed.TotalSeconds, 1);

                        if (success)
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Success, 100, $"✅ 配置已替换同步 (耗时 {duration}s)", duration);
                        }
                        else
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, $"❌ 下发失败: {msg}", duration, msg);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, "⚠️ 任务已被用户取消");
                    }
                    catch (Exception ex)
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, $"❌ 下发异常: {ex.Message}", error: ex.Message);
                    }
                    finally
                    {
                        if (tempFile != null && File.Exists(tempFile))
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                        semaphore.Release();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                _isBusy = false;
                _currentCts?.Dispose();
                _currentCts = null;
            }
        }

        public async Task RunBatchFilePushAsync(
            List<DeviceInfo> targetDevices, 
            string localFilePath, 
            string remoteDirPath, 
            int concurrency = 3)
        {
            if (_isBusy) throw new InvalidOperationException("当前已有任务正在执行中。");

            _isBusy = true;
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            ResetProgress(targetDevices);
            concurrency = Math.Clamp(concurrency, 1, 10);
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);

            var fileName = Path.GetFileName(localFilePath);
            var remoteTargetPath = $"{remoteDirPath.TrimEnd('/')}/{fileName}";

            var tasks = new List<Task>();

            foreach (var dev in targetDevices)
            {
                var target = dev;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    var key = !string.IsNullOrWhiteSpace(target.DeviceNo) ? target.DeviceNo : target.AdbTarget;
                    var sw = Stopwatch.StartNew();

                    try
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Connecting, 10, "正在连接设备...");
                        ct.ThrowIfCancellationRequested();

                        var (success, msg) = await _adb.PushFileAsync(target.AdbTarget, localFilePath, remoteTargetPath, (pct, statusText) =>
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Transferring, pct, statusText);
                        }, ct);

                        sw.Stop();
                        var duration = Math.Round(sw.Elapsed.TotalSeconds, 1);

                        if (success)
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Success, 100, $"✅ 文件推送成功 (耗时 {duration}s)", duration);
                        }
                        else
                        {
                            UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, $"❌ 推送失败: {msg}", duration, msg);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, "⚠️ 任务已被用户取消");
                    }
                    catch (Exception ex)
                    {
                        UpdateDeviceProgress(key, target.AdbTarget, TaskStage.Failed, 100, $"❌ 异常: {ex.Message}", error: ex.Message);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                _isBusy = false;
                _currentCts?.Dispose();
                _currentCts = null;
            }
        }

        private void UpdateDeviceProgress(
            string key, 
            string target, 
            TaskStage stage, 
            int percent, 
            string message, 
            double duration = 0, 
            string? error = null)
        {
            var p = new DeviceTaskProgress
            {
                DeviceNo = key,
                Target = target,
                Stage = stage,
                Percent = percent,
                Message = message,
                DurationSeconds = duration,
                Error = error
            };
            _progressMap[key] = p;
            OnProgressUpdated?.Invoke(p);
        }
    }
}

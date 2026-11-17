using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PicoManager.Server.Native;

namespace PicoManager.Server.Services
{
    public class ScreenMonitorService
    {
        private readonly ConcurrentDictionary<string, byte[]> _latestFrames = new();
        private readonly ConcurrentDictionary<string, HashSet<Func<byte[], Task>>> _subscribers = new();
        private readonly object _subLock = new();

        public void HandleRawFramePointer(IntPtr userData, bool isLegacy = false)
        {
            if (userData == IntPtr.Zero) return;

            try
            {
                if (isLegacy)
                {
                    var blob = Marshal.PtrToStructure<PXREAFrameBlob>(userData);
                    HandleBlob(blob.devID, blob.addr, blob.width, blob.height, blob.bytesPerLine, blob.pixFormat);
                }
                else
                {
                    var multi = Marshal.PtrToStructure<PXREAFrameBlobMultiplyMonitor>(userData);
                    if (multi.width > 0 && multi.width <= 4096 && multi.height > 0 && multi.height <= 4096)
                    {
                        HandleBlob(multi.devID, multi.addr, multi.width, multi.height, multi.bytesPerLine, multi.pixFormat);
                    }
                    else
                    {
                        var blob = Marshal.PtrToStructure<PXREAFrameBlob>(userData);
                        HandleBlob(blob.devID, blob.addr, blob.width, blob.height, blob.bytesPerLine, blob.pixFormat);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenMonitorService] 解析帧异常: {ex.Message}");
            }
        }

        public void HandleRawFrame(PXREAFrameBlobMultiplyMonitor frame)
        {
            HandleBlob(frame.devID, frame.addr, frame.width, frame.height, frame.bytesPerLine, frame.pixFormat);
        }

        private void HandleBlob(string devID, IntPtr addr, uint width, uint height, uint bytesPerLine, int pixFormat)
        {
            if (string.IsNullOrEmpty(devID)) return;
            string cleanDevId = devID.Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrEmpty(cleanDevId) || addr == IntPtr.Zero || width == 0 || height == 0 || width > 4096 || height > 4096)
                return;

            byte[] jpeg;
            if (pixFormat == 4) // BGRA
            {
                jpeg = ScreenConverter.BgraToJpeg(addr, (int)width, (int)height, (int)bytesPerLine, 75);
            }
            else // Format 13/18 = YUV420P
            {
                jpeg = ScreenConverter.Yuv420PToJpeg(addr, (int)width, (int)height, 75);
            }

            if (jpeg.Length > 0)
            {
                UpdateFrame(cleanDevId, jpeg);
            }
        }

        private readonly ConcurrentDictionary<string, long> _lastFrameTimestamp = new();
        private readonly ConcurrentDictionary<string, int> _dispatchInProgress = new();

        public void UpdateFrame(string sn, byte[] jpeg)
        {
            // 限频保护：防止单机高频渲染（如 60-90Hz）导致转码及网络广播拥塞，默认限制为 20fps (50ms 采样)
            long nowMs = Environment.TickCount64;
            if (_lastFrameTimestamp.TryGetValue(sn, out long lastMs) && (nowMs - lastMs) < 45)
            {
                return;
            }
            _lastFrameTimestamp[sn] = nowMs;

            _latestFrames[sn] = jpeg;

            List<Func<byte[], Task>> callbacks;
            lock (_subLock)
            {
                if (!_subscribers.TryGetValue(sn, out var set) || set.Count == 0)
                {
                    return;
                }
                callbacks = new List<Func<byte[], Task>>(set);
            }

            // 防堆叠保护：如果上一次广播任务尚未结束，则跳过本次广播（保持最新帧在 _latestFrames 即可）
            if (_dispatchInProgress.TryGetValue(sn, out int inProgress) && inProgress == 1)
            {
                return;
            }
            _dispatchInProgress[sn] = 1;

            Task.Run(async () =>
            {
                try
                {
                    foreach (var cb in callbacks)
                    {
                        try
                        {
                            await cb(jpeg);
                        }
                        catch { }
                    }
                }
                finally
                {
                    _dispatchInProgress[sn] = 0;
                }
            });
        }

        public byte[]? GetLatestFrame(string sn)
        {
            _latestFrames.TryGetValue(sn, out var frame);
            return frame;
        }

        public IDisposable Subscribe(string sn, Func<byte[], Task> callback)
        {
            lock (_subLock)
            {
                if (!_subscribers.TryGetValue(sn, out var set))
                {
                    set = new HashSet<Func<byte[], Task>>();
                    _subscribers[sn] = set;
                }
                set.Add(callback);
            }

            return new SubscriptionToken(() =>
            {
                lock (_subLock)
                {
                    if (_subscribers.TryGetValue(sn, out var set))
                    {
                        set.Remove(callback);
                        if (set.Count == 0)
                        {
                            _subscribers.TryRemove(sn, out _);
                        }
                    }
                }
            });
        }

        public bool HasSubscribers(string sn)
        {
            lock (_subLock)
            {
                return _subscribers.TryGetValue(sn, out var set) && set.Count > 0;
            }
        }

        private class SubscriptionToken : IDisposable
        {
            private readonly Action _unsubscribe;
            private int _disposed = 0;

            public SubscriptionToken(Action unsubscribe)
            {
                _unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _unsubscribe();
                }
            }
        }
    }
}

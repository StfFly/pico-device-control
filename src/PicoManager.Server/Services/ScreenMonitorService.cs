using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PicoManager.Server.Models;
using PicoManager.Server.Native;

namespace PicoManager.Server.Services
{
    public sealed class RawFrameData
    {
        public string DevId { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint BytesPerLine { get; set; }
        public int PixFormat { get; set; }
    }

    public class ScreenMonitorService
    {
        private readonly ConcurrentDictionary<string, byte[]> _latestFrames = new();
        private readonly ConcurrentDictionary<string, HashSet<Func<byte[], Task>>> _subscribers = new();
        private readonly ConcurrentDictionary<string, DeviceStreamStatus> _streamStatuses = new();
        private readonly object _subLock = new();

        // 异步有界通道：解耦 PICO SDK 非托管 C++ 回调线程与 Bitmap/JPEG 软编码 CPU 密集任务
        // DropOldest: 当消费慢时自动丢弃最旧帧，确保零延迟并坚决不阻塞 PICO SDK 原生消息泵
        private readonly Channel<RawFrameData> _frameChannel = Channel.CreateBounded<RawFrameData>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public ScreenMonitorService()
        {
            Task.Run(ProcessFramesWorkerAsync);
        }

        private async Task ProcessFramesWorkerAsync()
        {
            var reader = _frameChannel.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var frame))
                {
                    try
                    {
                        byte[] jpeg;
                        if (frame.PixFormat == 4) // BGRA
                        {
                            jpeg = ScreenConverter.BgraToJpeg(frame.Data, (int)frame.Width, (int)frame.Height, (int)frame.BytesPerLine, 75);
                        }
                        else // Format 13/18 = YUV420P
                        {
                            jpeg = ScreenConverter.Yuv420PToJpeg(frame.Data, (int)frame.Width, (int)frame.Height, 75);
                        }

                        if (jpeg.Length > 0)
                        {
                            UpdateFrame(frame.DevId, jpeg);
                        }
                        else
                        {
                            Console.WriteLine($"[ScreenMonitorService] 警告: 帧转码返回空字节 (SN={frame.DevId}, {frame.Width}x{frame.Height}, PixFormat={frame.PixFormat})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ScreenMonitorService] 异步编码帧异常: {ex.Message}");
                    }
                }
            }
        }

        private readonly ConcurrentDictionary<string, bool> _firstFrameLogged = new();

        public void HandleRawFramePointer(IntPtr userData, bool isLegacy = false)
        {
            if (userData == IntPtr.Zero) return;

            try
            {
                if (isLegacy)
                {
                    var blob = Marshal.PtrToStructure<PXREAFrameBlob>(userData);
                    EnqueueBlob(blob.devID, blob.addr, blob.width, blob.height, blob.bytesPerLine, blob.pixFormat);
                }
                else
                {
                    var multi = Marshal.PtrToStructure<PXREAFrameBlobMultiplyMonitor>(userData);
                    if (multi.width > 0 && multi.width <= 4096 && multi.height > 0 && multi.height <= 4096)
                    {
                        EnqueueBlob(multi.devID, multi.addr, multi.width, multi.height, multi.bytesPerLine, multi.pixFormat);
                    }
                    else
                    {
                        var blob = Marshal.PtrToStructure<PXREAFrameBlob>(userData);
                        EnqueueBlob(blob.devID, blob.addr, blob.width, blob.height, blob.bytesPerLine, blob.pixFormat);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenMonitorService] 快速分发原始帧异常: {ex.Message}");
            }
        }

        public void HandleRawFrame(PXREAFrameBlobMultiplyMonitor frame)
        {
            EnqueueBlob(frame.devID, frame.addr, frame.width, frame.height, frame.bytesPerLine, frame.pixFormat);
        }

        public DeviceStreamStatus GetStreamStatus(string sn)
        {
            return _streamStatuses.GetOrAdd(sn, s => new DeviceStreamStatus { Sn = s });
        }

        public IReadOnlyDictionary<string, DeviceStreamStatus> GetAllStreamStatuses()
        {
            return _streamStatuses;
        }

        public void RecordStartRequested(string sn, int ret, string diagnosticMessage, StreamPhase phase)
        {
            var st = GetStreamStatus(sn);
            st.IsRequested = true;
            st.LastStartResult = ret;
            st.DiagnosticMessage = diagnosticMessage;
            st.Phase = phase;
            st.RequestedTime = DateTime.Now;
        }

        public void RecordStopRequested(string sn)
        {
            var st = GetStreamStatus(sn);
            st.IsRequested = false;
            st.Phase = StreamPhase.Standby;
            st.DiagnosticMessage = "省流待命中 (已手动停止拉流)";
        }

        public void EnqueueBlob(string devID, IntPtr addr, uint width, uint height, uint bytesPerLine, int pixFormat)
        {
            if (string.IsNullOrEmpty(devID)) return;
            string cleanDevId = devID.Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrEmpty(cleanDevId) || addr == IntPtr.Zero || width == 0 || height == 0 || width > 4096 || height > 4096)
                return;

            int dataSize;
            if (pixFormat == 4) // BGRA
            {
                dataSize = (int)(bytesPerLine > 0 ? bytesPerLine * height : width * height * 4);
            }
            else // Format 13/18 = YUV420P
            {
                dataSize = (int)(width * height * 3 / 2);
            }

            if (dataSize <= 0 || dataSize > 32 * 1024 * 1024)
                return;

            var st = GetStreamStatus(cleanDevId);
            st.Phase = StreamPhase.Streaming;
            st.FirstFrameTime ??= DateTime.Now;
            st.LastFrameTime = DateTime.Now;
            st.TotalFrames++;
            st.LastWidth = width;
            st.LastHeight = height;
            st.PixFormat = pixFormat;
            st.DiagnosticMessage = $"● 正在推流 ({width}x{height}, {(pixFormat == 4 ? "BGRA" : "YUV420P")})";

            if (_firstFrameLogged.TryAdd(cleanDevId, true))
            {
                Console.WriteLine($"[ScreenMonitorService] ★ 成功接收到头显推流首帧! SN={cleanDevId}, 分辨率={width}x{height}, 格式={pixFormat}, 字节={dataSize}");
            }

            // 极速内存拷贝（耗时 < 0.2ms），迅速解放 PICO C++ 回调线程
            byte[] buffer = new byte[dataSize];
            Marshal.Copy(addr, buffer, 0, dataSize);

            _frameChannel.Writer.TryWrite(new RawFrameData
            {
                DevId = cleanDevId,
                Data = buffer,
                Width = width,
                Height = height,
                BytesPerLine = bytesPerLine,
                PixFormat = pixFormat
            });
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

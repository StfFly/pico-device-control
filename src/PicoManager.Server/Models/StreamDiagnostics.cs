using System;
using System.Collections.Generic;

namespace PicoManager.Server.Models
{
    public enum StreamPhase
    {
        Standby,            // 待机中（未请求推流）
        SuiteOffline,       // 官方套件离线 (60061 端口不可达或进程未运行)
        Requesting,         // 正在向套件发送监看指令 (StartMonitorScreen)
        WaitingFirstFrame,  // 监看指令已下发，等待头显 ByteRTC 推流响应
        Streaming,          // 正在流畅推流中
        Error               // 异常报错
    }

    public class DeviceStreamStatus
    {
        public string Sn { get; set; } = string.Empty;
        public bool IsRequested { get; set; }
        public StreamPhase Phase { get; set; } = StreamPhase.Standby;
        public int LastStartResult { get; set; } = 999; // 999: 未曾启动
        public string DiagnosticMessage { get; set; } = "待命中 (点击开启实时监看)";
        public DateTime? RequestedTime { get; set; }
        public DateTime? FirstFrameTime { get; set; }
        public DateTime? LastFrameTime { get; set; }
        public long TotalFrames { get; set; }
        public uint LastWidth { get; set; }
        public uint LastHeight { get; set; }
        public int PixFormat { get; set; } // 4: BGRA, 13/18: YUV420P
        public double Fps { get; set; }
    }

    public class SuiteHealthStatus
    {
        public bool IsProcessRunning { get; set; }
        public bool IsPortOpen { get; set; }
        public bool IsSdkActive { get; set; }
        public bool IsServerConnected { get; set; }
        public int ListenPort { get; set; } = 60061;
        public string StatusSummary { get; set; } = string.Empty;
        public string Advice { get; set; } = string.Empty;
        public DateTime CheckTime { get; set; } = DateTime.Now;
        public Dictionary<string, DeviceStreamStatus> Streams { get; set; } = new();
    }
}

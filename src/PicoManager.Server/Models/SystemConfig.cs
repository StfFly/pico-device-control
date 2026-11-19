using System.Collections.Generic;

namespace PicoManager.Server.Models
{
    public class AppProject
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "vr_app"; // vr_app | vr_video
        public string PackageName { get; set; } = string.Empty;
        public int DurationMinutes { get; set; } = 15;
        public string Icon { get; set; } = "🎮";
        public string ExtraJson { get; set; } = string.Empty;
    }

    public class DeviceGroup
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DefaultProjectId { get; set; } = string.Empty;
        public List<string> DeviceIdentifiers { get; set; } = new();
    }

    public class SystemConfig
    {
        public int WebPort { get; set; } = 8999;
        public int UdpPort { get; set; } = 8998;
        public bool EnableUdpService { get; set; } = true;
        public bool AutoLockOnFind { get; set; } = true;
        public bool EnableSimulatorFallback { get; set; } = true;
        public List<AppProject> Projects { get; set; } = new();
        public List<DeviceGroup> Groups { get; set; } = new();
        public Dictionary<string, string> HeadsetAliases { get; set; } = new();
        /// <summary>
        /// 机器号或 SN 与默认启动游戏项目 Id 或包名的持久化映射表
        /// 例如: { "01": "com.vr.ocean", "P4E-880001": "com.vr.ocean" }
        /// </summary>
        public Dictionary<string, string> DeviceDefaultProjects { get; set; } = new();
    }

    public class UdpCommandMessage
    {
        public string? DeviceNo { get; set; }
        public string? Action { get; set; }
        public string? PackageName { get; set; }
        public string? ExtraJson { get; set; }
    }

    public class BatchCommandRequest
    {
        public List<string> SnList { get; set; } = new();
        public string PackageName { get; set; } = string.Empty;
        public string ExtraJson { get; set; } = string.Empty;
        public int Volume { get; set; } = 70;
    }

    public class SetAliasRequest
    {
        public string SN { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
    }

    public class ScreenStateRequest
    {
        public bool ScreenOn { get; set; } = true;
    }

    public class BatchScreenRequest
    {
        public List<string> SnList { get; set; } = new();
        public bool ScreenOn { get; set; } = true;
    }
}

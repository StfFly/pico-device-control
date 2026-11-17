using System;

namespace PicoManager.Server.Models
{
    public class PicoDevice
    {
        public string SN { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;       // 自定义业务别名 (例如: 1号机、VIP01)
        public string SuiteNo { get; set; } = string.Empty;     // PICO 企业套件编号 (例如: 022, 01)
        public string Model { get; set; } = "PICO VR";
        public bool Online { get; set; } = true;
        public bool Locked { get; set; } = false;
        public uint Battery { get; set; } = 100;
        public bool IsCharging { get; set; } = false;
        public bool ScreenOn { get; set; } = true;
        public bool IsWorn { get; set; } = false;
        public uint ControllerLeftBattery { get; set; } = 0;
        public uint ControllerRightBattery { get; set; } = 0;
        public string CurrentApp { get; set; } = "系统桌面";
        public string AppTitle { get; set; } = "系统桌面";
        public bool TrackingLost { get; set; } = false;         // 大空间定位丢失状态 (运行 com.pvr.seethrough.setting)
        public bool IsStreaming { get; set; } = false;
        public string IpAddress { get; set; } = "127.0.0.1";
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public bool IsDemo { get; set; } = false;

        public string DisplayName => !string.IsNullOrWhiteSpace(Alias) 
            ? Alias 
            : (!string.IsNullOrWhiteSpace(SuiteNo) ? SuiteNo : SN);
    }
}

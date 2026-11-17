using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PicoDeployer.Models
{
    public class DeviceInfo
    {
        [JsonPropertyName("deviceNo")]
        public string DeviceNo { get; set; } = string.Empty;

        [JsonPropertyName("alias")]
        public string Alias { get; set; } = string.Empty;

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; } = 5555;

        [JsonPropertyName("sn")]
        public string Sn { get; set; } = string.Empty;

        [JsonPropertyName("isConnected")]
        public bool IsConnected { get; set; }

        [JsonPropertyName("battery")]
        public int Battery { get; set; } = -1;

        [JsonPropertyName("androidVersion")]
        public string AndroidVersion { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("lastChecked")]
        public DateTime? LastChecked { get; set; }

        [JsonPropertyName("customVars")]
        public Dictionary<string, string> CustomVars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonIgnore]
        public string AdbTarget => string.IsNullOrWhiteSpace(Ip) ? Sn : $"{Ip}:{Port}";

        [JsonPropertyName("displayName")]
        public string DisplayName => !string.IsNullOrWhiteSpace(Alias) 
            ? Alias 
            : (!string.IsNullOrWhiteSpace(DeviceNo) ? $"{DeviceNo}号机" : AdbTarget);
    }

    public class DeploySettings
    {
        [JsonPropertyName("adbPath")]
        public string AdbPath { get; set; } = "adb";

        [JsonPropertyName("webPort")]
        public int WebPort { get; set; } = 9005;

        [JsonPropertyName("defaultConcurrency")]
        public int DefaultConcurrency { get; set; } = 3;

        [JsonPropertyName("defaultRemoteConfigPath")]
        public string DefaultRemoteConfigPath { get; set; } = "/sdcard/Android/data/com.picovr.app/files/config.json";

        [JsonPropertyName("devices")]
        public List<DeviceInfo> Devices { get; set; } = new();
    }

    public enum TaskStage
    {
        Pending,
        Connecting,
        Transferring,
        Installing,
        Verifying,
        Success,
        Failed
    }

    public class DeviceTaskProgress
    {
        [JsonPropertyName("deviceNo")]
        public string DeviceNo { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("stage")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TaskStage Stage { get; set; } = TaskStage.Pending;

        [JsonPropertyName("percent")]
        public int Percent { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "待命中";

        [JsonPropertyName("speed")]
        public string Speed { get; set; } = string.Empty;

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public class BatchInstallRequest
    {
        [JsonPropertyName("apkPath")]
        public string ApkPath { get; set; } = string.Empty;

        [JsonPropertyName("targetDevices")]
        public List<string> TargetDevices { get; set; } = new();

        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 3;

        [JsonPropertyName("autoLaunch")]
        public bool AutoLaunch { get; set; }

        [JsonPropertyName("packageName")]
        public string? PackageName { get; set; }
    }

    public class BatchConfigRequest
    {
        [JsonPropertyName("templateContent")]
        public string TemplateContent { get; set; } = string.Empty;

        [JsonPropertyName("remoteTargetPath")]
        public string RemoteTargetPath { get; set; } = string.Empty;

        [JsonPropertyName("targetDevices")]
        public List<string> TargetDevices { get; set; } = new();

        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 4;
    }

    public class BatchFilePushRequest
    {
        [JsonPropertyName("localFilePath")]
        public string LocalFilePath { get; set; } = string.Empty;

        [JsonPropertyName("remoteDirPath")]
        public string RemoteDirPath { get; set; } = "/sdcard/Movies/";

        [JsonPropertyName("targetDevices")]
        public List<string> TargetDevices { get; set; } = new();

        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 3;
    }

    public class PreviewConfigRequest
    {
        [JsonPropertyName("templateContent")]
        public string TemplateContent { get; set; } = string.Empty;

        [JsonPropertyName("deviceNo")]
        public string DeviceNo { get; set; } = string.Empty;
    }

    public class PullConfigRequest
    {
        [JsonPropertyName("deviceNo")]
        public string DeviceNo { get; set; } = string.Empty;

        [JsonPropertyName("remoteTargetPath")]
        public string RemoteTargetPath { get; set; } = string.Empty;
    }
}

using System;
using System.IO;
using System.Text.Json;
using PicoManager.Server.Models;

namespace PicoManager.Server.Services
{
    public class ConfigService
    {
        private readonly string _configFilePath;
        private readonly object _lock = new();
        public SystemConfig CurrentConfig { get; private set; }

        public ConfigService(IWebHostEnvironment env)
        {
            string rootConfig = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "config.json"));
            string localConfig = Path.Combine(env.ContentRootPath, "config.json");
            _configFilePath = File.Exists(rootConfig) ? rootConfig : localConfig;
            Console.WriteLine($"[ConfigService] 使用配置文件: {_configFilePath}");
            CurrentConfig = LoadOrCreateDefault();
        }

        public SystemConfig LoadOrCreateDefault()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_configFilePath))
                    {
                        var json = File.ReadAllText(_configFilePath);
                        var loaded = JsonSerializer.Deserialize<SystemConfig>(json);
                        if (loaded != null) return loaded;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConfigService] 读取 config.json 失败: {ex.Message}");
                }

                var defaultConfig = new SystemConfig();
                defaultConfig.Projects.Add(new AppProject
                {
                    Id = "proj-1",
                    Name = "宇宙星空奇遇 360°",
                    Type = "vr_video",
                    PackageName = "com.pico.space",
                    DurationMinutes = 15,
                    Icon = "🪐"
                });
                defaultConfig.Projects.Add(new AppProject
                {
                    Id = "proj-2",
                    Name = "深海探秘大冒险",
                    Type = "vr_app",
                    PackageName = "com.vr.ocean",
                    DurationMinutes = 12,
                    Icon = "🐋"
                });
                defaultConfig.Projects.Add(new AppProject
                {
                    Id = "proj-3",
                    Name = "重返恐龙纪元",
                    Type = "vr_app",
                    PackageName = "com.vr.dino",
                    DurationMinutes = 10,
                    Icon = "🦖"
                });
                defaultConfig.Projects.Add(new AppProject
                {
                    Id = "proj-4",
                    Name = "PICO 官方全景影院",
                    Type = "vr_app",
                    PackageName = "com.pico.vrplayer",
                    DurationMinutes = 20,
                    Icon = "🎬"
                });

                SaveConfig(defaultConfig);
                return defaultConfig;
            }
        }

        public void SaveConfig(SystemConfig? config = null)
        {
            lock (_lock)
            {
                if (config != null) CurrentConfig = config;
                try
                {
                    var opt = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(CurrentConfig, opt);
                    File.WriteAllText(_configFilePath, json, System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ConfigService] 写入 config.json 失败: {ex.Message}");
                }
            }
        }

        public void SetAlias(string sn, string alias)
        {
            lock (_lock)
            {
                CurrentConfig.HeadsetAliases[sn] = alias;
                SaveConfig();
            }
        }

        public string GetAlias(string sn)
        {
            lock (_lock)
            {
                if (CurrentConfig.HeadsetAliases.TryGetValue(sn, out var alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    return alias;
                }
                return sn;
            }
        }

        public void SetDeviceDefaultProject(string key, string packageOrProjectId)
        {
            lock (_lock)
            {
                CurrentConfig.DeviceDefaultProjects[key] = packageOrProjectId;
                SaveConfig();
            }
        }

        public string? GetDeviceDefaultProject(string key)
        {
            lock (_lock)
            {
                if (CurrentConfig.DeviceDefaultProjects.TryGetValue(key, out var val))
                {
                    return val;
                }
                return null;
            }
        }
    }
}

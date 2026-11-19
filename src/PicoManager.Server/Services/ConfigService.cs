using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using PicoManager.Server.Models;

namespace PicoManager.Server.Services
{
    public class ConfigService
    {
        private readonly string _configFilePath;
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);
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
            _rwLock.EnterWriteLock();
            try
            {
                // 1. 尝试从主配置文件读取
                if (File.Exists(_configFilePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_configFilePath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var loaded = JsonSerializer.Deserialize<SystemConfig>(json);
                            if (loaded != null) return loaded;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ConfigService] 主配置读取失败: {ex.Message}，尝试从备份恢复...");
                    }
                }

                // 2. 容灾恢复：尝试从 .bak 恢复
                string bakPath = _configFilePath + ".bak";
                if (File.Exists(bakPath))
                {
                    try
                    {
                        var jsonBak = File.ReadAllText(bakPath);
                        if (!string.IsNullOrWhiteSpace(jsonBak))
                        {
                            var loadedBak = JsonSerializer.Deserialize<SystemConfig>(jsonBak);
                            if (loadedBak != null)
                            {
                                Console.WriteLine("[ConfigService] 成功从 config.json.bak 灾备文件恢复配置！");
                                SaveConfigInternal(loadedBak);
                                return loadedBak;
                            }
                        }
                    }
                    catch { }
                }

                // 3. 全新默认配置
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

                SaveConfigInternal(defaultConfig);
                return defaultConfig;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        public void SaveConfig(SystemConfig? config = null)
        {
            _rwLock.EnterWriteLock();
            try
            {
                SaveConfigInternal(config);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        private void SaveConfigInternal(SystemConfig? config)
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

                // 原子落盘机制：先写临时文件后原子移动覆盖，严防断电导致 0 字节文件崩溃
                string tmpPath = _configFilePath + ".tmp";
                string bakPath = _configFilePath + ".bak";

                File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);

                // 保留上一份安全备份
                if (File.Exists(_configFilePath))
                {
                    try { File.Copy(_configFilePath, bakPath, true); } catch { }
                }

                File.Move(tmpPath, _configFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigService] 原子写入 config.json 失败: {ex.Message}");
            }
        }

        public void SetAlias(string sn, string alias)
        {
            _rwLock.EnterWriteLock();
            try
            {
                CurrentConfig.HeadsetAliases[sn] = alias;
                SaveConfigInternal(null);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        public string GetAlias(string sn)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (CurrentConfig.HeadsetAliases.TryGetValue(sn, out var alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    return alias;
                }
                return sn;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        public void SetDeviceDefaultProject(string key, string packageOrProjectId)
        {
            _rwLock.EnterWriteLock();
            try
            {
                CurrentConfig.DeviceDefaultProjects[key] = packageOrProjectId;
                SaveConfigInternal(null);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        public string? GetDeviceDefaultProject(string key)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (CurrentConfig.DeviceDefaultProjects.TryGetValue(key, out var val))
                {
                    return val;
                }
                return null;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
    }
}

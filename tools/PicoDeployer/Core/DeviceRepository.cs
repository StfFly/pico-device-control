using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using PicoDeployer.Models;

namespace PicoDeployer.Core
{
    public class DeviceRepository
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private DeploySettings _settings = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public DeviceRepository(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devices.json");
            Load();
        }

        public DeploySettings Settings
        {
            get
            {
                lock (_lock) return _settings;
            }
        }

        public List<DeviceInfo> GetDevices()
        {
            lock (_lock)
            {
                return _settings.Devices.ToList();
            }
        }

        public DeviceInfo? GetDevice(string identifier)
        {
            lock (_lock)
            {
                return _settings.Devices.FirstOrDefault(d => 
                    d.DeviceNo.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
                    d.Alias.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
                    d.Ip.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
                    d.Sn.Equals(identifier, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_settings, JsonOptions);
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[警告] 保存 devices.json 失败: {ex.Message}");
                }
            }
        }

        public void Load()
        {
            lock (_lock)
            {
                if (File.Exists(_filePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_filePath);
                        var loaded = JsonSerializer.Deserialize<DeploySettings>(json, JsonOptions);
                        if (loaded != null)
                        {
                            _settings = loaded;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[警告] 读取 devices.json 异常: {ex.Message}，将使用默认配置");
                    }
                }

                // 若文件不存在，尝试从已有的 PicoManager.Server/config.json 自动吸取机号并生成默认列表
                _settings = new DeploySettings();
                AutoImportFromMainConfig();
                Save();
            }
        }

        public void UpsertDevice(DeviceInfo device)
        {
            lock (_lock)
            {
                var existing = _settings.Devices.FirstOrDefault(d => 
                    (!string.IsNullOrEmpty(device.DeviceNo) && d.DeviceNo.Equals(device.DeviceNo, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(device.Ip) && d.Ip.Equals(device.Ip, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    existing.Alias = !string.IsNullOrEmpty(device.Alias) ? device.Alias : existing.Alias;
                    existing.Ip = !string.IsNullOrEmpty(device.Ip) ? device.Ip : existing.Ip;
                    existing.Port = device.Port > 0 ? device.Port : existing.Port;
                    existing.Sn = !string.IsNullOrEmpty(device.Sn) ? device.Sn : existing.Sn;
                    if (device.CustomVars != null && device.CustomVars.Count > 0)
                    {
                        foreach (var (k, v) in device.CustomVars)
                        {
                            existing.CustomVars[k] = v;
                        }
                    }
                }
                else
                {
                    _settings.Devices.Add(device);
                }

                Save();
            }
        }

        public bool RemoveDevice(string identifier)
        {
            lock (_lock)
            {
                var removed = _settings.Devices.RemoveAll(d => 
                    d.DeviceNo.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
                    d.Ip.Equals(identifier, StringComparison.OrdinalIgnoreCase) ||
                    d.Sn.Equals(identifier, StringComparison.OrdinalIgnoreCase));

                if (removed > 0)
                {
                    Save();
                    return true;
                }
                return false;
            }
        }

        public void AutoImportFromMainConfig()
        {
            // 常见主配置文件查找位置
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\src\PicoManager.Server\config.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\config.json"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), @"src\PicoManager.Server\config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), @"config.json")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("HeadsetAliases", out var aliasesEl))
                        {
                            int index = 1;
                            foreach (var prop in aliasesEl.EnumerateObject())
                            {
                                var sn = prop.Name;
                                var alias = prop.Value.GetString() ?? $"0{index}";
                                var numOnly = System.Text.RegularExpressions.Regex.Replace(alias, @"\D", "");
                                if (string.IsNullOrEmpty(numOnly)) numOnly = index.ToString("D2");

                                if (!_settings.Devices.Any(d => d.Sn == sn || d.DeviceNo == numOnly))
                                {
                                    _settings.Devices.Add(new DeviceInfo
                                    {
                                        DeviceNo = numOnly,
                                        Alias = $"{alias}号机",
                                        Ip = $"192.168.1.{100 + index}", // 预设示例 IP
                                        Port = 5555,
                                        Sn = sn,
                                        CustomVars = new Dictionary<string, string>
                                        {
                                            ["SEAT_ID"] = numOnly,
                                            ["ROLE"] = index == 1 ? "Master" : "Client"
                                        }
                                    });
                                }
                                index++;
                            }
                        }
                        return;
                    }
                    catch { }
                }
            }

            // 若完全没有外部配置，提供示范性默认设备
            if (_settings.Devices.Count == 0)
            {
                _settings.Devices.Add(new DeviceInfo
                {
                    DeviceNo = "022",
                    Alias = "022号机",
                    Ip = "192.168.1.122",
                    Port = 5555,
                    Sn = "PA9410MGJ9090528G",
                    CustomVars = new Dictionary<string, string>
                    {
                        ["SEAT_ID"] = "22",
                        ["ROLE"] = "Client"
                    }
                });
                _settings.Devices.Add(new DeviceInfo
                {
                    DeviceNo = "01",
                    Alias = "01号机",
                    Ip = "192.168.1.101",
                    Port = 5555,
                    Sn = "PA9410MGJ9090101G",
                    CustomVars = new Dictionary<string, string>
                    {
                        ["SEAT_ID"] = "1",
                        ["ROLE"] = "Master"
                    }
                });
                _settings.Devices.Add(new DeviceInfo
                {
                    DeviceNo = "02",
                    Alias = "02号机",
                    Ip = "192.168.1.102",
                    Port = 5555,
                    Sn = "PA9410MGJ9090102G",
                    CustomVars = new Dictionary<string, string>
                    {
                        ["SEAT_ID"] = "2",
                        ["ROLE"] = "Client"
                    }
                });
            }
        }
    }
}

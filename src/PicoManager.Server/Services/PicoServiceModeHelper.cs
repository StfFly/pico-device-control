using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PicoManager.Server.Services
{
    public class PicoServiceStatus
    {
        public bool FoundIni { get; set; }
        public string IniPath { get; set; } = string.Empty;
        public bool RunAsService { get; set; }
        public bool IsProcessRunning { get; set; }
        public int ProcessId { get; set; }
        public string ProcessPath { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class PicoServiceResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public PicoServiceStatus Status { get; set; } = new();
    }

    public static class PicoServiceModeHelper
    {
        public static string ResolveIniPath()
        {
            // 1. 当前用户 AppData
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string p1 = Path.Combine(localApp, "PICOBusinessSuitData", "key-setting.ini");
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine(localApp, "PICOBusinessSuitData", "setting.ini");
            if (File.Exists(p2)) return p2;

            // 2. 搜索其他常见用户路径 (如 C:\Users\Admin)
            try
            {
                var usersDir = new DirectoryInfo(@"C:\Users");
                if (usersDir.Exists)
                {
                    foreach (var u in usersDir.GetDirectories())
                    {
                        string candidate = Path.Combine(u.FullName, "AppData", "Local", "PICOBusinessSuitData", "key-setting.ini");
                        if (File.Exists(candidate)) return candidate;

                        string candidate2 = Path.Combine(u.FullName, "AppData", "Local", "PICOBusinessSuitData", "setting.ini");
                        if (File.Exists(candidate2)) return candidate2;
                    }
                }
            }
            catch { }

            // 3. 安装目录 fallback
            string[] installPaths = {
                @"D:\Program Files (x86)\PICO Business Suite\setting.ini",
                @"C:\Program Files (x86)\PICO Business Suite\setting.ini",
                @"C:\Program Files\PICO Business Suite\setting.ini"
            };
            foreach (var ip in installPaths)
            {
                if (File.Exists(ip)) return ip;
            }

            // 默认返回当前用户的 key-setting.ini 路径 (即使尚未创建)
            return Path.Combine(localApp, "PICOBusinessSuitData", "key-setting.ini");
        }

        public static PicoServiceStatus GetStatus()
        {
            var status = new PicoServiceStatus();
            string iniPath = ResolveIniPath();
            status.IniPath = iniPath;
            status.FoundIni = File.Exists(iniPath);

            if (status.FoundIni)
            {
                try
                {
                    var lines = File.ReadAllLines(iniPath);
                    bool inServiceSection = false;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                        {
                            inServiceSection = trimmed.Equals("[Service]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }

                        if (inServiceSection && trimmed.StartsWith("runAsService", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split('=', 2);
                            if (parts.Length == 2 && bool.TryParse(parts[1].Trim(), out bool val))
                            {
                                status.RunAsService = val;
                            }
                            else if (parts.Length == 2 && parts[1].Trim().Equals("1"))
                            {
                                status.RunAsService = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    status.Message = "读取配置文件异常: " + ex.Message;
                }
            }

            // 检查 PICOBusinessSuite 进程
            try
            {
                var procs = Process.GetProcessesByName("PICOBusinessSuite");
                if (procs.Length > 0)
                {
                    status.IsProcessRunning = true;
                    status.ProcessId = procs[0].Id;
                    try
                    {
                        status.ProcessPath = procs[0].MainModule?.FileName ?? "";
                    }
                    catch { }
                }
            }
            catch { }

            return status;
        }

        public static PicoServiceResult SetServiceMode(bool enable, bool restartProcess = false)
        {
            var result = new PicoServiceResult();
            try
            {
                string iniPath = ResolveIniPath();
                string dir = Path.GetDirectoryName(iniPath)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 备份原始文件
                if (File.Exists(iniPath))
                {
                    string bakPath = iniPath + ".bak";
                    File.Copy(iniPath, bakPath, true);
                }

                List<string> lines = File.Exists(iniPath)
                    ? File.ReadAllLines(iniPath).ToList()
                    : new List<string>();

                bool inServiceSection = false;
                bool sectionFound = false;
                bool keyFound = false;
                List<string> newLines = new();

                string valStr = enable ? "true" : "false";

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        if (inServiceSection && !keyFound)
                        {
                            newLines.Add($"runAsService={valStr}");
                            keyFound = true;
                        }

                        inServiceSection = trimmed.Equals("[Service]", StringComparison.OrdinalIgnoreCase);
                        if (inServiceSection) sectionFound = true;
                        newLines.Add(line);
                        continue;
                    }

                    if (inServiceSection && trimmed.StartsWith("runAsService", StringComparison.OrdinalIgnoreCase))
                    {
                        newLines.Add($"runAsService={valStr}");
                        keyFound = true;
                        continue;
                    }

                    newLines.Add(line);
                }

                if (inServiceSection && !keyFound)
                {
                    newLines.Add($"runAsService={valStr}");
                    keyFound = true;
                }

                if (!sectionFound)
                {
                    if (newLines.Count > 0 && !string.IsNullOrWhiteSpace(newLines.Last()))
                    {
                        newLines.Add("");
                    }
                    newLines.Add("[Service]");
                    newLines.Add($"runAsService={valStr}");
                }

                File.WriteAllLines(iniPath, newLines, new UTF8Encoding(false));

                result.Success = true;
                result.Message = enable
                    ? $"已成功开启服务模式 (runAsService=true)，配置写入: {iniPath}"
                    : $"已成功关闭服务模式 (runAsService=false)，配置写入: {iniPath}";

                if (restartProcess)
                {
                    RestartProcessInternal();
                }

                result.Status = GetStatus();
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "修改配置失败: " + ex.Message;
                result.Status = GetStatus();
                return result;
            }
        }

        public static PicoServiceResult RestartProcess()
        {
            var result = new PicoServiceResult();
            try
            {
                bool restarted = RestartProcessInternal();
                result.Success = restarted;
                result.Message = restarted ? "PICO 企业服务客户端已成功重启！" : "未能重新启动客户端，请手动打开 PICOBusinessSuite.exe";
                result.Status = GetStatus();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "重启进程失败: " + ex.Message;
                result.Status = GetStatus();
            }
            return result;
        }

        private static bool RestartProcessInternal()
        {
            string? exePath = null;
            var procs = Process.GetProcessesByName("PICOBusinessSuite");
            foreach (var p in procs)
            {
                try
                {
                    if (string.IsNullOrEmpty(exePath)) exePath = p.MainModule?.FileName;
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
            }

            if (string.IsNullOrEmpty(exePath))
            {
                string[] candidates = {
                    @"D:\Program Files (x86)\PICO Business Suite\PICOBusinessSuite.exe",
                    @"C:\Program Files (x86)\PICO Business Suite\PICOBusinessSuite.exe",
                    @"C:\Program Files\PICO Business Suite\PICOBusinessSuite.exe"
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) { exePath = c; break; }
                }
            }

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                System.Threading.Thread.Sleep(800);
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };
                Process.Start(psi);
                return true;
            }
            return false;
        }
    }
}

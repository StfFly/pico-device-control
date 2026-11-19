using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PicoManager.Server.Services
{
    public class OtaVersionInfo
    {
        public string Version { get; set; } = "v1.3.0";
        public string BuildTime { get; set; } = "2026-11-17 16:50";
        public string Channel { get; set; } = "Release (商用稳定版)";
        public string OsPlatform { get; set; } = Environment.OSVersion.ToString();
        public string DotNetVersion { get; set; } = Environment.Version.ToString();
    }

    public class OtaService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<OtaService> _logger;
        private readonly HttpClient _httpClient;

        public OtaService(IHostApplicationLifetime lifetime, ILogger<OtaService> logger)
        {
            _lifetime = lifetime;
            _logger = logger;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public OtaVersionInfo GetVersionInfo()
        {
            return new OtaVersionInfo();
        }

        public async Task<(bool Success, string Message)> ApplyUpdateFromStreamAsync(Stream zipStream, string fileName)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var stagingDir = Path.Combine(baseDir, "_ota_staging");
                var extractedDir = Path.Combine(stagingDir, "extracted");

                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                }
                Directory.CreateDirectory(extractedDir);

                var tempZip = Path.Combine(stagingDir, "update.zip");
                using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await zipStream.CopyToAsync(fs);
                }

                _logger.LogInformation("[OTA] 接收到更新包，大小: {Length} 字节，正在安全解压校验...", new FileInfo(tempZip).Length);

                // 使用 SafeZipExtractor 安全解压更新包，防御 Zip Slip
                SafeZipExtractor.ExtractToDirectorySafe(tempZip, extractedDir, true);

                // 智能定位有效更新目录：优先寻找包含 PicoManager.Server.dll 的目录
                var dllFiles = Directory.GetFiles(extractedDir, "PicoManager.Server.dll", SearchOption.AllDirectories);
                if (dllFiles.Length > 0)
                {
                    var targetSourceDir = Path.GetDirectoryName(dllFiles[0])!;
                    if (targetSourceDir != extractedDir)
                    {
                        _logger.LogInformation("[OTA] 智能定位到内嵌服务目录: {Dir}", targetSourceDir);
                        extractedDir = targetSourceDir;
                    }
                }

                _logger.LogInformation("[OTA] 解压校验完成，生成升级热替换脚本...");

                var cleanBaseDir = baseDir.TrimEnd('\\', '/');
                var cleanExtractedDir = extractedDir.TrimEnd('\\', '/');
                var cleanStagingDir = stagingDir.TrimEnd('\\', '/');

                // 生成批处理脚本
                var batPath = Path.Combine(cleanBaseDir, "_apply_ota.bat");
                var batContent = new StringBuilder();
                batContent.AppendLine("@echo off");
                batContent.AppendLine("chcp 65001 >nul");
                batContent.AppendLine("echo ===================================================");
                batContent.AppendLine("echo [PICO OTA] 正在执行系统远程更新，请稍候...");
                batContent.AppendLine("echo ===================================================");
                batContent.AppendLine("timeout /t 1 /nobreak >nul");
                batContent.AppendLine("taskkill /F /IM PicoManager.Server.exe >nul 2>nul");
                batContent.AppendLine("timeout /t 2 /nobreak >nul");
                batContent.AppendLine("REM 1. 强力备份现有配置文件，杜绝更新丢失用户自定义游戏与机位别名");
                batContent.AppendLine($"if exist \"{cleanBaseDir}\\config.json\" copy /Y \"{cleanBaseDir}\\config.json\" \"{cleanBaseDir}\\config.json.user_backup\" >nul");
                batContent.AppendLine($"if exist \"{cleanBaseDir}\\..\\config.json\" copy /Y \"{cleanBaseDir}\\..\\config.json\" \"{cleanBaseDir}\\..\\config.json.user_backup\" >nul");
                batContent.AppendLine("REM 2. 从更新临时源彻底删除默认 config.json，杜绝源头覆盖");
                batContent.AppendLine($"if exist \"{cleanExtractedDir}\\config.json\" del /F /Q \"{cleanExtractedDir}\\config.json\" >nul");
                batContent.AppendLine($"if exist \"{cleanExtractedDir}\\server\\config.json\" del /F /Q \"{cleanExtractedDir}\\server\\config.json\" >nul");
                batContent.AppendLine("REM 3. 执行热替换");
                batContent.AppendLine($"robocopy \"{cleanExtractedDir}\" \"{cleanBaseDir}\" /E /R:5 /W:1 /XF config.json config.json.bak config.json.tmp config.json.user_backup _apply_ota.bat /XD _ota_staging logs /NFL /NDL /NJH /NJS >nul");
                batContent.AppendLine("REM 4. 强制还原用户原始配置");
                batContent.AppendLine($"if exist \"{cleanBaseDir}\\config.json.user_backup\" copy /Y \"{cleanBaseDir}\\config.json.user_backup\" \"{cleanBaseDir}\\config.json\" >nul");
                batContent.AppendLine($"if exist \"{cleanBaseDir}\\..\\config.json.user_backup\" copy /Y \"{cleanBaseDir}\\..\\config.json.user_backup\" \"{cleanBaseDir}\\..\\config.json\" >nul");
                batContent.AppendLine("echo [PICO OTA] 文件热替换完毕，正在启动最新版本服务...");
                batContent.AppendLine($"start \"\" \"{Path.Combine(cleanBaseDir, "PicoManager.Server.exe")}\"");
                batContent.AppendLine("timeout /t 2 /nobreak >nul");
                batContent.AppendLine($"rmdir /S /Q \"{cleanStagingDir}\" 2>nul");
                batContent.AppendLine($"del \"%~f0\" 2>nul");
                batContent.AppendLine("exit");

                await File.WriteAllTextAsync(batPath, batContent.ToString(), new UTF8Encoding(false));

                _logger.LogInformation("[OTA] 启动升级守护脚本并安全退出当前服务端...");

                // 异步拉起脚本并停止当前服务
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = batPath,
                            UseShellExecute = true,
                            CreateNoWindow = false,
                            WorkingDirectory = baseDir
                        };
                        Process.Start(psi);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[OTA] 启动更新脚本失败");
                    }
                    finally
                    {
                        _lifetime.StopApplication();
                    }
                });

                return (true, "OTA 升级包已成功接收并就绪，系统将在 2 秒内热重载重启生效！");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OTA] 应用更新包发生异常");
                return (false, $"更新失败: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message)> ApplyUpdateFromUrlAsync(string url)
        {
            try
            {
                _logger.LogInformation("[OTA] 正在从远程地址下载更新包: {Url}", url);
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                return await ApplyUpdateFromStreamAsync(stream, Path.GetFileName(url));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OTA] 从远程 URL 下载更新包出错");
                return (false, $"下载更新失败: {ex.Message}");
            }
        }
    }
}

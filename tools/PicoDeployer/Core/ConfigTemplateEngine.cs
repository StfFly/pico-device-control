using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PicoDeployer.Models;

namespace PicoDeployer.Core
{
    public class ConfigTemplateEngine
    {
        public static string RenderTemplate(string templateContent, DeviceInfo dev, int index = 1)
        {
            if (string.IsNullOrEmpty(templateContent))
                return string.Empty;

            var sb = new StringBuilder(templateContent);

            // 1. 内置常用宏变量
            var devNo = !string.IsNullOrWhiteSpace(dev.DeviceNo) ? dev.DeviceNo : dev.Alias;
            var numOnly = Regex.Replace(devNo, @"\D", "");
            if (string.IsNullOrEmpty(numOnly)) numOnly = index.ToString();

            sb.Replace("{{DEVICE_NO}}", devNo);
            sb.Replace("{{DEVICE_NO_INT}}", numOnly);
            sb.Replace("{{DEVICE_ALIAS}}", dev.DisplayName);
            sb.Replace("{{DEVICE_IP}}", dev.Ip);
            sb.Replace("{{DEVICE_PORT}}", dev.Port.ToString());
            sb.Replace("{{DEVICE_SN}}", dev.Sn);
            sb.Replace("{{INDEX}}", index.ToString());
            sb.Replace("{{INDEX_PAD2}}", index.ToString("D2"));
            sb.Replace("{{TIMESTAMP}}", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            sb.Replace("{{DATETIME}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // 2. 每台设备的独立自定义差异化变量
            if (dev.CustomVars != null)
            {
                foreach (var kv in dev.CustomVars)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var placeholder = "{{" + kv.Key.Trim() + "}}";
                    sb.Replace(placeholder, kv.Value ?? string.Empty);
                    // 也兼容大小写变体
                    sb.Replace(placeholder.ToUpperInvariant(), kv.Value ?? string.Empty);
                    sb.Replace(placeholder.ToLowerInvariant(), kv.Value ?? string.Empty);
                }
            }

            return sb.ToString();
        }

        public static string GeneratePreview(string templateContent, DeviceInfo dev, int index = 1)
        {
            return RenderTemplate(templateContent, dev, index);
        }

        public static string CreateRenderedTempFile(string templateContent, DeviceInfo dev, int index = 1)
        {
            var rendered = RenderTemplate(templateContent, dev, index);
            var tempDir = Path.Combine(Path.GetTempPath(), "PicoDeployer");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            var safeName = !string.IsNullOrWhiteSpace(dev.DeviceNo) ? dev.DeviceNo : dev.Ip.Replace('.', '_');
            var filePath = Path.Combine(tempDir, $"cfg_{safeName}_{Guid.NewGuid():N}.tmp");

            // 写入 UTF-8 无 BOM 文件
            File.WriteAllText(filePath, rendered, new UTF8Encoding(false));
            return filePath;
        }

        public static string? FindDeviceSpecificFile(string baseDirectory, DeviceInfo dev)
        {
            if (!Directory.Exists(baseDirectory)) return null;

            var candidates = new[]
            {
                Path.Combine(baseDirectory, dev.DeviceNo),
                Path.Combine(baseDirectory, dev.Alias),
                Path.Combine(baseDirectory, dev.Ip),
                Path.Combine(baseDirectory, dev.Sn)
            };

            foreach (var cand in candidates)
            {
                if (Directory.Exists(cand))
                {
                    var files = Directory.GetFiles(cand);
                    if (files.Length > 0) return files[0];
                }
                else if (File.Exists(cand))
                {
                    return cand;
                }
            }

            return null;
        }
    }
}

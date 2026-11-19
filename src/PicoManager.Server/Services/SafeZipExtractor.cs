using System;
using System.IO;
using System.IO.Compression;

namespace PicoManager.Server.Services
{
    /// <summary>
    /// 提供安全解压缩功能，严格防御 Zip Slip（目录穿越逃逸）安全漏洞。
    /// </summary>
    public static class SafeZipExtractor
    {
        /// <summary>
        /// 安全解压 ZIP 文件到指定目录。
        /// </summary>
        /// <param name="zipFilePath">ZIP 文件路径</param>
        /// <param name="destinationDirectory">目标解压根目录</param>
        /// <param name="overwrite">是否覆盖已存在的文件</param>
        public static void ExtractToDirectorySafe(string zipFilePath, string destinationDirectory, bool overwrite = true)
        {
            if (string.IsNullOrEmpty(zipFilePath))
                throw new ArgumentNullException(nameof(zipFilePath));
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new ArgumentNullException(nameof(destinationDirectory));

            using var archive = ZipFile.OpenRead(zipFilePath);
            ExtractArchiveSafe(archive, destinationDirectory, overwrite);
        }

        /// <summary>
        /// 从已打开的 ZipArchive 中安全提取所有条目。
        /// </summary>
        public static void ExtractArchiveSafe(ZipArchive archive, string destinationDirectory, bool overwrite = true)
        {
            if (archive == null)
                throw new ArgumentNullException(nameof(archive));
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new ArgumentNullException(nameof(destinationDirectory));

            var destDirFull = Path.GetFullPath(destinationDirectory);
            if (!destDirFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                destDirFull += Path.DirectorySeparatorChar;
            }

            Directory.CreateDirectory(destDirFull);

            foreach (var entry in archive.Entries)
            {
                // 计算目标文件的完整绝对路径并规范化
                var targetPath = Path.GetFullPath(Path.Combine(destDirFull, entry.FullName));

                // 核心安全校验：确保目标路径必须严格位于目标解压目录内
                if (!targetPath.StartsWith(destDirFull, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"[安全拦截] Zip Slip 攻击检测: 压缩包内非法路径 '{entry.FullName}' 试图逃逸至目标解压目录外！");
                }

                // 判断是否为目录项 (以 / 或 \ 结尾)
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                // 确保目标文件的父目录存在
                var parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                // 安全解压写入文件
                entry.ExtractToFile(targetPath, overwrite);
            }
        }
    }
}

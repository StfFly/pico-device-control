using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PicoManager.Server.Services
{
    public static class ScreenConverter
    {
        private static readonly ImageCodecInfo? JpegEncoder = GetEncoder(ImageFormat.Jpeg);

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        public static byte[] Yuv420PToJpeg(IntPtr yuvData, int width, int height, long quality = 75)
        {
            if (yuvData == IntPtr.Zero || width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return Array.Empty<byte>();

            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);

                try
                {
                    unsafe
                    {
                        byte* pY = (byte*)yuvData.ToPointer();
                        byte* pU = pY + (width * height);
                        byte* pV = pU + (width * height / 4);

                        byte* dstScan0 = (byte*)bmpData.Scan0.ToPointer();
                        int stride = bmpData.Stride;
                        int halfWidth = width >> 1;

                        for (int y = 0; y < height; y++)
                        {
                            byte* row = dstScan0 + (y * stride);
                            int yIdx = y * width;
                            int uvY = (y >> 1) * halfWidth;

                            for (int x = 0; x < width; x++)
                            {
                                int uvIdx = uvY + (x >> 1);

                                int yVal = pY[yIdx + x];
                                int uVal = pU[uvIdx] - 128;
                                int vVal = pV[uvIdx] - 128;

                                int r = yVal + ((359 * vVal) >> 8);
                                int g = yVal - ((88 * uVal + 183 * vVal) >> 8);
                                int b = yVal + ((454 * uVal) >> 8);

                                if (r < 0) r = 0; else if (r > 255) r = 255;
                                if (g < 0) g = 0; else if (g > 255) g = 255;
                                if (b < 0) b = 0; else if (b > 255) b = 255;

                                int pixelOffset = x * 3;
                                row[pixelOffset] = (byte)b;
                                row[pixelOffset + 1] = (byte)g;
                                row[pixelOffset + 2] = (byte)r;
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                return BitmapToJpegBytes(bmp, quality);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenConverter] YUV420P转JPEG异常 ({width}x{height}): {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public static byte[] Yuv420PToJpeg(byte[] yuvData, int width, int height, long quality = 75)
        {
            if (yuvData == null || yuvData.Length == 0 || width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return Array.Empty<byte>();

            int minSize = (width * height * 3) / 2;
            if (yuvData.Length < minSize)
                return Array.Empty<byte>();

            unsafe
            {
                fixed (byte* p = yuvData)
                {
                    return Yuv420PToJpeg((IntPtr)p, width, height, quality);
                }
            }
        }

        public static byte[] BgraToJpeg(byte[] bgraData, int width, int height, int bytesPerLine, long quality = 75)
        {
            if (bgraData == null || bgraData.Length == 0 || width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return Array.Empty<byte>();

            if (bytesPerLine <= 0)
                bytesPerLine = width * 4;

            unsafe
            {
                fixed (byte* p = bgraData)
                {
                    return BgraToJpeg((IntPtr)p, width, height, bytesPerLine, quality);
                }
            }
        }

        public static byte[] BgraToJpeg(IntPtr bgraData, int width, int height, int bytesPerLine, long quality = 75)
        {
            if (bgraData == IntPtr.Zero || width <= 0 || height <= 0 || width > 4096 || height > 4096)
                return Array.Empty<byte>();

            if (bytesPerLine <= 0)
                bytesPerLine = width * 4;

            try
            {
                using var bmp = new Bitmap(width, height, bytesPerLine, PixelFormat.Format32bppArgb, bgraData);
                return BitmapToJpegBytes(bmp, quality);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenConverter] BGRA转JPEG异常 ({width}x{height}): {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public static byte[] GeneratePlaceholderFrame(
            string sn,
            string alias,
            string currentApp,
            uint battery,
            bool isPlaying,
            PicoManager.Server.Models.DeviceStreamStatus? streamStatus = null,
            PicoManager.Server.Models.SuiteHealthStatus? suiteStatus = null)
        {
            int width = 640;
            int height = 360;
            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(15, 23, 42)); // Slate 900

                // Draw decorative grid / HUD
                using (var pen = new Pen(Color.FromArgb(30, 41, 59), 1))
                {
                    for (int x = 0; x < width; x += 40)
                        g.DrawLine(pen, x, 0, x, height);
                    for (int y = 0; y < height; y += 40)
                        g.DrawLine(pen, 0, y, width, y);
                }

                // Header badge
                using (var brush = new SolidBrush(Color.FromArgb(30, 58, 138)))
                {
                    g.FillRectangle(brush, 20, 16, 200, 32);
                }
                using (var font = new Font("Arial", 11, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString($"设备 #{alias} ({sn.Substring(Math.Max(0, sn.Length - 6))})", font, brush, 28, 23);
                }

                bool isSuiteOffline = (suiteStatus != null && !suiteStatus.IsPortOpen) ||
                                      (streamStatus != null && streamStatus.Phase == PicoManager.Server.Models.StreamPhase.SuiteOffline);

                if (isSuiteOffline)
                {
                    // 官方套件未就绪高亮告警 HUD
                    using (var warnBrush = new SolidBrush(Color.FromArgb(40, 239, 68, 68))) // 半透明红底
                    using (var warnPen = new Pen(Color.FromArgb(239, 68, 68), 1.5f))
                    {
                        g.FillRectangle(warnBrush, 40, 75, width - 80, 190);
                        g.DrawRectangle(warnPen, 40, 75, width - 80, 190);
                    }

                    using (var font = new Font("Microsoft YaHei", 15, FontStyle.Bold))
                    using (var brush = new SolidBrush(Color.FromArgb(248, 113, 113)))
                    {
                        string title = "⚠️ PICO 官方企业应用套件服务未连接";
                        var sz = g.MeasureString(title, font);
                        g.DrawString(title, font, brush, (width - sz.Width) / 2, 95);
                    }

                    using (var font = new Font("Microsoft YaHei", 11, FontStyle.Regular))
                    using (var brush = new SolidBrush(Color.FromArgb(254, 202, 202)))
                    {
                        string line1 = "原因: 本地 gRPC 端口 (127.0.0.1:60061) 离线或套件未登录";
                        var sz1 = g.MeasureString(line1, font);
                        g.DrawString(line1, font, brush, (width - sz1.Width) / 2, 138);

                        string line2 = "指引: 请确保在电脑上启动并登录 PICO Business Suite";
                        var sz2 = g.MeasureString(line2, font);
                        g.DrawString(line2, font, brush, (width - sz2.Width) / 2, 168);

                        string line3 = "官方套件登录后会自动开放 60061 端口，系统将秒级自动重连";
                        var sz3 = g.MeasureString(line3, font);
                        g.DrawString(line3, font, brush, (width - sz3.Width) / 2, 198);
                    }
                }
                else if (streamStatus?.Phase == PicoManager.Server.Models.StreamPhase.WaitingFirstFrame)
                {
                    // 等待首帧推流 HUD
                    using (var bgBrush = new SolidBrush(Color.FromArgb(40, 14, 165, 233))) // 半透明天蓝底
                    using (var bgPen = new Pen(Color.FromArgb(14, 165, 233), 1.5f))
                    {
                        g.FillRectangle(bgBrush, 40, 80, width - 80, 180);
                        g.DrawRectangle(bgPen, 40, 80, width - 80, 180);
                    }

                    using (var font = new Font("Microsoft YaHei", 14, FontStyle.Bold))
                    using (var brush = new SolidBrush(Color.FromArgb(56, 189, 248)))
                    {
                        string title = "📡 监看指令已下发至头显 (StartMonitorScreen)";
                        var sz = g.MeasureString(title, font);
                        g.DrawString(title, font, brush, (width - sz.Width) / 2, 105);
                    }

                    using (var font = new Font("Microsoft YaHei", 11, FontStyle.Regular))
                    using (var brush = new SolidBrush(Color.FromArgb(186, 230, 253)))
                    {
                        string line1 = "正在等待头显 ByteRTC 房间推流响应首帧...";
                        var sz1 = g.MeasureString(line1, font);
                        g.DrawString(line1, font, brush, (width - sz1.Width) / 2, 148);

                        string line2 = "提示: 请确保头盔佩戴/亮屏且与电脑连接在同一局域网 Wi-Fi";
                        var sz2 = g.MeasureString(line2, font);
                        g.DrawString(line2, font, brush, (width - sz2.Width) / 2, 180);
                    }
                }
                else
                {
                    // 标准待机 / 桌面 HUD
                    using (var font = new Font("Microsoft YaHei", 14, FontStyle.Bold))
                    using (var brush = new SolidBrush(isPlaying ? Color.FromArgb(74, 222, 128) : Color.FromArgb(148, 163, 184)))
                    {
                        string statusText = isPlaying ? "● 正在运行体验程序" : "○ 头显待机中 / 系统桌面";
                        var size = g.MeasureString(statusText, font);
                        g.DrawString(statusText, font, brush, (width - size.Width) / 2, 100);
                    }

                    using (var font = new Font("Microsoft YaHei", 16, FontStyle.Bold))
                    using (var brush = new SolidBrush(Color.White))
                    {
                        var size = g.MeasureString(currentApp, font);
                        g.DrawString(currentApp, font, brush, (width - size.Width) / 2, 138);
                    }

                    // 诊断提示
                    string displayHint = streamStatus?.DiagnosticMessage ?? "💡 省流待命中，点击右侧/控制台「开启监看」拉取实时画面";
                    using (var font = new Font("Microsoft YaHei", 11, FontStyle.Regular))
                    using (var brush = new SolidBrush(Color.FromArgb(56, 189, 248)))
                    {
                        var size = g.MeasureString(displayHint, font);
                        g.DrawString(displayHint, font, brush, (width - size.Width) / 2, 180);
                    }

                    using (var font = new Font("Microsoft YaHei", 10, FontStyle.Regular))
                    using (var brush = new SolidBrush(Color.FromArgb(100, 116, 139)))
                    {
                        string subHint = "PICO 官方企业应用套件协同通道 | 支持 480P~1080P 自适应帧率";
                        var size = g.MeasureString(subHint, font);
                        g.DrawString(subHint, font, brush, (width - size.Width) / 2, 212);
                    }
                }

                // 底部状态栏
                using (var font = new Font("Microsoft YaHei", 10, FontStyle.Regular))
                using (var brush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    string suiteTag = (suiteStatus?.IsPortOpen == true || streamStatus?.Phase == PicoManager.Server.Models.StreamPhase.Streaming) ? "🟢 官方套件已联机" : "🔴 官方套件服务未连接";
                    g.DrawString($"SN: {sn} | 电量: {battery}%", font, brush, 25, 320);
                    g.DrawString($"{suiteTag} | {DateTime.Now:HH:mm:ss}", font, brush, 360, 320);
                }
            }

            return BitmapToJpegBytes(bmp, 80);
        }

        private static byte[] BitmapToJpegBytes(Bitmap bmp, long quality)
        {
            using var ms = new MemoryStream();
            if (JpegEncoder != null)
            {
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                bmp.Save(ms, JpegEncoder, encoderParams);
            }
            else
            {
                bmp.Save(ms, ImageFormat.Jpeg);
            }
            return ms.ToArray();
        }
    }
}

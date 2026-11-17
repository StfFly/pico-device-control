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
            catch
            {
                return Array.Empty<byte>();
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
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public static byte[] GeneratePlaceholderFrame(string sn, string alias, string currentApp, uint battery, bool isPlaying)
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
                    g.FillRectangle(brush, 20, 20, 180, 36);
                }
                using (var font = new Font("Arial", 12, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString($"设备 #{alias}", font, brush, 30, 28);
                }

                // Center VR icon & info
                using (var font = new Font("Arial", 16, FontStyle.Bold))
                using (var brush = new SolidBrush(isPlaying ? Color.FromArgb(74, 222, 128) : Color.FromArgb(148, 163, 184)))
                {
                    string statusText = isPlaying ? "● 正在运行程序" : "○ 待机中 / 桌面";
                    var size = g.MeasureString(statusText, font);
                    g.DrawString(statusText, font, brush, (width - size.Width) / 2, 120);
                }

                using (var font = new Font("Arial", 18, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.White))
                {
                    var size = g.MeasureString(currentApp, font);
                    g.DrawString(currentApp, font, brush, (width - size.Width) / 2, 165);
                }

                // Bottom telemetry
                using (var font = new Font("Arial", 11, FontStyle.Regular))
                using (var brush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    g.DrawString($"SN: {sn}", font, brush, 25, 315);
                    g.DrawString($"电量: {battery}% | 时间: {DateTime.Now:HH:mm:ss}", font, brush, 400, 315);
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

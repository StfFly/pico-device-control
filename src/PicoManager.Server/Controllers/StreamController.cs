using System;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StreamController : ControllerBase
    {
        private readonly ScreenMonitorService _screenMonitorService;
        private readonly PicoDeviceManager _deviceManager;

        public StreamController(ScreenMonitorService screenMonitorService, PicoDeviceManager deviceManager)
        {
            _screenMonitorService = screenMonitorService;
            _deviceManager = deviceManager;
        }

        [HttpPost("{sn}/start")]
        public IActionResult StartStream(string sn)
        {
            int ret = _deviceManager.StartMonitor(sn);
            return Ok(new { success = true, ret });
        }

        [HttpPost("{sn}/stop")]
        public IActionResult StopStream(string sn)
        {
            int ret = _deviceManager.StopMonitor(sn);
            return Ok(new { success = true, ret });
        }

        [HttpGet("{sn}/frame")]
        public IActionResult GetFrame(string sn)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            Response.Headers.Append("Pragma", "no-cache");
            Response.Headers.Append("Expires", "0");

            var frame = _screenMonitorService.GetLatestFrame(sn);
            if (frame == null || frame.Length == 0)
            {
                var dev = _deviceManager.GetDevice(sn);
                frame = ScreenConverter.GeneratePlaceholderFrame(
                    sn,
                    dev?.DisplayName ?? sn,
                    dev?.AppTitle ?? "系统桌面",
                    dev?.Battery ?? 100,
                    dev?.CurrentApp != null && dev.CurrentApp != "系统桌面");
            }

            return File(frame, "image/jpeg");
        }

        [HttpGet("{sn}/status")]
        public IActionResult GetStreamStatus(string sn)
        {
            var frame = _screenMonitorService.GetLatestFrame(sn);
            bool hasReal = frame != null && frame.Length > 0;
            return Ok(new { hasRealFrame = hasReal, length = frame?.Length ?? 0 });
        }

        [HttpGet("{sn}/mjpeg")]
        public async Task GetMjpegStream(string sn)
        {
            Response.StatusCode = 200;
            Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            Response.Headers.CacheControl = "no-cache, private, no-store, must-revalidate";
            Response.Headers.Append("Pragma", "no-cache");
            Response.Headers.Append("Expires", "0");

            _deviceManager.StartMonitor(sn);

            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

            using var subToken = _screenMonitorService.Subscribe(sn, frame =>
            {
                channel.Writer.TryWrite(frame);
                return Task.CompletedTask;
            });

            // Write initial frame immediately
            var initial = _screenMonitorService.GetLatestFrame(sn);
            if (initial != null)
            {
                channel.Writer.TryWrite(initial);
            }
            else
            {
                var dev = _deviceManager.GetDevice(sn);
                var placeholder = ScreenConverter.GeneratePlaceholderFrame(
                    sn,
                    dev?.DisplayName ?? sn,
                    dev?.AppTitle ?? "系统桌面",
                    dev?.Battery ?? 100,
                    dev?.CurrentApp != null && dev.CurrentApp != "系统桌面");
                channel.Writer.TryWrite(placeholder);
            }

            try
            {
                var reader = channel.Reader;
                while (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    byte[]? frame = null;
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(1200);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, timeoutCts.Token);
                        if (await reader.WaitToReadAsync(linkedCts.Token))
                        {
                            reader.TryRead(out frame);
                        }
                    }
                    catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        // 1.2 秒内若未产生新画面帧，发送保活状态帧以防连接挂起
                    }

                    if (frame == null)
                    {
                        var dev = _deviceManager.GetDevice(sn);
                        frame = _screenMonitorService.GetLatestFrame(sn) ?? ScreenConverter.GeneratePlaceholderFrame(
                            sn,
                            dev?.DisplayName ?? sn,
                            dev?.AppTitle ?? "系统桌面",
                            dev?.Battery ?? 100,
                            dev?.CurrentApp != null && dev.CurrentApp != "系统桌面");
                    }

                    var header = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
                    var headerBytes = Encoding.ASCII.GetBytes(header);

                    await Response.Body.WriteAsync(headerBytes, 0, headerBytes.Length, HttpContext.RequestAborted);
                    await Response.Body.WriteAsync(frame, 0, frame.Length, HttpContext.RequestAborted);
                    await Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), 0, 2, HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                if (!_screenMonitorService.HasSubscribers(sn))
                {
                    _deviceManager.StopMonitor(sn);
                }
            }
        }
    }
}

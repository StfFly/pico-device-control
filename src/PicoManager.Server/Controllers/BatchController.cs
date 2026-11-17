using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Models;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BatchController : ControllerBase
    {
        private readonly PicoDeviceManager _deviceManager;

        public BatchController(PicoDeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        [HttpPost("start-app")]
        public async Task<IActionResult> BatchStartApp([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            if (string.IsNullOrWhiteSpace(req.PackageName))
                return BadRequest(new { message = "必须提供应用包名" });

            int count = await _deviceManager.BatchStartAppAsync(req.SnList, req.PackageName, req.ExtraJson ?? "");
            return Ok(new { success = true, processedCount = count });
        }

        [HttpPost("stop-app")]
        public async Task<IActionResult> BatchStopApp([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchStopAppAsync(req.SnList);
            return Ok(new { success = true, processedCount = count });
        }

        [HttpPost("pause-video")]
        public async Task<IActionResult> BatchPauseVideo([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchPauseVideoAsync(req.SnList);
            return Ok(new { success = true, processedCount = count });
        }

        [HttpPost("resume-video")]
        public async Task<IActionResult> BatchResumeVideo([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchContinueVideoAsync(req.SnList);
            return Ok(new { success = true, processedCount = count });
        }

        [HttpPost("volume")]
        public async Task<IActionResult> BatchVolume([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchSetVolumeAsync(req.SnList, req.Volume);
            return Ok(new { success = true, processedCount = count });
        }

        [HttpPost("reboot")]
        public async Task<IActionResult> BatchReboot([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchRebootAsync(req.SnList);
            return Ok(new { success = true, processedCount = count });
        }

        [HttpPost("screen")]
        public async Task<IActionResult> BatchScreen([FromBody] BatchScreenRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchSetScreenStateAsync(req.SnList, req.ScreenOn);
            return Ok(new { success = true, processedCount = count, screenOn = req.ScreenOn });
        }

        [HttpPost("shutdown")]
        public async Task<IActionResult> BatchShutdown([FromBody] BatchCommandRequest req)
        {
            if (req.SnList == null || req.SnList.Count == 0)
                return BadRequest(new { message = "请至少选择一台设备" });

            int count = await _deviceManager.BatchShutdownAsync(req.SnList);
            return Ok(new { success = true, processedCount = count });
        }
    }
}

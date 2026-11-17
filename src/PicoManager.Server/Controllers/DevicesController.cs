using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Models;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DevicesController : ControllerBase
    {
        private readonly PicoDeviceManager _deviceManager;

        public DevicesController(PicoDeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        [HttpGet]
        public ActionResult<IEnumerable<PicoDevice>> GetAll()
        {
            return Ok(_deviceManager.GetAllDevices());
        }

        [HttpGet("{sn}")]
        public ActionResult<PicoDevice> GetBySn(string sn)
        {
            var dev = _deviceManager.GetDevice(sn);
            if (dev == null) return NotFound(new { message = $"设备 {sn} 未找到" });
            return Ok(dev);
        }

        [HttpPost("{sn}/lock")]
        public IActionResult LockDevice(string sn)
        {
            int ret = _deviceManager.LockDevice(sn);
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/unlock")]
        public IActionResult UnlockDevice(string sn)
        {
            int ret = _deviceManager.UnlockDevice(sn);
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/recenter")]
        public IActionResult Recenter(string sn)
        {
            int ret = _deviceManager.Recenter(sn);
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/volume")]
        public IActionResult SetVolume(string sn, [FromBody] VolumeRequest req)
        {
            int ret = _deviceManager.SetVolume(sn, req.Volume);
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/alias")]
        public IActionResult SetAlias(string sn, [FromBody] SetAliasRequest req)
        {
            int ret = _deviceManager.SetDeviceAlias(sn, req.Alias);
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/screen")]
        public async Task<IActionResult> SetScreen(string sn, [FromBody] ScreenStateRequest req)
        {
            int ret = await _deviceManager.SetScreenStateAsync(sn, req.ScreenOn);
            return Ok(new { success = true, screenOn = req.ScreenOn, ret });
        }

        [HttpPost("{sn}/start-app")]
        public IActionResult StartApp(string sn, [FromBody] StartAppRequest req)
        {
            int ret = _deviceManager.StartApp(sn, req.PackageName, req.ExtraJson ?? "");
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/stop-app")]
        public IActionResult StopApp(string sn, [FromBody] StopAppRequest? req)
        {
            int ret = _deviceManager.StopApp(sn, req?.PackageName ?? "");
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/reboot")]
        public IActionResult Reboot(string sn)
        {
            int ret = _deviceManager.Reboot(sn);
            return Ok(new { success = ret == 0, ret });
        }

        [HttpPost("{sn}/shutdown")]
        public IActionResult Shutdown(string sn)
        {
            int ret = _deviceManager.Shutdown(sn);
            return Ok(new { success = ret == 0, ret });
        }
    }

    public class VolumeRequest
    {
        public int Volume { get; set; } = 70;
    }

    public class StartAppRequest
    {
        public string PackageName { get; set; } = string.Empty;
        public string? ExtraJson { get; set; }
    }

    public class StopAppRequest
    {
        public string? PackageName { get; set; }
    }
}

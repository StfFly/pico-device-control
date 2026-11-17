using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Models;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConfigController : ControllerBase
    {
        private readonly ConfigService _configService;
        private readonly PicoDeviceManager _deviceManager;

        public ConfigController(ConfigService configService, PicoDeviceManager deviceManager)
        {
            _configService = configService;
            _deviceManager = deviceManager;
        }

        [HttpGet]
        public ActionResult<SystemConfig> GetConfig()
        {
            return Ok(_configService.CurrentConfig);
        }

        [HttpPost("projects")]
        public IActionResult SaveProject([FromBody] AppProject project)
        {
            if (string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.PackageName))
            {
                return BadRequest(new { message = "项目名称和包名不能为空" });
            }

            var config = _configService.CurrentConfig;
            if (string.IsNullOrWhiteSpace(project.Id))
            {
                project.Id = "proj-" + Guid.NewGuid().ToString("N")[..8];
                config.Projects.Add(project);
            }
            else
            {
                var existing = config.Projects.FirstOrDefault(p => p.Id == project.Id);
                if (existing != null)
                {
                    existing.Name = project.Name;
                    existing.PackageName = project.PackageName;
                    existing.Type = project.Type;
                    existing.DurationMinutes = project.DurationMinutes;
                    existing.Icon = string.IsNullOrWhiteSpace(project.Icon) ? "🎮" : project.Icon;
                    existing.ExtraJson = project.ExtraJson;
                }
                else
                {
                    config.Projects.Add(project);
                }
            }

            _configService.SaveConfig();
            return Ok(new { success = true, project });
        }

        [HttpDelete("projects/{id}")]
        public IActionResult DeleteProject(string id)
        {
            var config = _configService.CurrentConfig;
            int removed = config.Projects.RemoveAll(p => p.Id == id);
            if (removed > 0)
            {
                _configService.SaveConfig();
                return Ok(new { success = true });
            }
            return NotFound(new { message = "项目不存在" });
        }

        [HttpPost("alias")]
        public IActionResult SetAlias([FromBody] SetAliasRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.SN))
            {
                return BadRequest(new { message = "SN 不能为空" });
            }

            _configService.SetAlias(req.SN, req.Alias);
            _deviceManager.SetDeviceAlias(req.SN, req.Alias);

            return Ok(new { success = true, sn = req.SN, alias = req.Alias });
        }

        [HttpPost("bind-headset")]
        public IActionResult BindHeadset([FromBody] BindHeadsetRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.SN))
            {
                return BadRequest(new { message = "SN 序列号不能为空" });
            }

            string alias = string.IsNullOrWhiteSpace(req.Alias) ? req.SN : req.Alias;
            _deviceManager.RegisterHeadset(req.SN.Trim(), alias.Trim(), req.Model ?? "PICO 4 Enterprise");

            return Ok(new { success = true, sn = req.SN.Trim(), alias = alias.Trim() });
        }

        [HttpDelete("bind-headset/{sn}")]
        public IActionResult UnbindHeadset(string sn)
        {
            if (string.IsNullOrWhiteSpace(sn)) return BadRequest();
            _deviceManager.UnbindHeadset(sn);
            return Ok(new { success = true });
        }

        [HttpPost("device-project")]
        public IActionResult SetDeviceProject([FromBody] DeviceProjectRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.DeviceKey))
            {
                return BadRequest(new { message = "机号/SN 标识不能为空" });
            }

            if (string.IsNullOrWhiteSpace(req.PackageOrProjectId))
            {
                _configService.CurrentConfig.DeviceDefaultProjects.Remove(req.DeviceKey);
                _configService.SaveConfig();
                return Ok(new { success = true, removed = true });
            }

            string key = req.DeviceKey.Trim();
            string pkg = req.PackageOrProjectId.Trim();
            _configService.SetDeviceDefaultProject(key, pkg);

            // 如果该 key 对应已知设备，同步绑定其 SN、Alias 与纯数字机号
            var dev = _deviceManager.ResolveDevices(key).FirstOrDefault();
            if (dev != null)
            {
                if (!string.IsNullOrWhiteSpace(dev.SN) && dev.SN != key)
                    _configService.SetDeviceDefaultProject(dev.SN, pkg);
                if (!string.IsNullOrWhiteSpace(dev.Alias) && dev.Alias != key)
                    _configService.SetDeviceDefaultProject(dev.Alias, pkg);
                string digits = System.Text.RegularExpressions.Regex.Replace(dev.Alias, @"\D", "");
                if (!string.IsNullOrEmpty(digits) && digits != key)
                    _configService.SetDeviceDefaultProject(digits, pkg);
            }

            return Ok(new { success = true, deviceKey = key, package = pkg });
        }

        [HttpPost("test-udp")]
        public async Task<IActionResult> TestUdpMessage([FromBody] UdpCommandMessage msg)
        {
            try
            {
                int port = _configService.CurrentConfig.UdpPort > 0 ? _configService.CurrentConfig.UdpPort : 8998;
                using var client = new System.Net.Sockets.UdpClient();
                client.Client.ReceiveTimeout = 2000;
                var jsonBytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(msg));
                await client.SendAsync(jsonBytes, jsonBytes.Length, "127.0.0.1", port);

                var receiveTask = client.ReceiveAsync();
                if (await Task.WhenAny(receiveTask, Task.Delay(1500)) == receiveTask)
                {
                    var res = receiveTask.Result;
                    var ackJson = System.Text.Encoding.UTF8.GetString(res.Buffer);
                    return Ok(new { success = true, ack = ackJson });
                }
                return Ok(new { success = true, ack = "已下发 UDP 报文" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
        }

        [HttpPost("system")]
        public IActionResult UpdateSystemSettings([FromBody] SystemSettingsRequest req)
        {
            var config = _configService.CurrentConfig;
            config.AutoLockOnFind = req.AutoLockOnFind;
            config.EnableSimulatorFallback = req.EnableSimulatorFallback;
            if (req.UdpPort > 0) config.UdpPort = req.UdpPort;
            config.EnableUdpService = req.EnableUdpService;
            _configService.SaveConfig();

            return Ok(new { success = true });
        }

        [HttpPost("seed-demo")]
        public IActionResult SeedDemoDevices([FromQuery] int count = 12)
        {
            _deviceManager.SeedDemoDevices(count);
            return Ok(new { success = true, count });
        }

        [HttpPost("clear-demo")]
        public IActionResult ClearDemoDevices()
        {
            _deviceManager.ClearDemoDevices();
            return Ok(new { success = true });
        }

        [HttpGet("pico-service-mode")]
        public IActionResult GetPicoServiceMode()
        {
            var status = PicoServiceModeHelper.GetStatus();
            return Ok(status);
        }

        [HttpPost("pico-service-mode")]
        public IActionResult SetPicoServiceMode([FromBody] SetServiceModeRequest req)
        {
            var result = PicoServiceModeHelper.SetServiceMode(req.Enable, req.RestartProcess);
            return Ok(result);
        }

        [HttpPost("pico-service-mode/restart")]
        public IActionResult RestartPicoServiceProcess()
        {
            var result = PicoServiceModeHelper.RestartProcess();
            return Ok(result);
        }
    }

    public class SetServiceModeRequest
    {
        public bool Enable { get; set; } = true;
        public bool RestartProcess { get; set; } = false;
    }

    public class SystemSettingsRequest
    {
        public bool AutoLockOnFind { get; set; } = true;
        public bool EnableSimulatorFallback { get; set; } = true;
        public int UdpPort { get; set; } = 8998;
        public bool EnableUdpService { get; set; } = true;
    }

    public class DeviceProjectRequest
    {
        public string DeviceKey { get; set; } = string.Empty;
        public string PackageOrProjectId { get; set; } = string.Empty;
    }

    public class BindHeadsetRequest
    {
        public string SN { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string? Model { get; set; }
    }
}

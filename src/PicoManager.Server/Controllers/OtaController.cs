using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OtaController : ControllerBase
    {
        private readonly OtaService _otaService;

        public OtaController(OtaService otaService)
        {
            _otaService = otaService;
        }

        [HttpGet("version")]
        public IActionResult GetVersion()
        {
            return Ok(_otaService.GetVersionInfo());
        }

        [HttpPost("upload")]
        [RequestSizeLimit(300_000_000)] // 支持最大 300MB 完整更新包
        public async Task<IActionResult> UploadUpdate(IFormFile? file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { success = false, message = "未选择任何有效的 OTA 更新文件 (.zip)" });
            }

            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { success = false, message = "OTA 升级包必须为 .zip 压缩文件格式" });
            }

            using var stream = file.OpenReadStream();
            var (ok, msg) = await _otaService.ApplyUpdateFromStreamAsync(stream, file.FileName);

            if (ok)
            {
                return Ok(new { success = true, message = msg });
            }

            return StatusCode(500, new { success = false, message = msg });
        }

        [HttpPost("download")]
        public async Task<IActionResult> DownloadUpdate([FromBody] OtaDownloadRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Url) || !Uri.TryCreate(req.Url, UriKind.Absolute, out _))
            {
                return BadRequest(new { success = false, message = "无效的远程 OTA 下载 URL 地址" });
            }

            var (ok, msg) = await _otaService.ApplyUpdateFromUrlAsync(req.Url.Trim());
            if (ok)
            {
                return Ok(new { success = true, message = msg });
            }

            return StatusCode(500, new { success = false, message = msg });
        }
    }

    public class OtaDownloadRequest
    {
        public string Url { get; set; } = string.Empty;
    }
}

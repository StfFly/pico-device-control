using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using PicoManager.Server.Services;

namespace PicoManager.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SystemController : ControllerBase
    {
        private readonly ConfigService _configService;

        public SystemController(ConfigService configService)
        {
            _configService = configService;
        }

        [HttpGet("network")]
        public IActionResult GetNetworkInfo()
        {
            int port = _configService.CurrentConfig.WebPort;
            var ipList = new List<LanIpInfo>();

            try
            {
                foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (netInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    var ipProps = netInterface.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        {
                            string ip = addr.Address.ToString();
                            // 排除 APIPA 地址 (169.254.x.x)
                            if (ip.StartsWith("169.254.")) continue;

                            bool isLan = ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.");
                            ipList.Add(new LanIpInfo
                            {
                                Ip = ip,
                                InterfaceName = netInterface.Name,
                                Description = netInterface.Description,
                                WebUrl = $"http://{ip}:{port}",
                                IsPreferred = isLan
                            });
                        }
                    }
                }
            }
            catch { }

            // 按局域网常用私网网段优先排序
            var sortedIps = ipList
                .OrderByDescending(x => x.IsPreferred)
                .ThenBy(x => x.Ip.StartsWith("198.18.") ? 1 : 0) // Clash tun 放后面
                .ToList();

            var primary = sortedIps.FirstOrDefault()?.WebUrl ?? $"http://127.0.0.1:{port}";

            return Ok(new
            {
                port,
                primaryUrl = primary,
                ips = sortedIps.Select(x => x.Ip).ToList(),
                interfaces = sortedIps
            });
        }
    }

    public class LanIpInfo
    {
        public string Ip { get; set; } = string.Empty;
        public string InterfaceName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string WebUrl { get; set; } = string.Empty;
        public bool IsPreferred { get; set; }
    }
}

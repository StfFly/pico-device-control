use serde::{Deserialize, Serialize};
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::time::Duration;
use tauri::command;
use tokio::net::TcpStream;

#[derive(Debug, Serialize, Deserialize)]
pub struct HostIpInfo {
    pub ip: String,
    pub web_url: String,
    pub is_default: bool,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct DiscoveredDevice {
    pub ip: String,
    pub port_adb_open: bool,
    pub is_responsive: bool,
}

#[command]
pub async fn get_host_network_info(port: Option<u16>) -> Result<Vec<HostIpInfo>, String> {
    let p = port.unwrap_or(8999);
    let mut result = Vec::new();

    let default_ip = local_ip_address::local_ip().ok();

    if let Ok(interfaces) = local_ip_address::list_afinet_netifas() {
        for (_name, ip) in interfaces {
            if let IpAddr::V4(ipv4) = ip {
                let octets = ipv4.octets();
                // 排除 127.0.0.1 环回与 169.254.x.x 无效链路本地地址
                if !ipv4.is_loopback() && !(octets[0] == 169 && octets[1] == 254) {
                    let ip_str = ipv4.to_string();
                    let is_lan = (octets[0] == 192 && octets[1] == 168)
                        || (octets[0] == 10)
                        || (octets[0] == 172 && (16..=31).contains(&octets[1]));
                    let is_proxy = octets[0] == 198 && octets[1] == 18;

                    let is_default = is_lan || (default_ip.map_or(false, |def| def == IpAddr::V4(ipv4)) && !is_proxy);

                    result.push(HostIpInfo {
                        ip: ip_str.clone(),
                        web_url: format!("http://{}:{}", ip_str, p),
                        is_default,
                    });
                }
            }
        }
    }

    // 常用门店局域网私网地址 (192.168.x.x / 10.x.x.x) 优先置顶，虚拟代理网卡置底
    result.sort_by(|a, b| {
        let a_score = if a.is_default { 0 } else if a.ip.starts_with("198.18.") { 2 } else { 1 };
        let b_score = if b.is_default { 0 } else if b.ip.starts_with("198.18.") { 2 } else { 1 };
        a_score.cmp(&b_score)
    });

    Ok(result)
}

#[command]
pub async fn send_udp_command(
    target_ip: String,
    port: u16,
    json_data: String,
) -> Result<bool, String> {
    let socket = UdpSocket::bind("0.0.0.0:0").map_err(|e| e.to_string())?;
    socket
        .set_broadcast(true)
        .map_err(|e| format!("设置广播失败: {}", e))?;

    let addr = format!("{}:{}", target_ip, port);
    socket
        .send_to(json_data.as_bytes(), &addr)
        .map_err(|e| format!("发送 UDP 失败: {}", e))?;

    Ok(true)
}

#[command]
pub async fn scan_lan_pico_devices(base_subnet: Option<String>) -> Result<Vec<DiscoveredDevice>, String> {
    let mut subnets = Vec::new();

    if let Some(s) = base_subnet {
        if !s.trim().is_empty() {
            subnets.push(s.trim().trim_end('.').to_string());
        }
    }

    if subnets.is_empty() {
        if let Ok(interfaces) = local_ip_address::list_afinet_netifas() {
            for (_name, ip) in interfaces {
                if let IpAddr::V4(ipv4) = ip {
                    let octets = ipv4.octets();
                    if !ipv4.is_loopback() && !(octets[0] == 169 && octets[1] == 254) && !(octets[0] == 198 && octets[1] == 18) {
                        let sub = format!("{}.{}.{}", octets[0], octets[1], octets[2]);
                        if !subnets.contains(&sub) {
                            subnets.push(sub);
                        }
                    }
                }
            }
        }
    }

    if subnets.is_empty() {
        subnets.push("192.168.1".to_string());
    }

    let mut tasks = Vec::new();
    for subnet in subnets {
        for i in 1..255 {
            let ip = format!("{}.{}", subnet, i);
            let task = tokio::spawn(async move {
                let addr = format!("{}:5555", ip);
                let socket_addr: Result<SocketAddr, _> = addr.parse();
                if let Ok(sa) = socket_addr {
                    let connect_timeout = Duration::from_millis(500);
                    if let Ok(Ok(_)) = tokio::time::timeout(connect_timeout, TcpStream::connect(sa)).await {
                        return Some(DiscoveredDevice {
                            ip,
                            port_adb_open: true,
                            is_responsive: true,
                        });
                    }
                }
                None
            });
            tasks.push(task);
        }
    }

    let mut discovered = Vec::new();
    for task in tasks {
        if let Ok(Some(dev)) = task.await {
            discovered.push(dev);
        }
    }

    Ok(discovered)
}

use crate::models::LanIpInfo;
use std::collections::HashSet;
use std::net::IpAddr;

/// 获取宿主机局域网 IPv4 列表（过滤 Loopback / APIPA / Clash TUN）
pub fn get_lan_ips(web_port: u16) -> Vec<LanIpInfo> {
    let mut list = Vec::new();

    if let Ok(ifaces) = if_addrs::get_if_addrs() {
        for iface in ifaces {
            if iface.is_loopback() {
                continue;
            }
            if let IpAddr::V4(v4) = iface.ip() {
                let octets = v4.octets();
                // 排除 169.254.x.x (APIPA) 和 198.18.x.x (Clash/TUN)
                if (octets[0] == 169 && octets[1] == 254) || (octets[0] == 198 && octets[1] == 18) {
                    continue;
                }

                let ip = v4.to_string();
                let is_lan = octets[0] == 192 && octets[1] == 168
                    || octets[0] == 10
                    || (octets[0] == 172 && (16..=31).contains(&octets[1]));

                list.push(LanIpInfo {
                    web_url: format!("http://{}:{}", ip, web_port),
                    ip,
                    interface_name: iface.name.clone(),
                    description: iface.name,
                    is_preferred: is_lan,
                });
            }
        }
    }

    list.sort_by(|a, b| b.is_preferred.cmp(&a.is_preferred).then(a.ip.cmp(&b.ip)));
    list
}

/// 自动探测活跃局域网 /24 子网前缀（如 "192.168.1"）
pub fn detect_lan_subnets() -> Vec<String> {
    let mut set = HashSet::new();
    for info in get_lan_ips(8999) {
        let parts: Vec<&str> = info.ip.split('.').collect();
        if parts.len() == 4 {
            set.insert(format!("{}.{}.{}", parts[0], parts[1], parts[2]));
        }
    }
    if set.is_empty() {
        set.insert("192.168.1".to_string());
    }
    let mut subnets: Vec<String> = set.into_iter().collect();
    subnets.sort();
    subnets
}

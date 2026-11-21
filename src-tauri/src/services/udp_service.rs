use crate::models::WsEvent;
use crate::services::config_service::ConfigService;
use crate::services::device_manager::PicoDeviceManager;
use chrono::Local;
use serde_json::Value;
use std::collections::HashSet;
use std::sync::Arc;
use tokio::net::UdpSocket;
use tokio::sync::broadcast;
use tokio::time::{sleep, Duration};

pub struct UdpCommandService {
    config_service: Arc<ConfigService>,
    device_manager: Arc<PicoDeviceManager>,
    ws_tx: broadcast::Sender<WsEvent>,
}

impl UdpCommandService {
    pub fn new(
        config_service: Arc<ConfigService>,
        device_manager: Arc<PicoDeviceManager>,
        ws_tx: broadcast::Sender<WsEvent>,
    ) -> Arc<Self> {
        Arc::new(Self {
            config_service,
            device_manager,
            ws_tx,
        })
    }

    pub fn start(self: &Arc<Self>) {
        let cfg = self.config_service.get_config();
        if !cfg.enable_udp_service {
            tracing::info!("[UDP] 配置已禁用 UDP 播控监听");
            return;
        }
        let port = if cfg.udp_port > 0 { cfg.udp_port } else { 8998 };
        let svc = self.clone();

        tokio::spawn(async move {
            let bind_addr = format!("0.0.0.0:{}", port);
            let socket = match UdpSocket::bind(&bind_addr).await {
                Ok(s) => Arc::new(s),
                Err(err) => {
                    tracing::error!("[UDP] 绑定 {} 失败: {}", bind_addr, err);
                    return;
                }
            };
            tracing::info!("[UDP] 外部中控指令服务已启动，监听 {}", bind_addr);

            let mut buf = vec![0u8; 65536];
            loop {
                match socket.recv_from(&mut buf).await {
                    Ok((len, peer)) => {
                        let payload = String::from_utf8_lossy(&buf[..len]).to_string();
                        let svc_c = svc.clone();
                        let sock_c = socket.clone();
                        tokio::spawn(async move {
                            let ack = svc_c.process_message(&payload).await;
                            let _ = sock_c.send_to(ack.as_bytes(), peer).await;
                        });
                    }
                    Err(err) => {
                        tracing::warn!("[UDP] 接收数据包异常: {}", err);
                        sleep(Duration::from_millis(200)).await;
                    }
                }
            }
        });
    }

    pub async fn process_message(&self, json_text: &str) -> String {
        let root: Value = match serde_json::from_str(json_text) {
            Ok(v) => v,
            Err(e) => return create_ack(false, "", "", &format!("报文 JSON 解析异常: {}", e), 0, ""),
        };

        let dev_list = extract_device_list(&root);
        let device_desc = if dev_list.is_empty() {
            "all".to_string()
        } else {
            dev_list.join(",")
        };

        let action = extract_string(&root, &["action", "command", "cmd", "type"]).to_ascii_lowercase();
        let pkg_name = extract_string(&root, &["packageName", "package_name", "package", "app", "appName"]);
        let extra_json = extract_string(&root, &["extraJson", "extra", "params"]);

        if action.is_empty() {
            return create_ack(false, &device_desc, &action, "缺少 action 动作字段 (如 start / stop)", 0, "");
        }

        let mut targets = Vec::new();
        if dev_list.is_empty() || dev_list.iter().any(|d| d.eq_ignore_ascii_case("all")) {
            targets = self.device_manager.resolve_devices(Some("all"));
        } else {
            let mut seen = HashSet::new();
            for d_no in &dev_list {
                for m in self.device_manager.resolve_devices(Some(d_no)) {
                    if seen.insert(m.sn.clone()) {
                        targets.push(m);
                    }
                }
            }
        }

        if targets.is_empty() {
            let msg = format!("未找到匹配设备: {}", device_desc);
            self.notify_frontend(&device_desc, "none", &action, &pkg_name, false, &msg);
            return create_ack(false, &device_desc, &action, &msg, 0, "");
        }

        let mut success_count = 0usize;
        let mut last_pkg = String::new();

        match action.as_str() {
            "start" | "play" | "launch" | "open" | "1" => {
                for dev in &targets {
                    let final_pkg = self
                        .device_manager
                        .resolve_package_name(Some(&dev.sn), if pkg_name.is_empty() { None } else { Some(&pkg_name) });
                    last_pkg = final_pkg.clone();
                    self.device_manager.start_app(&dev.sn, &final_pkg, &extra_json);
                    success_count += 1;
                    let updated = self.device_manager.get_device(&dev.sn);
                    let app_title = updated.map(|d| d.app_title).unwrap_or_else(|| final_pkg.clone());
                    self.notify_frontend(
                        &dev.display_name,
                        &dev.sn,
                        "start",
                        &final_pkg,
                        true,
                        &format!("已通过 UDP 启动【{}】游戏：{}", dev.display_name, app_title),
                    );
                    if targets.len() > 1 {
                        sleep(Duration::from_millis(30)).await;
                    }
                }
            }
            "stop" | "close" | "exit" | "quit" | "0" => {
                for dev in &targets {
                    self.device_manager.stop_app(&dev.sn, "");
                    success_count += 1;
                    self.notify_frontend(
                        &dev.display_name,
                        &dev.sn,
                        "stop",
                        "系统桌面",
                        true,
                        &format!("已通过 UDP 退出【{}】回到系统桌面", dev.display_name),
                    );
                    if targets.len() > 1 {
                        sleep(Duration::from_millis(30)).await;
                    }
                }
            }
            "pause" => {
                for dev in &targets {
                    self.device_manager.pause_video(&dev.sn);
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "pause", "", true, "已通过 UDP 下发暂停");
            }
            "resume" | "continue" => {
                for dev in &targets {
                    self.device_manager.continue_video(&dev.sn);
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "resume", "", true, "已通过 UDP 下发继续播放");
            }
            "recenter" => {
                for dev in &targets {
                    self.device_manager.recenter(&dev.sn);
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "recenter", "", true, "已通过 UDP 下发视角正中复位");
            }
            "reboot" => {
                for dev in &targets {
                    self.device_manager.reboot(&dev.sn);
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "reboot", "", true, "已通过 UDP 下发重启指令");
            }
            "screen_on" | "screenon" | "light_on" => {
                for dev in &targets {
                    self.device_manager.set_screen_state(&dev.sn, true).await;
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "screen_on", "", true, "已通过 UDP 下发点亮屏幕");
            }
            "screen_off" | "screenoff" | "light_off" | "sleep" => {
                for dev in &targets {
                    self.device_manager.set_screen_state(&dev.sn, false).await;
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "screen_off", "", true, "已通过 UDP 下发熄灭屏幕");
            }
            "shutdown" => {
                for dev in &targets {
                    self.device_manager.shutdown(&dev.sn);
                    success_count += 1;
                }
                self.notify_frontend(&device_desc, "batch", "shutdown", "", true, "已通过 UDP 下发关机指令");
            }
            _ => {
                return create_ack(false, &device_desc, &action, &format!("不支持的动作: {}", action), 0, "");
            }
        }

        create_ack(
            true,
            &device_desc,
            &action,
            &format!("成功调度 {} 台设备执行指令", success_count),
            success_count,
            &last_pkg,
        )
    }

    fn notify_frontend(
        &self,
        device_no: &str,
        sn: &str,
        action: &str,
        package_name: &str,
        success: bool,
        message: &str,
    ) {
        let _ = self.ws_tx.send(WsEvent {
            event_type: "UdpCommandReceived".to_string(),
            data: serde_json::json!({
                "deviceNo": device_no,
                "sn": sn,
                "action": action,
                "packageName": package_name,
                "success": success,
                "message": message,
                "timestamp": Local::now().format("%H:%M:%S").to_string(),
            }),
        });
    }
}

fn extract_device_list(root: &Value) -> Vec<String> {
    let mut list = Vec::new();
    let Some(obj) = root.as_object() else {
        return list;
    };
    const KEYS: &[&str] = &[
        "devices", "deviceList", "device_list", "deviceNo", "device_no", "machineNo", "alias", "sn", "target", "targets",
    ];

    for (k, v) in obj {
        if KEYS.iter().any(|cand| cand.eq_ignore_ascii_case(k)) {
            match v {
                Value::Array(arr) => {
                    for item in arr {
                        let s = match item {
                            Value::String(st) => st.trim().to_string(),
                            other => other.to_string().trim().to_string(),
                        };
                        if !s.is_empty() {
                            list.push(s);
                        }
                    }
                    if !list.is_empty() {
                        return list;
                    }
                }
                Value::String(st) => {
                    for p in st.split(|c| matches!(c, ',' | ';' | '，' | '；')) {
                        let trimmed = p.trim();
                        if !trimmed.is_empty() {
                            list.push(trimmed.to_string());
                        }
                    }
                    if !list.is_empty() {
                        return list;
                    }
                }
                Value::Number(n) => {
                    list.push(n.to_string());
                    return list;
                }
                _ => {}
            }
        }
    }
    list
}

fn extract_string(root: &Value, keys: &[&str]) -> String {
    let Some(obj) = root.as_object() else {
        return String::new();
    };
    for (k, v) in obj {
        if keys.iter().any(|cand| cand.eq_ignore_ascii_case(k)) {
            match v {
                Value::String(s) => return s.trim().to_string(),
                Value::Number(n) => return n.to_string(),
                Value::Bool(b) => return b.to_string(),
                Value::Object(_) | Value::Array(_) => return v.to_string(),
                _ => {}
            }
        }
    }
    String::new()
}

fn create_ack(
    success: bool,
    device_no: &str,
    action: &str,
    message: &str,
    matched_count: usize,
    package_name: &str,
) -> String {
    serde_json::json!({
        "success": success,
        "deviceNo": device_no,
        "action": action,
        "packageName": package_name,
        "matchedCount": matched_count,
        "message": message,
        "time": Local::now().format("%Y-%m-%d %H:%M:%S").to_string(),
    })
    .to_string()
}

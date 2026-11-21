use serde::{Deserialize, Serialize};
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;
use tauri::command;

static SERVER_RUNNING: AtomicBool = AtomicBool::new(false);

#[derive(Debug, Serialize, Deserialize)]
pub struct ServerStatus {
    pub is_running: bool,
    pub port: u16,
    pub api_url: String,
    pub message: String,
}

#[command]
pub async fn check_server_status(port: Option<u16>) -> Result<ServerStatus, String> {
    let p = port.unwrap_or(8999);
    let url = format!("http://127.0.0.1:{}/api/devices", p);

    let client = reqwest::Client::builder()
        .timeout(Duration::from_millis(1500))
        .build()
        .map_err(|e| e.to_string())?;

    match client.get(&url).send().await {
        Ok(res) if res.status().is_success() => {
            SERVER_RUNNING.store(true, Ordering::SeqCst);
            Ok(ServerStatus {
                is_running: true,
                port: p,
                api_url: format!("http://localhost:{}", p),
                message: "PICO 控制后台服务正在运行中".into(),
            })
        }
        _ => {
            SERVER_RUNNING.store(false, Ordering::SeqCst);
            Ok(ServerStatus {
                is_running: false,
                port: p,
                api_url: format!("http://localhost:{}", p),
                message: "后台服务未启动或正在初始化...".into(),
            })
        }
    }
}

#[command]
pub async fn start_server_process(port: Option<u16>) -> Result<ServerStatus, String> {
    let p = port.unwrap_or(8999);
    
    // 检查是否已经在运行
    if let Ok(status) = check_server_status(Some(p)).await {
        if status.is_running {
            return Ok(status);
        }
    }

    // 查找已发布的 exe (优先使用 PicoVR_Server_Deploy 下的独立部署服务端)
    let current_dir = std::env::current_dir().unwrap_or_default();
    let current_exe = std::env::current_exe().unwrap_or_default();
    let exe_dir = current_exe.parent().unwrap_or(&current_dir);

    let candidates = [
        // 0. 项目绝对物理路径直达
        std::path::PathBuf::from(r"F:\Sun\AIProgram\Pico设备控制\PicoVR_Server_Deploy\server\PicoManager.Server.exe"),
        // 1. 优先唤起 PicoVR_Server_Deploy 下的完整部署服务 (包含真实门店配置与 PICO SDK DLL)
        current_dir.join("PicoVR_Server_Deploy").join("server").join("PicoManager.Server.exe"),
        exe_dir.join("..").join("..").join("..").join("PicoVR_Server_Deploy").join("server").join("PicoManager.Server.exe"),
        exe_dir.join("..").join("PicoVR_Server_Deploy").join("server").join("PicoManager.Server.exe"),
        exe_dir.join("PicoVR_Server_Deploy").join("server").join("PicoManager.Server.exe"),
        exe_dir.join("server").join("PicoManager.Server.exe"),
        exe_dir.join("PicoManager.Server.exe"),
        // 2. 完整部署包
        current_dir.join("PicoManager_v1.0_完整部署包").join("server").join("PicoManager.Server.exe"),
        // 3. 开发编译输出目录
        current_dir.join("src").join("PicoManager.Server").join("bin").join("Debug").join("net10.0-windows").join("PicoManager.Server.exe"),
        exe_dir.join("..").join("..").join("..").join("src").join("PicoManager.Server").join("bin").join("Debug").join("net10.0-windows").join("PicoManager.Server.exe"),
        current_dir.join("src").join("PicoManager.Server").join("publish").join("PicoManager.Server.exe"),
    ];

    let mut launched = false;
    for exe in &candidates {
        if exe.exists() {
            let working_dir = exe.parent().unwrap_or(&current_dir);
            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                const CREATE_NO_WINDOW: u32 = 0x08000000;
                let _ = Command::new(exe)
                    .current_dir(working_dir)
                    .creation_flags(CREATE_NO_WINDOW)
                    .spawn();
                launched = true;
                break;
            }
        }
    }

    if !launched {
        // 回退至 dotnet run
        let project_path = current_dir.join("src").join("PicoManager.Server").join("PicoManager.Server.csproj");
        if project_path.exists() {
            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                const CREATE_NO_WINDOW: u32 = 0x08000000;
                let _ = Command::new("dotnet")
                    .args(["run", "--project", project_path.to_str().unwrap_or("")])
                    .current_dir(project_path.parent().unwrap_or(&current_dir))
                    .creation_flags(CREATE_NO_WINDOW)
                    .spawn();
            }
        }
    }

    // 循环探测等待服务启动完成 (最多等待 15 秒)
    for _ in 0..30 {
        tokio::time::sleep(Duration::from_millis(500)).await;
        if let Ok(status) = check_server_status(Some(p)).await {
            if status.is_running {
                return Ok(status);
            }
        }
    }
    check_server_status(Some(p)).await
}

#[command]
pub async fn stop_server_process() -> Result<bool, String> {
    #[cfg(windows)]
    {
        let _ = Command::new("taskkill")
            .args(["/F", "/IM", "PicoManager.Server.exe"])
            .output();
    }
    SERVER_RUNNING.store(false, Ordering::SeqCst);
    Ok(true)
}

#[command]
pub async fn api_proxy(
    method: String,
    path: String,
    body: Option<serde_json::Value>,
) -> Result<serde_json::Value, String> {
    let clean_path = if path.starts_with('/') { path } else { format!("/{}", path) };
    let url = format!("http://127.0.0.1:8999{}", clean_path);

    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(12))
        .build()
        .map_err(|e| format!("Reqwest init error: {}", e))?;

    let req = match method.to_uppercase().as_str() {
        "POST" => {
            let mut r = client.post(&url);
            if let Some(b) = body {
                r = r.json(&b);
            } else {
                r = r.header("Content-Type", "application/json").body("{}");
            }
            r
        }
        "DELETE" => client.delete(&url),
        "PUT" => {
            let mut r = client.put(&url);
            if let Some(b) = body {
                r = r.json(&b);
            } else {
                r = r.header("Content-Type", "application/json").body("{}");
            }
            r
        }
        _ => client.get(&url),
    };

    let res = req.send().await.map_err(|e| format!("网络请求失败 [{}]: {}", url, e))?;
    let status = res.status();
    let text = res.text().await.unwrap_or_default();

    if status.is_success() {
        if text.trim().is_empty() {
            return Ok(serde_json::json!({ "success": true }));
        }
        match serde_json::from_str::<serde_json::Value>(&text) {
            Ok(val) => Ok(val),
            Err(_) => Ok(serde_json::json!({ "raw": text })),
        }
    } else {
        Err(format!("HTTP {}: {}", status, text))
    }
}

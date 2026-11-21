use serde::{Deserialize, Serialize};
use std::process::Command;
use tauri::command;

#[derive(Debug, Serialize, Deserialize)]
pub struct AdbResult {
    pub success: bool,
    pub output: String,
    pub target: String,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct AdbDeviceInfo {
    pub serial: String,
    pub status: String,
    pub is_wireless: bool,
}

fn run_adb(args: &[&str]) -> (bool, String) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x08000000;
        match Command::new("adb")
            .args(args)
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        {
            Ok(output) => {
                let stdout = String::from_utf8_lossy(&output.stdout).to_string();
                let stderr = String::from_utf8_lossy(&output.stderr).to_string();
                let combined = if stderr.trim().is_empty() {
                    stdout
                } else {
                    format!("{}\n{}", stdout, stderr)
                };
                (output.status.success(), combined)
            }
            Err(e) => (false, format!("执行 ADB 失败: {}", e)),
        }
    }

    #[cfg(not(windows))]
    {
        match Command::new("adb").args(args).output() {
            Ok(output) => {
                let stdout = String::from_utf8_lossy(&output.stdout).to_string();
                let stderr = String::from_utf8_lossy(&output.stderr).to_string();
                (output.status.success(), format!("{}\n{}", stdout, stderr))
            }
            Err(e) => (false, format!("执行 ADB 失败: {}", e)),
        }
    }
}

#[command]
pub async fn adb_connect(ip: String, port: Option<u16>) -> Result<AdbResult, String> {
    let p = port.unwrap_or(5555);
    let target = format!("{}:{}", ip.trim(), p);
    let (success, output) = run_adb(&["connect", &target]);
    Ok(AdbResult {
        success: success && (output.contains("connected to") || output.contains("already connected to")),
        output,
        target,
    })
}

#[command]
pub async fn adb_disconnect(ip: String, port: Option<u16>) -> Result<AdbResult, String> {
    let p = port.unwrap_or(5555);
    let target = format!("{}:{}", ip.trim(), p);
    let (success, output) = run_adb(&["disconnect", &target]);
    Ok(AdbResult {
        success,
        output,
        target,
    })
}

#[command]
pub async fn adb_list_devices() -> Result<Vec<AdbDeviceInfo>, String> {
    let (_, output) = run_adb(&["devices"]);
    let mut devices = Vec::new();
    for line in output.lines().skip(1) {
        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.len() >= 2 {
            let serial = parts[0].to_string();
            let status = parts[1].to_string();
            let is_wireless = serial.contains(':');
            devices.push(AdbDeviceInfo {
                serial,
                status,
                is_wireless,
            });
        }
    }
    Ok(devices)
}

#[command]
pub async fn adb_install_apk(target: String, apk_path: String) -> Result<AdbResult, String> {
    let (success, output) = run_adb(&["-s", &target, "install", "-r", "-d", &apk_path]);
    Ok(AdbResult {
        success: success && output.contains("Success"),
        output,
        target,
    })
}

#[command]
pub async fn adb_push_file(
    target: String,
    local_path: String,
    remote_path: String,
) -> Result<AdbResult, String> {
    let (success, output) = run_adb(&["-s", &target, "push", &local_path, &remote_path]);
    Ok(AdbResult {
        success,
        output,
        target,
    })
}

#[command]
pub async fn adb_reboot_device(target: String) -> Result<AdbResult, String> {
    let (success, output) = run_adb(&["-s", &target, "reboot"]);
    Ok(AdbResult {
        success,
        output,
        target,
    })
}

#[command]
pub async fn adb_shell(target: String, command: String) -> Result<AdbResult, String> {
    let (success, output) = run_adb(&["-s", &target, "shell", &command]);
    Ok(AdbResult {
        success,
        output,
        target,
    })
}

use serde_json::Value;
use std::fs;
use std::path::PathBuf;
use tauri::command;

fn get_config_path() -> PathBuf {
    let current_dir = std::env::current_dir().unwrap_or_default();
    let candidates = [
        current_dir.join("src").join("PicoManager.Server").join("config.json"),
        current_dir.join("config.json"),
        current_dir.join("PicoManager_v1.0_完整部署包").join("config.json"),
    ];

    for path in &candidates {
        if path.exists() {
            return path.clone();
        }
    }

    candidates[0].clone()
}

#[command]
pub async fn load_config_json() -> Result<Value, String> {
    let path = get_config_path();
    if !path.exists() {
        return Err(format!("配置文件未找到: {:?}", path));
    }

    let content = fs::read_to_string(&path).map_err(|e| e.to_string())?;
    let json: Value = serde_json::from_str(&content).map_err(|e| e.to_string())?;
    Ok(json)
}

#[command]
pub async fn save_config_json(content: Value) -> Result<bool, String> {
    let path = get_config_path();
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }

    // 自动创建备份
    if path.exists() {
        let backup_path = path.with_extension("backup.json");
        let _ = fs::copy(&path, &backup_path);
    }

    let formatted = serde_json::to_string_pretty(&content).map_err(|e| e.to_string())?;
    fs::write(&path, formatted).map_err(|e| e.to_string())?;
    Ok(true)
}

#[command]
pub async fn check_pico_business_suite_service_mode() -> Result<Value, String> {
    #[cfg(windows)]
    {
        use std::process::Command;
        let mut is_process_running = false;
        if let Ok(output) = Command::new("tasklist")
            .args(["/FI", "IMAGENAME eq PICOBusinessSuite.exe"])
            .output()
        {
            let text = String::from_utf8_lossy(&output.stdout);
            if text.contains("PICOBusinessSuite.exe") {
                is_process_running = true;
            }
        }

        // 检测 key-setting.ini
        let user_profile = std::env::var("USERPROFILE").unwrap_or_default();
        let roaming = std::env::var("APPDATA").unwrap_or_default();
        let possible_ini_paths = [
            PathBuf::from(&roaming).join("PICO").join("PICOBusinessSuite").join("key-setting.ini"),
            PathBuf::from(&user_profile).join("AppData").join("Roaming").join("PICO").join("key-setting.ini"),
        ];

        let mut service_mode_enabled = false;
        let mut found_ini = false;
        for ini in &possible_ini_paths {
            if ini.exists() {
                found_ini = true;
                if let Ok(content) = fs::read_to_string(ini) {
                    if content.contains("ServiceMode=1") || content.contains("ServiceMode = 1") {
                        service_mode_enabled = true;
                        break;
                    }
                }
            }
        }

        Ok(serde_json::json!({
            "is_process_running": is_process_running,
            "found_ini": found_ini,
            "service_mode_enabled": service_mode_enabled,
        }))
    }

    #[cfg(not(windows))]
    {
        Ok(serde_json::json!({
            "is_process_running": false,
            "found_ini": false,
            "service_mode_enabled": false,
        }))
    }
}

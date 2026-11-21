use crate::models::{PicoServiceResult, PicoServiceStatus};
use std::fs;
use std::path::PathBuf;
use std::process::Command;
use sysinfo::{ProcessesToUpdate, System};

pub fn resolve_ini_path() -> PathBuf {
    let local_app = dirs::data_local_dir().unwrap_or_else(|| PathBuf::from(r"C:\Users\Default\AppData\Local"));

    let p1 = local_app.join("PICOBusinessSuitData").join("key-setting.ini");
    if p1.exists() {
        return p1;
    }

    let p2 = local_app.join("PICOBusinessSuitData").join("setting.ini");
    if p2.exists() {
        return p2;
    }

    if let Ok(entries) = fs::read_dir(r"C:\Users") {
        for entry in entries.flatten() {
            let base = entry.path().join("AppData").join("Local").join("PICOBusinessSuitData");
            let c1 = base.join("key-setting.ini");
            if c1.exists() {
                return c1;
            }
            let c2 = base.join("setting.ini");
            if c2.exists() {
                return c2;
            }
        }
    }

    for install in [
        r"D:\Program Files (x86)\PICO Business Suite\setting.ini",
        r"C:\Program Files (x86)\PICO Business Suite\setting.ini",
        r"C:\Program Files\PICO Business Suite\setting.ini",
    ] {
        let p = PathBuf::from(install);
        if p.exists() {
            return p;
        }
    }

    p1
}

pub fn find_pico_suite_process() -> Option<(u32, String)> {
    let mut sys = System::new();
    sys.refresh_processes(ProcessesToUpdate::All, true);
    for (pid, proc_info) in sys.processes() {
        let name = proc_info.name().to_string_lossy().to_lowercase();
        if name == "picobusinesssuite.exe" || name == "picobusinesssuite" {
            let exe = proc_info
                .exe()
                .map(|p| p.to_string_lossy().to_string())
                .unwrap_or_default();
            return Some((pid.as_u32(), exe));
        }
    }
    None
}

pub fn get_status() -> PicoServiceStatus {
    let ini_path = resolve_ini_path();
    let found_ini = ini_path.exists();
    let mut run_as_service = false;
    let mut message = String::new();

    if found_ini {
        match fs::read_to_string(&ini_path) {
            Ok(content) => {
                let mut in_service_section = false;
                for line in content.lines() {
                    let trimmed = line.trim();
                    if trimmed.starts_with('[') && trimmed.ends_with(']') {
                        in_service_section = trimmed.eq_ignore_ascii_case("[Service]");
                        continue;
                    }
                    if in_service_section
                        && trimmed.to_ascii_lowercase().starts_with("runasservice")
                    {
                        if let Some((_, val)) = trimmed.split_once('=') {
                            let v = val.trim();
                            run_as_service = v.eq_ignore_ascii_case("true") || v == "1";
                        }
                    }
                }
            }
            Err(err) => {
                message = format!("读取配置文件异常: {}", err);
            }
        }
    }

    let (is_process_running, process_id, process_path) = match find_pico_suite_process() {
        Some((pid, path)) => (true, pid, path),
        None => (false, 0, String::new()),
    };

    PicoServiceStatus {
        found_ini,
        ini_path: ini_path.to_string_lossy().to_string(),
        run_as_service,
        is_process_running,
        process_id,
        process_path,
        message,
    }
}

pub fn set_service_mode(enable: bool, restart: bool) -> PicoServiceResult {
    let ini_path = resolve_ini_path();
    if let Some(parent) = ini_path.parent() {
        if let Err(e) = fs::create_dir_all(parent) {
            return PicoServiceResult {
                success: false,
                message: format!("创建目录失败: {}", e),
                status: get_status(),
            };
        }
    }

    if ini_path.exists() {
        let bak = PathBuf::from(format!("{}.bak", ini_path.to_string_lossy()));
        let _ = fs::copy(&ini_path, &bak);
    }

    let existing = fs::read_to_string(&ini_path).unwrap_or_default();
    let val_str = if enable { "true" } else { "false" };

    let mut new_lines: Vec<String> = Vec::new();
    let mut in_service_section = false;
    let mut section_found = false;
    let mut key_found = false;

    for line in existing.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with('[') && trimmed.ends_with(']') {
            if in_service_section && !key_found {
                new_lines.push(format!("runAsService={}", val_str));
                key_found = true;
            }
            in_service_section = trimmed.eq_ignore_ascii_case("[Service]");
            if in_service_section {
                section_found = true;
            }
            new_lines.push(line.to_string());
            continue;
        }

        if in_service_section && trimmed.to_ascii_lowercase().starts_with("runasservice") {
            new_lines.push(format!("runAsService={}", val_str));
            key_found = true;
            continue;
        }

        new_lines.push(line.to_string());
    }

    if in_service_section && !key_found {
        new_lines.push(format!("runAsService={}", val_str));
    }

    if !section_found {
        if !new_lines.is_empty() && !new_lines.last().map(|s| s.trim().is_empty()).unwrap_or(true) {
            new_lines.push(String::new());
        }
        new_lines.push("[Service]".to_string());
        new_lines.push(format!("runAsService={}", val_str));
    }

    let output = new_lines.join("\r\n");
    if let Err(e) = fs::write(&ini_path, output.as_bytes()) {
        return PicoServiceResult {
            success: false,
            message: format!("写入配置失败: {}", e),
            status: get_status(),
        };
    }

    if restart {
        let _ = restart_process_internal();
    }

    let msg = if enable {
        format!(
            "已成功开启服务模式 (runAsService=true)，配置写入: {}",
            ini_path.display()
        )
    } else {
        format!(
            "已成功关闭服务模式 (runAsService=false)，配置写入: {}",
            ini_path.display()
        )
    };

    PicoServiceResult {
        success: true,
        message: msg,
        status: get_status(),
    }
}

pub fn restart_process() -> PicoServiceResult {
    let ok = restart_process_internal();
    PicoServiceResult {
        success: ok,
        message: if ok {
            "PICO 企业服务客户端已成功重启！".to_string()
        } else {
            "未能重新启动客户端，请手动打开 PICOBusinessSuite.exe".to_string()
        },
        status: get_status(),
    }
}

fn restart_process_internal() -> bool {
    let mut exe_path: Option<String> = None;

    if let Some((_pid, path)) = find_pico_suite_process() {
        if !path.is_empty() {
            exe_path = Some(path);
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x08000000;
            let _ = Command::new("taskkill")
                .args(["/F", "/IM", "PICOBusinessSuite.exe"])
                .creation_flags(CREATE_NO_WINDOW)
                .output();
        }
    }

    if exe_path.is_none() {
        for cand in [
            r"D:\Program Files (x86)\PICO Business Suite\PICOBusinessSuite.exe",
            r"C:\Program Files (x86)\PICO Business Suite\PICOBusinessSuite.exe",
            r"C:\Program Files\PICO Business Suite\PICOBusinessSuite.exe",
        ] {
            if PathBuf::from(cand).exists() {
                exe_path = Some(cand.to_string());
                break;
            }
        }
    }

    if let Some(path) = exe_path {
        let p = PathBuf::from(&path);
        if p.exists() {
            std::thread::sleep(std::time::Duration::from_millis(800));
            let mut cmd = Command::new(&p);
            if let Some(dir) = p.parent() {
                cmd.current_dir(dir);
            }
            return cmd.spawn().is_ok();
        }
    }

    false
}

pub mod commands;

use commands::adb::*;
use commands::config::*;
use commands::network::*;
use commands::server::*;
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Emitter, Manager,
};

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            // 构建系统托盘菜单
            let show_i = MenuItem::with_id(app, "show", "打开管理工作台", true, None::<&str>)?;
            let restart_srv_i = MenuItem::with_id(app, "restart_server", "重启后台服务", true, None::<&str>)?;
            let quit_i = MenuItem::with_id(app, "quit", "退出 PicoManager", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show_i, &restart_srv_i, &quit_i])?;

            let _tray = TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .menu(&menu)
                .show_menu_on_left_click(false)
                .tooltip("PICO VR 设备管理控制中心")
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "show" => {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                    }
                    "restart_server" => {
                        let _ = app.emit("tray-restart-server", ());
                        tauri::async_runtime::spawn(async {
                            let _ = stop_server_process().await;
                            tokio::time::sleep(std::time::Duration::from_millis(800)).await;
                            let _ = start_server_process(Some(8999)).await;
                        });
                    }
                    "quit" => {
                        tauri::async_runtime::spawn(async {
                            let _ = stop_server_process().await;
                            std::process::exit(0);
                        });
                    }
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        let app = tray.app_handle();
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                    }
                })
                .build(app)?;

            // 自动唤起 PicoVR_Server_Deploy 下的核心后台服务 (若未启动)
            tauri::async_runtime::spawn(async {
                let _ = start_server_process(Some(8999)).await;
            });

            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            check_server_status,
            start_server_process,
            stop_server_process,
            api_proxy,
            adb_connect,
            adb_disconnect,
            adb_list_devices,
            adb_install_apk,
            adb_push_file,
            adb_reboot_device,
            adb_shell,
            get_host_network_info,
            send_udp_command,
            scan_lan_pico_devices,
            load_config_json,
            save_config_json,
            check_pico_business_suite_service_mode,
        ])
        .run(tauri::generate_context!())
        .expect("运行 Tauri 应用程序时发生错误");
}

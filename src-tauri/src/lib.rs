pub mod commands;
pub mod models;
pub mod server;
pub mod services;
pub mod state;

use commands::{get_runtime_info, reconnect_sdk};
use state::AppState;
use std::sync::Arc;
use tauri::{
    menu::{Menu, MenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    Manager, RunEvent, WindowEvent,
};

pub fn run() {
    let _ = tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "pico_manager_desktop_lib=info,warn".into()),
        )
        .try_init();

    let app_state: Arc<AppState> = AppState::initialize();
    let state_for_setup = app_state.clone();
    let state_for_exit = app_state.clone();

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .manage(app_state)
        .setup(move |app| {
            // 在 Tauri 内置异步运行时中拉起一体化后台服务：
            // 1. 内嵌 Axum HTTP + WebSocket 服务（默认 0.0.0.0:8999，供本桌面端与局域网平板访问）
            // 2. UDP 外部中控指令监听服务（默认 0.0.0.0:8998）
            // 3. PICO Enterprise Suite 原生 DLL FFI 连接与硬件遥测保活
            let state_bg = state_for_setup.clone();
            tauri::async_runtime::spawn(async move {
                server::spawn_embedded_server(state_bg.clone());
                state_bg.udp_service.start();
                state_bg.sdk_service.start();
            });

            // 构建系统托盘菜单
            let show_i = MenuItem::with_id(app, "show", "打开控制中心", true, None::<&str>)?;
            let reconnect_i =
                MenuItem::with_id(app, "reconnect_sdk", "重连 PICO 官方套件", true, None::<&str>)?;
            let quit_i = MenuItem::with_id(app, "quit", "彻底退出 PicoManager", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show_i, &reconnect_i, &quit_i])?;

            let state_tray = state_for_setup.clone();
            let _tray = TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .menu(&menu)
                .show_menu_on_left_click(false)
                .tooltip("PICO VR 一体化设备管理控制中心")
                .on_menu_event(move |app, event| match event.id.as_ref() {
                    "show" => {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.unminimize();
                            let _ = window.set_focus();
                        }
                    }
                    "reconnect_sdk" => {
                        let st = state_tray.clone();
                        tauri::async_runtime::spawn(async move {
                            let _ = st.sdk_service.reconnect_sdk().await;
                        });
                    }
                    "quit" => {
                        state_tray.sdk_service.shutdown();
                        app.exit(0);
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
                            let _ = window.unminimize();
                            let _ = window.set_focus();
                        }
                    }
                })
                .build(app)?;

            Ok(())
        })
        .on_window_event(|window, event| {
            // 关闭主窗口时最小化至系统托盘保持后台播控与 UDP/局域网服务在线
            if let WindowEvent::CloseRequested { api, .. } = event {
                let _ = window.hide();
                api.prevent_close();
            }
        })
        .invoke_handler(tauri::generate_handler![get_runtime_info, reconnect_sdk])
        .build(tauri::generate_context!())
        .expect("构建 PicoManager Tauri 应用失败");

    app.run(move |_app_handle, event| {
        if let RunEvent::Exit = event {
            state_for_exit.sdk_service.shutdown();
        }
    });
}

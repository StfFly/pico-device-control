use crate::models::RuntimeInfo;
use crate::services::network_service;
use crate::state::AppState;
use std::sync::Arc;
use tauri::{command, State};

#[command]
pub async fn get_runtime_info(state: State<'_, Arc<AppState>>) -> Result<RuntimeInfo, String> {
    let lan_urls = network_service::get_lan_ips(state.web_port)
        .into_iter()
        .map(|i| i.web_url)
        .collect();

    Ok(RuntimeInfo {
        version: format!("v{}", env!("CARGO_PKG_VERSION")),
        web_port: state.web_port,
        udp_port: state.udp_port,
        config_path: state.config_service.config_path().to_string_lossy().to_string(),
        lan_urls,
    })
}

#[command]
pub async fn reconnect_sdk(state: State<'_, Arc<AppState>>) -> Result<bool, String> {
    Ok(state.sdk_service.reconnect_sdk().await)
}

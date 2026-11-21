use crate::services::network_service;
use crate::state::AppState;
use axum::extract::State;
use axum::response::IntoResponse;
use axum::routing::get;
use axum::{Json, Router};
use serde_json::json;
use std::sync::Arc;

pub fn router() -> Router<Arc<AppState>> {
    Router::new()
        .route("/api/system/network", get(get_network_info))
        .route("/api/system/health", get(health_check))
}

async fn get_network_info(State(state): State<Arc<AppState>>) -> impl IntoResponse {
    let port = state.web_port;
    let ips = network_service::get_lan_ips(port);
    let primary_url = ips
        .first()
        .map(|i| i.web_url.clone())
        .unwrap_or_else(|| format!("http://localhost:{}", port));

    Json(json!({
        "webPort": port,
        "udpPort": state.udp_port,
        "primaryUrl": primary_url,
        "ips": ips
    }))
}

async fn health_check(State(state): State<Arc<AppState>>) -> impl IntoResponse {
    Json(json!({
        "status": "ok",
        "version": env!("CARGO_PKG_VERSION"),
        "webPort": state.web_port,
        "udpPort": state.udp_port,
        "sdkActive": state.device_manager.is_sdk_active()
    }))
}

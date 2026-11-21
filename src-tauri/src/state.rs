use crate::models::WsEvent;
use crate::services::adb_service::AdbService;
use crate::services::config_service::ConfigService;
use crate::services::device_manager::PicoDeviceManager;
use crate::services::pico_sdk_service::PicoSdkService;
use crate::services::screen_monitor::ScreenMonitorService;
use crate::services::udp_service::UdpCommandService;
use std::sync::Arc;
use tokio::sync::broadcast;

pub struct AppState {
    pub config_service: Arc<ConfigService>,
    pub screen_monitor: Arc<ScreenMonitorService>,
    pub adb_service: Arc<AdbService>,
    pub device_manager: Arc<PicoDeviceManager>,
    pub sdk_service: Arc<PicoSdkService>,
    pub udp_service: Arc<UdpCommandService>,
    pub ws_tx: broadcast::Sender<WsEvent>,
    pub web_port: u16,
    pub udp_port: u16,
}

impl AppState {
    pub fn initialize() -> Arc<Self> {
        let (ws_tx, _) = broadcast::channel::<WsEvent>(256);
        let config_service = Arc::new(ConfigService::new());
        let cfg = config_service.get_config();
        let web_port = if cfg.web_port > 0 { cfg.web_port } else { 8999 };
        let udp_port = if cfg.udp_port > 0 { cfg.udp_port } else { 8998 };

        let screen_monitor = Arc::new(ScreenMonitorService::new());
        let adb_service = Arc::new(AdbService::new(config_service.clone()));
        let device_manager = Arc::new(PicoDeviceManager::new(
            ws_tx.clone(),
            config_service.clone(),
            screen_monitor.clone(),
            adb_service.clone(),
        ));
        let sdk_service = PicoSdkService::new(
            device_manager.clone(),
            config_service.clone(),
            screen_monitor.clone(),
        );
        let udp_service = UdpCommandService::new(
            config_service.clone(),
            device_manager.clone(),
            ws_tx.clone(),
        );

        Arc::new(Self {
            config_service,
            screen_monitor,
            adb_service,
            device_manager,
            sdk_service,
            udp_service,
            ws_tx,
            web_port,
            udp_port,
        })
    }
}

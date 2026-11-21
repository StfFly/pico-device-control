// ======================== PicoManager 运营工作台 前端交互逻辑 ========================

// 多环境 API 基础路径兼容 (标准 Web 浏览器 / Tauri 桌面客户端)
const API_BASE = (window.location.protocol === 'tauri:' || window.location.protocol === 'asset:' || window.location.hostname === 'tauri.localhost')
  ? 'http://localhost:8999'
  : '';

function apiUrl(path) {
  if (path && path.startsWith('/') && API_BASE) {
    return API_BASE + path;
  }
  return path;
}

if (API_BASE) {
  const originalFetch = window.fetch;
  window.fetch = function(url, init) {
    if (typeof url === 'string' && url.startsWith('/')) {
      url = API_BASE + url;
    }
    return originalFetch(url, init);
  };
}

function safeSetText(id, text) {
  const el = document.getElementById(id);
  if (el) el.innerText = text;
}

// 全局状态
let devices = new Map();
let projects = [];
let systemConfig = null;
let selectedSns = new Set();
let currentMonitorSn = null;
let currentProject = null;
let globalVolume = 70;
let pendingSystemAction = null;
let hubConnection = null;
let isStreamingActive = false; // 按需拉流状态开关，默认省流待命

// 判断当前包名是否为系统桌面或企业桌面环境 (非体验游戏运行中)
function isDesktopApp(app) {
  if (!app) return true;
  const a = app.trim();
  return a === '' || 
         a === '系统桌面' || 
         a === 'com.picovr.enterpriseassistant' || 
         a === 'com.pico.launcher';
}

// 音效上下文
let audioCtx = null;
function playSound(type = 'click') {
  try {
    if (!audioCtx) audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    if (audioCtx.state === 'suspended') audioCtx.resume();

    const osc = audioCtx.createOscillator();
    const gain = audioCtx.createGain();
    osc.connect(gain);
    gain.connect(audioCtx.destination);

    const now = audioCtx.currentTime;
    if (type === 'click') {
      osc.frequency.setValueAtTime(800, now);
      osc.frequency.exponentialRampToValueAtTime(400, now + 0.05);
      gain.gain.setValueAtTime(0.12, now);
      gain.gain.exponentialRampToValueAtTime(0.01, now + 0.05);
      osc.start(now);
      osc.stop(now + 0.05);
    } else if (type === 'success') {
      osc.frequency.setValueAtTime(523.25, now);
      osc.frequency.setValueAtTime(659.25, now + 0.08);
      osc.frequency.setValueAtTime(783.99, now + 0.16);
      gain.gain.setValueAtTime(0.15, now);
      gain.gain.exponentialRampToValueAtTime(0.01, now + 0.28);
      osc.start(now);
      osc.stop(now + 0.28);
    } else if (type === 'warning') {
      osc.type = 'sawtooth';
      osc.frequency.setValueAtTime(320, now);
      osc.frequency.setValueAtTime(260, now + 0.1);
      gain.gain.setValueAtTime(0.15, now);
      gain.gain.exponentialRampToValueAtTime(0.01, now + 0.25);
      osc.start(now);
      osc.stop(now + 0.25);
    }
  } catch (e) { }
}

// 页面加载初始化
window.addEventListener('DOMContentLoaded', async () => {
  await loadConfig();
  await loadDevices();
  initSignalR();
  // 保障性心跳巡检：每 2.5 秒兜底同步一次真机状态，杜绝任何长连接意外导致的界面留白或漏更
  setInterval(loadDevices, 2500);
  setInterval(loadConfig, 15000);
  startCanvasSimulation();
  checkSuiteHealthBackground();
  setInterval(checkSuiteHealthBackground, 5000);
});

// ======================== 1. SignalR 实时长连接 ========================
function initSignalR() {
  startSignalR();
}

function startSignalR() {
  const statusPill = document.getElementById('system-status-pill');
  const statusText = document.getElementById('system-status-text');

  if (typeof signalR === 'undefined') {
    console.warn('SignalR 客户端库未加载，自动降级至 HTTP 轮询模式');
    if (statusPill && statusText) {
      statusPill.className = 'text-[11px] px-2 py-0.5 rounded-full bg-amber-50 text-amber-700 border border-amber-200 font-medium flex items-center gap-1';
      statusText.innerText = 'HTTP 轮询模式';
    }
    return;
  }

  if (hubConnection && (hubConnection.state === signalR.HubConnectionState.Connected || hubConnection.state === signalR.HubConnectionState.Connecting)) {
    return;
  }

  hubConnection = new signalR.HubConnectionBuilder()
    .withUrl(apiUrl('/hubs/pico'))
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .build();

  hubConnection.on('DeviceAdded', (dev) => {
    devices.set(dev.sn, dev);
    renderMatrix();
    updateTopStats();
    if (currentMonitorSn === dev.sn) refreshMonitorTelemetry(dev);
  });

  hubConnection.on('DeviceUpdated', (dev) => {
    devices.set(dev.sn, dev);
    renderMatrix();
    updateTopStats();
    if (currentMonitorSn === dev.sn) refreshMonitorTelemetry(dev);
  });

  hubConnection.on('DeviceRemoved', (sn) => {
    devices.delete(sn);
    selectedSns.delete(sn);
    renderMatrix();
    updateTopStats();
    if (currentMonitorSn === sn) {
      const first = Array.from(devices.values())[0];
      if (first) selectMonitorDevice(first.sn);
    }
  });

  hubConnection.on('UdpCommandReceived', (info) => {
    playSound(info.success ? 'success' : 'warning');
    showToast(`📡 [UDP指令] ${info.message}`, info.success ? 'info' : 'warning');
  });

  hubConnection.onreconnecting(() => {
    if (statusPill && statusText) {
      statusPill.className = 'text-[11px] px-2 py-0.5 rounded-full bg-amber-50 text-amber-700 border border-amber-200 font-medium flex items-center gap-1';
      statusText.innerText = '长连接重新连接中...';
    }
  });

  hubConnection.onreconnected(() => {
    if (statusPill && statusText) {
      statusPill.className = 'text-[11px] px-2 py-0.5 rounded-full bg-emerald-50 text-emerald-600 border border-emerald-200 font-medium flex items-center gap-1';
      statusText.innerText = '系统服务在线';
    }
    loadDevices();
  });

  hubConnection.onclose(() => {
    if (statusPill && statusText) {
      statusPill.className = 'text-[11px] px-2 py-0.5 rounded-full bg-rose-50 text-rose-600 border border-rose-200 font-medium flex items-center gap-1';
      statusText.innerText = '服务已断开，尝试重连...';
    }
    setTimeout(() => startSignalR(), 3000);
  });

  hubConnection.start()
    .then(() => {
      if (statusPill && statusText) {
        statusPill.className = 'text-[11px] px-2 py-0.5 rounded-full bg-emerald-50 text-emerald-600 border border-emerald-200 font-medium flex items-center gap-1';
        statusText.innerText = '系统服务在线 (端口 8999)';
      }
      loadDevices();
    })
    .catch(err => {
      console.warn('SignalR start error:', err);
      if (statusPill && statusText) {
        statusPill.className = 'text-[11px] px-2 py-0.5 rounded-full bg-amber-50 text-amber-700 border border-amber-200 font-medium flex items-center gap-1';
        statusText.innerText = 'HTTP 轮询模式';
      }
      loadDevices();
      setTimeout(() => startSignalR(), 5000);
    });
}

// ======================== 2. 数据拉取与渲染 ========================
async function loadConfig() {
  try {
    const res = await fetch('/api/config');
    if (res.ok) {
      const cfg = await res.json();
      systemConfig = cfg;
      projects = cfg.projects || [];
      renderProjects();
    }
  } catch (e) {
    console.error('loadConfig error:', e);
  }
}

async function loadDevices() {
  try {
    const res = await fetch('/api/devices');
    if (res.ok) {
      const devList = await res.json();
      devices.clear();
      devList.forEach(d => devices.set(d.sn, d));
      renderMatrix();
      updateTopStats();

      if (!currentMonitorSn && devList.length > 0) {
        selectMonitorDevice(devList[0].sn);
      }
    }
  } catch (e) {
    console.error('loadDevices error:', e);
  }
}

// 渲染第一步：体验项目海报卡片
function renderProjects() {
  const container = document.getElementById('project-cards-container');
  container.innerHTML = '';

  if (projects.length === 0) {
    container.innerHTML = '<div class="col-span-full py-4 text-center text-slate-400 text-xs">暂无配置的项目，请点击右上角新增项目</div>';
    return;
  }

  if (!currentProject && projects.length > 0) {
    currentProject = projects[0];
  }

  projects.forEach((proj) => {
    const isSelected = currentProject && currentProject.id === proj.id;
    const card = document.createElement('div');
    card.id = `proj-card-${proj.id}`;
    card.onclick = () => selectProject(proj.id);

    let borderClass = isSelected
      ? 'border-2 border-blue-600 bg-blue-50/70 shadow-md'
      : 'border border-slate-200 bg-white hover:border-slate-300';

    card.className = `project-card relative rounded-xl p-3.5 cursor-pointer transition flex items-center gap-3 ${borderClass}`;
    card.innerHTML = `
      <div class="w-14 h-14 rounded-xl bg-gradient-to-tr from-indigo-900 via-blue-900 to-indigo-700 text-2xl flex items-center justify-center flex-shrink-0 shadow-sm border border-blue-200">
        ${proj.icon || '🎮'}
      </div>
      <div class="min-w-0 flex-1">
        <div class="flex items-center justify-between">
          <span class="text-[10px] px-1.5 py-0.2 rounded font-semibold ${isSelected ? 'bg-blue-600 text-white' : 'bg-slate-100 text-slate-600'}">
            ${isSelected ? '当前选定' : proj.type === 'vr_video' ? '全景视频' : 'VR应用'}
          </span>
          <div class="flex items-center gap-1.5">
            <span class="text-[11px] font-mono ${isSelected ? 'text-blue-700 font-bold' : 'text-slate-500 font-medium'}">${proj.durationMinutes} 分钟</span>
            <button onclick="event.stopPropagation(); openEditProjectFromCard('${proj.id}')" title="修改此项目包名/名称/时长" class="w-5 h-5 rounded hover:bg-blue-100 text-slate-400 hover:text-blue-700 flex items-center justify-center text-xs transition" style="pointer-events: auto;">
              ✏️
            </button>
          </div>
        </div>
        <h3 class="font-bold text-sm text-slate-900 truncate mt-0.5">${proj.name}</h3>
        <p class="text-[11px] text-slate-400 font-mono truncate" title="${proj.packageName}">${proj.packageName}</p>
      </div>
    `;
    container.appendChild(card);
  });
}

function selectProject(projId) {
  playSound('click');
  currentProject = projects.find(p => p.id === projId) || null;
  renderProjects();
  if (currentProject) {
    showToast(`已选定体验内容：【${currentProject.name}】`);
  }
}

function openEditProjectFromCard(projId) {
  playSound('click');
  openConfigModal('app');
  setTimeout(() => {
    openEditProjectForm(projId);
  }, 100);
}

// 渲染第二步：设备矩阵
function renderMatrix() {
  const matrix = document.getElementById('device-matrix');
  matrix.innerHTML = '';

  const devArray = Array.from(devices.values()).sort((a, b) => {
    const numA = parseInt(a.alias.replace(/\D/g, '')) || 999;
    const numB = parseInt(b.alias.replace(/\D/g, '')) || 999;
    return numA - numB;
  });

  if (devArray.length === 0) {
    matrix.innerHTML = `
      <div class="col-span-full py-12 flex flex-col items-center justify-center text-center p-6 bg-white border-2 border-dashed border-slate-200 rounded-2xl">
        <div class="w-14 h-14 rounded-2xl bg-blue-50 text-blue-600 flex items-center justify-center text-2xl mb-3 shadow-inner">
          👓
        </div>
        <h3 class="text-sm font-bold text-slate-800">等待真实 PICO 头显连入局域网...</h3>
        <p class="text-xs text-slate-500 max-w-md mt-1.5 leading-relaxed">
          1. 确保本电脑已启动 <b>PICO Business Suite (官方企业套件)</b> 并登录<br/>
          2. 将 PICO 头戴开机并连接同一店内 Wi-Fi (建议 5GHz 无 AP 隔离)<br/>
          3. 可在右上角「绑定头显」预设机器编号与 SN 码，开机后自动点亮
        </p>
      </div>
    `;
    document.getElementById('selected-badge').innerText = 0;
    return;
  }

  devArray.forEach(dev => {
    const isSelected = selectedSns.has(dev.sn);
    const isMonitored = currentMonitorSn === dev.sn;
    const isTrackingLost = dev.online && (dev.trackingLost || (dev.currentApp && dev.currentApp.toLowerCase().includes('seethrough.setting')));
    const isRunning = dev.online && !isTrackingLost && !isDesktopApp(dev.currentApp);
    const isLow = dev.online && dev.battery > 0 && dev.battery <= 30;

    // 默认状态与背景
    let cardBg = 'bg-white hover:bg-slate-50 border-slate-200 text-slate-800 shadow-sm';
    let badgeBg = 'bg-emerald-50 text-emerald-700 border-emerald-200';
    let badgeText = '空闲';

    if (!dev.online) {
      cardBg = 'bg-slate-100/70 border-slate-200 text-slate-400 opacity-60';
      badgeBg = 'bg-slate-200 text-slate-500 border-slate-300';
      badgeText = '离线';
    } else if (isTrackingLost) {
      cardBg = 'bg-amber-50 hover:bg-amber-100/80 border-2 border-amber-500 text-amber-950 shadow-md ring-2 ring-amber-400/50 animate-pulse';
      badgeBg = 'bg-amber-500 text-white border-amber-600 font-bold';
      badgeText = '⚠️ 定位丢失';
    } else if (isRunning) {
      cardBg = 'bg-blue-600 hover:bg-blue-700 border-blue-600 text-white shadow-md shadow-blue-500/20';
      badgeBg = 'bg-blue-700/80 text-blue-100 border-blue-500';
      badgeText = '体验中';
    } else if (isLow) {
      cardBg = 'bg-amber-50 hover:bg-amber-100/70 border-amber-300 text-amber-900';
      badgeBg = 'bg-amber-200 text-amber-900 border-amber-300';
      badgeText = '低电';
    }

    // 选中高亮状态
    let selectHighlight = '';
    if (isSelected) {
      selectHighlight = 'ring-4 ring-amber-400 border-amber-400 shadow-xl';
    }

    let monitorHighlight = '';
    if (isMonitored && !isSelected) {
      monitorHighlight = 'border-2 border-cyan-400 ring-2 ring-cyan-400/50';
    }

    const tagBadge = dev.online
      ? `<span class="text-[9px] px-1 py-0.5 rounded bg-emerald-100 text-emerald-800 border border-emerald-300 font-bold">🟢在线</span>`
      : `<span class="text-[9px] px-1 py-0.5 rounded bg-slate-100 text-slate-500 border border-slate-200">⚪离线</span>`;

    const card = document.createElement('div');
    card.className = `relative rounded-xl border p-2.5 flex flex-col justify-between cursor-pointer transition-all select-none ${cardBg} ${selectHighlight} ${monitorHighlight}`;

    // 点击卡片直接切换选中，并激活右侧监控面板
    card.onclick = (e) => {
      if (e.target.closest('.action-btn')) return;
      toggleSelectDevice(dev.sn);
      selectMonitorDevice(dev.sn);
    };

    const ctrlText = !dev.online
      ? '🎮 未连接'
      : `🎮 L:${dev.controllerLeftBattery}% R:${dev.controllerRightBattery}%`;

    const displayTitle = dev.displayName || dev.sn;
    const suiteBadge = dev.suiteNo ? `<span class="inline-block text-[9px] px-1.5 py-0.2 rounded bg-indigo-100 text-indigo-800 border border-indigo-200 font-bold" title="PICO 企业套件编号">套件#${dev.suiteNo}</span>` : '';
    const screenTag = dev.online && dev.screenOn === false ? `<span class="text-[9px] px-1 py-0.2 rounded bg-slate-800 text-slate-200 font-bold" title="屏幕已熄灭">🌑息屏</span>` : '';

    card.innerHTML = `
      <!-- 顶栏：复选框 + 头显电量 + 状态徽章 -->
      <div class="flex items-center justify-between text-[11px] font-mono leading-none">
        <div class="flex items-center gap-1.5">
          <input type="checkbox" ${isSelected ? 'checked' : ''} class="w-4 h-4 rounded text-blue-600 accent-blue-600 cursor-pointer pointer-events-none" />
          <span class="flex items-center gap-0.5 font-bold ${isRunning ? 'text-blue-100' : isLow ? 'text-rose-600' : 'text-slate-700'}">
            ${dev.isCharging ? '⚡' : '🔋'}${!dev.online ? '--' : dev.battery + '%'}
          </span>
          ${screenTag}
        </div>
        <div class="flex items-center gap-1">
          ${tagBadge}
          <span class="px-1.5 py-0.5 rounded text-[10px] font-sans font-semibold border ${badgeBg}">
            ${badgeText}
          </span>
        </div>
      </div>

      <!-- 中部机号/别名与套件编号 -->
      <div class="py-1 text-center">
        <span class="text-xl sm:text-2xl font-black tracking-tight font-mono block truncate">${displayTitle}</span>
        <div class="flex items-center justify-center gap-1 mt-0.5">
          ${suiteBadge}
          <span class="text-[9px] font-mono opacity-70 truncate tracking-tighter" title="${dev.sn}">SN:${dev.sn.slice(-8)}</span>
        </div>
        ${isTrackingLost ? '<div class="text-[10px] text-amber-900 font-bold bg-amber-200/90 rounded py-0.5 px-1 mt-1 text-center">⚠️ 大空间定位丢失 (透视中)</div>' : ''}
      </div>

      <!-- 手柄电量醒目条 -->
      <div class="px-1.5 py-0.5 rounded text-[10px] font-mono text-center mb-1 ${isRunning ? 'bg-blue-700/60 text-blue-100' : 'bg-slate-100 text-slate-600'}">
        ${ctrlText}
      </div>

      <!-- 脚部：佩戴状态与小眼睛监控图标 -->
      <div class="flex items-center justify-between text-[10px] pt-1 border-t ${isRunning ? 'border-blue-500/60 text-blue-200' : 'border-slate-100 text-slate-400'}">
        <span>${dev.isWorn ? '👓已佩戴' : '未佩戴'}</span>
        <button onclick="event.stopPropagation(); selectMonitorDevice('${dev.sn}')" class="action-btn px-1.5 py-0.5 rounded hover:bg-black/10 transition font-medium" title="调取此机详情与运维工具">
          👁️ 监看
        </button>
      </div>

      ${isSelected ? '<div class="absolute -top-1.5 -right-1.5 w-5 h-5 bg-amber-500 text-white rounded-full flex items-center justify-center text-xs font-black border-2 border-white shadow">✓</div>' : ''}
    `;

    matrix.appendChild(card);
  });

  document.getElementById('selected-badge').innerText = selectedSns.size;
}

// 统计指标与低电量警告
function updateTopStats() {
  const all = Array.from(devices.values());
  const idle = all.filter(d => d.online && isDesktopApp(d.currentApp)).length;
  const running = all.filter(d => d.online && !isDesktopApp(d.currentApp)).length;
  const offline = all.filter(d => !d.online).length;
  const lowDevs = all.filter(d => d.online && d.battery > 0 && d.battery <= 30);

  safeSetText('stat-total', all.length);
  safeSetText('stat-idle', idle);
  safeSetText('stat-running', running);
  safeSetText('stat-offline', offline);
  safeSetText('stat-low', lowDevs.length);

  // 兼容其他模板/别名 ID
  safeSetText('top-stat-total', all.length);
  safeSetText('top-stat-online', idle + running);
  safeSetText('top-stat-idle', idle);
  safeSetText('top-stat-running', running);
  safeSetText('top-stat-offline', offline);

  safeSetText('btn-total-online', idle + running);
  safeSetText('btn-total-idle', idle);
  safeSetText('btn-total-running', running);

  const lowAlert = document.getElementById('low-battery-alert');
  if (lowAlert) {
    if (lowDevs.length > 0) {
      const names = lowDevs.map(d => '#' + d.displayName).join('、');
      safeSetText('low-battery-names', names);
      lowAlert.classList.remove('hidden');
    } else {
      lowAlert.classList.add('hidden');
    }
  }
}

// ======================== 3. 机位选择交互 ========================
function toggleSelectDevice(sn) {
  playSound('click');
  if (selectedSns.has(sn)) selectedSns.delete(sn);
  else selectedSns.add(sn);
  renderMatrix();
}

function selectAllOnline() {
  playSound('click');
  selectedSns.clear();
  devices.forEach(d => {
    if (d.online) selectedSns.add(d.sn);
  });
  renderMatrix();
  showToast(`已一键全选全部 ${selectedSns.size} 台在线头显`);
}

function selectAllIdle() {
  playSound('click');
  selectedSns.clear();
  devices.forEach(d => {
    if (d.online && isDesktopApp(d.currentApp)) {
      selectedSns.add(d.sn);
    }
  });
  renderMatrix();
  showToast(`已勾选 ${selectedSns.size} 台空闲待命头显`);
}

function selectAllRunning() {
  playSound('click');
  selectedSns.clear();
  devices.forEach(d => {
    if (d.online && !isDesktopApp(d.currentApp)) {
      selectedSns.add(d.sn);
    }
  });
  renderMatrix();
  showToast(`已勾选 ${selectedSns.size} 台正在体验中的头显`);
}

function clearSelection() {
  playSound('click');
  selectedSns.clear();
  renderMatrix();
  showToast('已清空所选机位');
}

// ======================== 4. 单机按需画面监看与运维 ========================
function selectMonitorDevice(sn) {
  const dev = devices.get(sn);
  if (!dev) return;

  // 若之前在给其他设备拉流，切换时主动停止旧流，避免产生多路带宽堆积
  if (currentMonitorSn && currentMonitorSn !== sn && isStreamingActive) {
    stopLiveStreamInternal(currentMonitorSn);
  }

  currentMonitorSn = sn;
  refreshMonitorTelemetry(dev);

  // 默认进入省流待命状态，不自动拉流
  isStreamingActive = false;
  const streamImg = document.getElementById('side-monitor-img');
  const overlay = document.getElementById('stream-standby-overlay');
  const liveBar = document.getElementById('stream-live-bar');
  const canvas = document.getElementById('side-monitor-canvas');

  streamImg.src = '';
  streamImg.classList.add('hidden');
  liveBar.classList.add('hidden');
  overlay.classList.remove('hidden');
  canvas.classList.remove('hidden');

  renderMatrix();
}

function refreshMonitorTelemetry(dev) {
  document.getElementById('monitor-panel-title').innerText = `${dev.displayName}`;
  document.getElementById('panel-sn').innerText = dev.sn;
  const badge = document.getElementById('monitor-panel-badge');

  const isTrackingLost = dev.online && (dev.trackingLost || (dev.currentApp && dev.currentApp.toLowerCase().includes('seethrough.setting')));
  const isRunning = dev.online && !isTrackingLost && !isDesktopApp(dev.currentApp);
  if (!dev.online) {
    badge.className = 'text-[10px] px-2 py-0.5 rounded-md bg-slate-100 text-slate-500 border border-slate-200 font-medium';
    badge.innerText = '离线';
  } else if (isTrackingLost) {
    badge.className = 'text-[10px] px-2 py-0.5 rounded-md bg-amber-500 text-white border border-amber-600 font-bold animate-pulse';
    badge.innerText = '⚠️ 大空间丢失';
  } else if (isRunning) {
    badge.className = 'text-[10px] px-2 py-0.5 rounded-md bg-blue-50 text-blue-600 border border-blue-200 font-medium';
    badge.innerText = '正在体验';
  } else {
    badge.className = 'text-[10px] px-2 py-0.5 rounded-md bg-emerald-50 text-emerald-600 border border-emerald-200 font-medium';
    badge.innerText = '空闲待命';
  }

  document.getElementById('panel-battery').innerText = !dev.online ? '--' : `${dev.battery}%`;
  document.getElementById('panel-charging').innerText = dev.isCharging ? '⚡ 充电中' : '🔋 未充电';
  document.getElementById('panel-controllers').innerText = !dev.online
    ? '左: -- | 右: --'
    : `左: ${dev.controllerLeftBattery}% | 右: ${dev.controllerRightBattery}%`;
  document.getElementById('panel-worn').innerText = !dev.online ? '--' : dev.isWorn ? '👓 游客佩戴中' : '⚠️ 已脱下';
  if (isTrackingLost) {
    document.getElementById('panel-app').innerText = '⚠️ 大空间定位丢失 (透视重定中)';
  } else {
    document.getElementById('panel-app').innerText = isDesktopApp(dev.currentApp) ? '系统桌面' : (dev.appTitle || dev.currentApp);
  }
}

let snapshotTimer = null;

let streamStatusPollTimer = null;
let lastKnownSuiteHealth = null;

async function checkSuiteHealthBackground() {
  try {
    const res = await fetch('/api/stream/suite-status');
    if (res.ok) {
      lastKnownSuiteHealth = await res.json();
      updateSuiteLinkIndicator(lastKnownSuiteHealth);
    }
  } catch (e) { }
}

function updateSuiteLinkIndicator(health) {
  const el = document.getElementById('suite-link-indicator');
  if (!el) return;
  if (!health) {
    el.innerText = '套件: 未知';
    el.className = 'text-slate-400 font-mono text-[9px]';
    return;
  }
  if (!health.isProcessRunning) {
    el.innerText = '🔴 官方套件未运行';
    el.className = 'text-rose-400 font-mono text-[9px] cursor-pointer hover:underline';
    el.title = '未检测到 PICOBusinessSuite.exe，点击查看诊断';
  } else if (!health.isPortOpen) {
    el.innerText = '🟡 套件未登录 (60061未开放)';
    el.className = 'text-amber-400 font-mono text-[9px] cursor-pointer hover:underline';
    el.title = '套件已运行但未登录，点击查看诊断';
  } else if (!health.isSdkActive) {
    el.innerText = '🟡 SDK 握手中...';
    el.className = 'text-amber-300 font-mono text-[9px] cursor-pointer hover:underline';
    el.title = '正在与官方套件建立 gRPC 连接';
  } else {
    el.innerText = '🟢 套件服务(60061)就绪';
    el.className = 'text-emerald-400 font-mono text-[9px] cursor-pointer hover:underline';
    el.title = '链路正常，支持实时屏幕监控';
  }
}

function startStreamStatusPolling(sn) {
  stopStreamStatusPolling();
  streamStatusPollTimer = setInterval(async () => {
    if (!isStreamingActive || currentMonitorSn !== sn) {
      stopStreamStatusPolling();
      return;
    }
    try {
      const res = await fetch(`/api/stream/${sn}/status`);
      if (res.ok) {
        const data = await res.json();
        updateStreamStatusPill(data.streamStatus, data.suiteHealth, data.hasRealFrame);
        if (data.suiteHealth) {
          updateSuiteLinkIndicator(data.suiteHealth);
        }
      }
    } catch (e) { }
  }, 1200);
}

function stopStreamStatusPolling() {
  if (streamStatusPollTimer) {
    clearInterval(streamStatusPollTimer);
    streamStatusPollTimer = null;
  }
}

function updateStreamStatusPill(status, health, hasRealFrame) {
  const textEl = document.getElementById('stream-status-text');
  const dotEl = document.getElementById('stream-status-dot');
  const pillEl = document.getElementById('stream-status-pill');
  if (!textEl || !dotEl || !pillEl) return;

  if (health && !health.isPortOpen) {
    textEl.innerText = '⚠️ 官方套件未就绪 (60061离线)';
    dotEl.className = 'w-1.5 h-1.5 rounded-full bg-rose-500';
    pillEl.className = 'bg-black/85 backdrop-blur px-2.5 py-1 rounded-md text-[10px] font-mono text-rose-400 flex items-center gap-1.5 shadow pointer-events-auto cursor-pointer border border-rose-500/40 hover:border-rose-400 transition';
    return;
  }

  if (hasRealFrame || status?.phase === 'Streaming' || status?.phase === 4) {
    textEl.innerText = `LIVE 实时传输中 (${status?.lastWidth || 854}x${status?.lastHeight || 480})`;
    dotEl.className = 'w-1.5 h-1.5 rounded-full bg-emerald-400 animate-ping';
    pillEl.className = 'bg-black/75 backdrop-blur px-2.5 py-1 rounded-md text-[10px] font-mono text-emerald-400 flex items-center gap-1.5 shadow pointer-events-auto cursor-pointer border border-emerald-500/30 hover:border-emerald-400 transition';
  } else if (status?.phase === 'WaitingFirstFrame' || status?.phase === 3) {
    textEl.innerText = '⏳ 等待头显推流响应...';
    dotEl.className = 'w-1.5 h-1.5 rounded-full bg-amber-400 animate-ping';
    pillEl.className = 'bg-black/85 backdrop-blur px-2.5 py-1 rounded-md text-[10px] font-mono text-amber-300 flex items-center gap-1.5 shadow pointer-events-auto cursor-pointer border border-amber-500/40 hover:border-amber-400 transition';
  } else if (status?.phase === 'Error' || status?.phase === 5) {
    textEl.innerText = '❌ 监看请求异常';
    dotEl.className = 'w-1.5 h-1.5 rounded-full bg-rose-500';
    pillEl.className = 'bg-black/85 backdrop-blur px-2.5 py-1 rounded-md text-[10px] font-mono text-rose-400 flex items-center gap-1.5 shadow pointer-events-auto cursor-pointer border border-rose-500/40 hover:border-rose-400 transition';
  }
}

// 用户手动切换实时画面拉流 (按需开启，省流模式)
async function toggleLiveStream() {
  if (!currentMonitorSn) return;
  const dev = devices.get(currentMonitorSn);
  if (!dev || !dev.online) {
    showToast('该头显已离线，无法开启画面监控', '⚠️');
    return;
  }

  playSound('click');

  const streamImg = document.getElementById('side-monitor-img');
  const overlay = document.getElementById('stream-standby-overlay');
  const liveBar = document.getElementById('stream-live-bar');
  const canvas = document.getElementById('side-monitor-canvas');

  if (!isStreamingActive) {
    // 检查套件连通性
    try {
      const hRes = await fetch('/api/stream/suite-status');
      if (hRes.ok) {
        lastKnownSuiteHealth = await hRes.json();
        updateSuiteLinkIndicator(lastKnownSuiteHealth);
        if (!lastKnownSuiteHealth.isPortOpen) {
          showToast('⚠️ 官方套件未就绪(60061端口未开放)，请启动并登录套件', '⚠️');
        }
      }
    } catch (e) { }

    // 开启拉流
    isStreamingActive = true;
    overlay.classList.add('hidden');
    liveBar.classList.remove('hidden');
    canvas.classList.add('hidden');
    streamImg.classList.remove('hidden');

    const startRes = await fetch(`/api/stream/${dev.sn}/start`, { method: 'POST' });
    if (startRes.ok) {
      const data = await startRes.json();
      if (data.status) {
        updateStreamStatusPill(data.status, lastKnownSuiteHealth);
      }
    }

    startStreamStatusPolling(dev.sn);

    streamImg.src = `/api/stream/${dev.sn}/mjpeg?t=${Date.now()}`;
    streamImg.onerror = () => {
      console.log('[LiveStream] MJPEG 流断开或不支持，平滑切换为高频快照轮询模式...');
      startSnapshotStream(dev.sn);
    };
    showToast(`已开启 ${dev.displayName} 实时画面监控`);
  } else {
    // 关闭拉流
    stopLiveStreamInternal(dev.sn);
    showToast('已关闭画面拉流，恢复省流模式');
  }
}

function startSnapshotStream(sn) {
  if (snapshotTimer) clearInterval(snapshotTimer);
  const streamImg = document.getElementById('side-monitor-img');
  streamImg.classList.remove('hidden');
  let inFlight = false;

  snapshotTimer = setInterval(async () => {
    if (!isStreamingActive || currentMonitorSn !== sn) {
      clearInterval(snapshotTimer);
      snapshotTimer = null;
      return;
    }
    if (inFlight) return;
    inFlight = true;
    try {
      const res = await fetch(`/api/stream/${sn}/frame?t=${Date.now()}`);
      if (res.ok) {
        const blob = await res.blob();
        if (blob.size > 0) {
          const url = URL.createObjectURL(blob);
          const oldUrl = streamImg.src;
          streamImg.onload = () => {
            if (oldUrl && oldUrl.startsWith('blob:')) URL.revokeObjectURL(oldUrl);
          };
          streamImg.src = url;
        }
      }
    } catch (e) {
    } finally {
      inFlight = false;
    }
  }, 65); // 约 15~16 fps 顺滑拉流
}

function stopLiveStreamInternal(sn) {
  isStreamingActive = false;
  stopStreamStatusPolling();

  if (snapshotTimer) {
    clearInterval(snapshotTimer);
    snapshotTimer = null;
  }
  const streamImg = document.getElementById('side-monitor-img');
  const overlay = document.getElementById('stream-standby-overlay');
  const liveBar = document.getElementById('stream-live-bar');
  const canvas = document.getElementById('side-monitor-canvas');

  if (streamImg.src && streamImg.src.startsWith('blob:')) {
    URL.revokeObjectURL(streamImg.src);
  }
  streamImg.src = '';
  streamImg.classList.add('hidden');
  liveBar.classList.add('hidden');
  overlay.classList.remove('hidden');
  canvas.classList.remove('hidden');

  fetch(`/api/stream/${sn}/stop`, { method: 'POST' }).catch(() => {});
}

async function openSuiteDiagModal() {
  const modal = document.getElementById('suite-diag-modal');
  if (!modal) return;
  modal.classList.remove('hidden');
  playSound('click');

  document.getElementById('diag-target-sn').innerText = currentMonitorSn || '未选择';
  document.getElementById('diag-time').innerText = new Date().toLocaleTimeString();

  await refreshSuiteDiagModal();
}

function closeSuiteDiagModal() {
  const modal = document.getElementById('suite-diag-modal');
  if (modal) modal.classList.add('hidden');
}

async function refreshSuiteDiagModal() {
  try {
    const res = await fetch('/api/stream/suite-status');
    if (!res.ok) return;
    const data = await res.json();
    lastKnownSuiteHealth = data;
    updateSuiteLinkIndicator(data);

    // 更新各个徽章
    const procBadge = document.getElementById('diag-proc-badge');
    if (data.isProcessRunning) {
      procBadge.innerText = '✅ 已运行';
      procBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-emerald-100 text-emerald-700';
    } else {
      procBadge.innerText = '❌ 未检测到进程';
      procBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-rose-100 text-rose-700';
    }

    const portBadge = document.getElementById('diag-port-badge');
    if (data.isPortOpen) {
      portBadge.innerText = '✅ 端口开放 (60061)';
      portBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-emerald-100 text-emerald-700';
    } else {
      portBadge.innerText = '❌ 端口未开启 (需登录套件)';
      portBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-rose-100 text-rose-700';
    }

    const sdkBadge = document.getElementById('diag-sdk-badge');
    if (data.isSdkActive) {
      sdkBadge.innerText = '✅ 已激活连接';
      sdkBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-emerald-100 text-emerald-700';
    } else {
      sdkBadge.innerText = '🟡 未连接';
      sdkBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-amber-100 text-amber-700';
    }

    const streamBadge = document.getElementById('diag-stream-badge');
    const streamInfo = currentMonitorSn && data.streams ? data.streams[currentMonitorSn] : null;
    if (streamInfo) {
      if (streamInfo.phase === 'Streaming' || streamInfo.phase === 4) {
        streamBadge.innerText = `🟢 推流正常 (${streamInfo.totalFrames} 帧)`;
        streamBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-emerald-100 text-emerald-700';
      } else if (streamInfo.phase === 'WaitingFirstFrame' || streamInfo.phase === 3) {
        streamBadge.innerText = '⏳ 等待头显推流首帧';
        streamBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-amber-100 text-amber-700';
      } else if (streamInfo.phase === 'SuiteOffline' || streamInfo.phase === 1) {
        streamBadge.innerText = '🔴 官方套件离线';
        streamBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-rose-100 text-rose-700';
      } else {
        streamBadge.innerText = '待命省流中';
        streamBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-slate-100 text-slate-600';
      }
    } else {
      streamBadge.innerText = '未拉流';
      streamBadge.className = 'px-2 py-0.5 rounded text-[10px] font-bold bg-slate-100 text-slate-600';
    }

    // 更新总结卡片
    const titleEl = document.getElementById('diag-summary-title');
    const descEl = document.getElementById('diag-summary-desc');
    const iconEl = document.getElementById('diag-summary-icon');
    const boxEl = document.getElementById('diag-summary-box');

    if (data.isPortOpen && data.isSdkActive) {
      iconEl.innerText = '🎉';
      titleEl.innerText = '官方套件链路通信正常';
      titleEl.className = 'font-bold text-emerald-800 text-sm';
      descEl.innerText = data.statusSummary || '链路就绪，可随时开启头显屏幕画面实时监控';
      boxEl.className = 'p-3.5 rounded-xl bg-emerald-50 border border-emerald-200 flex items-start gap-3';
    } else {
      iconEl.innerText = '⚠️';
      titleEl.innerText = '官方套件链路尚未就绪';
      titleEl.className = 'font-bold text-amber-800 text-sm';
      descEl.innerText = data.statusSummary || '未开放 60061 端口，请确保 PICO Business Suite 处于登录状态';
      boxEl.className = 'p-3.5 rounded-xl bg-amber-50 border border-amber-200 flex items-start gap-3';
    }

    if (data.advice) {
      document.getElementById('diag-advice-text').innerHTML = `
        <p class="font-bold">诊断指引: ${data.advice}</p>
        <p>1. 官方套件必须处于登录状态，未登录或未连接网络时套件不会启动 gRPC 60061 端口。</p>
        <p>2. 头盔必须处于开机、亮屏、佩戴状态，并在同一个局域网 Wi-Fi 下。</p>
      `;
    }
  } catch (e) { }
}

async function reconnectSuiteSdk() {
  showToast('正在请求重新握手官方套件 SDK...', '🔄');
  try {
    const res = await fetch('/api/stream/reconnect', { method: 'POST' });
    if (res.ok) {
      const data = await res.json();
      if (data.success) {
        showToast('★ 官方企业套件 SDK 握手成功！', '🎉');
      } else {
        showToast('套件 60061 端口未就绪，请先登录 PICO Business Suite', '⚠️');
      }
      await refreshSuiteDiagModal();
    }
  } catch (e) {
    showToast('重连请求异常: ' + e.message, '❌');
  }
}

function copyCurrentSn() {
  if (!currentMonitorSn) return;
  navigator.clipboard.writeText(currentMonitorSn).then(() => {
    playSound('click');
    showToast(`已复制头显 SN 码: ${currentMonitorSn}`);
  });
}

// 单机运维操作指令
async function singleDeviceAction(action) {
  if (!currentMonitorSn) {
    showToast('请先选择一台设备', '⚠️');
    return;
  }
  const dev = devices.get(currentMonitorSn);
  if (!dev) return;

  playSound('click');

  if (action === 'recenter') {
    await fetch(`/api/devices/${dev.sn}/recenter`, { method: 'POST' });
    showToast(`已向 ${dev.displayName} 下发【🎯 视角正中复位】指令`);
  } else if (action === 'home') {
    await fetch(`/api/devices/${dev.sn}/stop-app`, { method: 'POST' });
    showToast(`已直接关闭 ${dev.displayName} 的游戏进程`);
  } else if (action === 'lock') {
    const url = dev.locked ? `/api/devices/${dev.sn}/unlock` : `/api/devices/${dev.sn}/lock`;
    await fetch(url, { method: 'POST' });
    showToast(`已切换 ${dev.displayName} 锁定状态`);
  } else if (action === 'screen_on') {
    await fetch(`/api/devices/${dev.sn}/screen`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ screenOn: true })
    });
    dev.screenOn = true;
    renderMatrix();
    showToast(`已向 ${dev.displayName} 发送【💡 点亮屏幕】指令`);
  } else if (action === 'screen_off') {
    await fetch(`/api/devices/${dev.sn}/screen`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ screenOn: false })
    });
    dev.screenOn = false;
    renderMatrix();
    showToast(`已向 ${dev.displayName} 发送【🌑 熄灭屏幕】指令`);
  } else if (action === 'reboot') {
    if (confirm(`确认要远程重启 ${dev.displayName} (SN: ${dev.sn}) 吗？`)) {
      await fetch(`/api/devices/${dev.sn}/reboot`, { method: 'POST' });
      showToast(`已向 ${dev.displayName} 发送重启指令`);
    }
  }
}

// ======================== 5. 批量播控操作 ========================
async function batchAction(type) {
  const snList = Array.from(selectedSns);
  if (snList.length === 0) {
    playSound('warning');
    showToast('请先选择机位 (可点击机号或上方一键全选)', '⚠️');
    return;
  }

  // 离线设备校验：防止误向离线设备下发指令
  const onlineSns = snList.filter(sn => devices.get(sn)?.online);
  if (onlineSns.length === 0) {
    playSound('warning');
    showToast('所选设备当前均处于离线状态，无法下发播控指令', '⚠️');
    return;
  }

  if (type === 'start') {
    if (!currentProject) {
      playSound('warning');
      showToast('请先在第一步选择要播放的项目', '⚠️');
      return;
    }

    playSound('success');
    showToast(`🚀 正在向 ${snList.length} 台设备下发启动【${currentProject.name}】...`);

    const res = await fetch('/api/batch/start-app', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        snList: snList,
        packageName: currentProject.packageName,
        extraJson: currentProject.extraJson || ''
      })
    });

    if (res.ok) {
      showToast(`🚀 体验启动成功，共分发 ${snList.length} 台设备！`);
      selectedSns.clear();
      renderMatrix();
    }
  } else if (type === 'pause') {
    playSound('click');
    await fetch('/api/batch/pause-video', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snList })
    });
    showToast(`⏸ 已对选中的 ${snList.length} 台设备下发【暂停】指令`);
  } else if (type === 'stop') {
    playSound('click');
    await fetch('/api/batch/stop-app', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snList })
    });
    showToast(`⏹ 已直接关闭 ${snList.length} 台设备的游戏进程`);
    selectedSns.clear();
    renderMatrix();
  } else if (type === 'screen_on') {
    playSound('click');
    await fetch('/api/batch/screen', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snList, screenOn: true })
    });
    snList.forEach(sn => {
      const dev = devices.get(sn);
      if (dev) dev.screenOn = true;
    });
    renderMatrix();
    showToast(`💡 已对 ${snList.length} 台设备执行【一键亮屏】`);
  } else if (type === 'screen_off') {
    playSound('click');
    await fetch('/api/batch/screen', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snList, screenOn: false })
    });
    snList.forEach(sn => {
      const dev = devices.get(sn);
      if (dev) dev.screenOn = false;
    });
    renderMatrix();
    showToast(`🌑 已对 ${snList.length} 台设备执行【一键息屏】`);
  }
}

// 全局音量调节（带 200ms 滑动防抖，防止高频网络请求）
let globalVolumeDebounce = null;
function onGlobalVolumeInput(val) {
  globalVolume = parseInt(val);
  safeSetText('global-vol-text', val + '%');

  if (globalVolumeDebounce) clearTimeout(globalVolumeDebounce);
  globalVolumeDebounce = setTimeout(() => {
    applyGlobalVolume(val, false);
  }, 200);
}

async function applyGlobalVolume(val, showFeedback = true) {
  if (globalVolumeDebounce) {
    clearTimeout(globalVolumeDebounce);
    globalVolumeDebounce = null;
  }
  const snList = selectedSns.size > 0
    ? Array.from(selectedSns)
    : Array.from(devices.values()).filter(d => d.online).map(d => d.sn);

  if (snList.length === 0) return;

  try {
    await fetch('/api/batch/volume', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        snList: snList,
        volume: parseInt(val)
      })
    });
    if (showFeedback) {
      showToast(`已调节 ${snList.length} 台设备音量为 ${val}%`);
    }
  } catch (e) {
    console.error('音量同步失败:', e);
  }
}

// ======================== 6. 重启与关机二次确认 ========================
function promptBatchSystem(action) {
  const snList = selectedSns.size > 0
    ? Array.from(selectedSns)
    : Array.from(devices.values()).filter(d => d.online).map(d => d.sn);

  if (snList.length === 0) {
    showToast('当前没有设备可操作', '⚠️');
    return;
  }

  playSound('warning');
  pendingSystemAction = action;
  const count = snList.length;

  const modal = document.getElementById('confirm-modal');
  const icon = document.getElementById('confirm-icon');
  const iconBg = document.getElementById('confirm-icon-bg');
  const title = document.getElementById('confirm-title');
  const desc = document.getElementById('confirm-desc');
  const okBtn = document.getElementById('confirm-ok-btn');

  if (action === 'reboot') {
    icon.innerText = '🔄';
    iconBg.className = 'w-12 h-12 rounded-full mx-auto flex items-center justify-center text-2xl bg-amber-100';
    title.innerText = `确认重启 ${count} 台设备？`;
    desc.innerText = `即将向勾选的 ${count} 台 PICO 头显发送远程重启指令，设备将在重启后自动重连。`;
    okBtn.className = 'py-2.5 bg-amber-600 hover:bg-amber-700 text-white rounded-xl text-xs font-bold shadow-md shadow-amber-600/30';
    okBtn.innerText = '确认立即重启';
  } else {
    icon.innerText = '⏻';
    iconBg.className = 'w-12 h-12 rounded-full mx-auto flex items-center justify-center text-2xl bg-rose-100';
    title.innerText = `确认批量关闭 ${count} 台设备？`;
    desc.innerText = `即将向勾选的 ${count} 台 PICO 头显发送关机指令，关机后需现场物理开机。`;
    okBtn.className = 'py-2.5 bg-rose-600 hover:bg-rose-700 text-white rounded-xl text-xs font-bold shadow-md shadow-rose-600/30';
    okBtn.innerText = '确认立即关机';
  }

  modal.classList.remove('hidden');
}

function closeConfirmModal() {
  document.getElementById('confirm-modal').classList.add('hidden');
  pendingSystemAction = null;
}

async function executeBatchSystemConfirmed() {
  const snList = selectedSns.size > 0
    ? Array.from(selectedSns)
    : Array.from(devices.values()).filter(d => d.online).map(d => d.sn);

  const action = pendingSystemAction;
  closeConfirmModal();

  if (action === 'reboot') {
    await fetch('/api/batch/reboot', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snList })
    });
    showToast(`已成功向 ${snList.length} 台头显下发【批量重启】指令`);
  } else if (action === 'shutdown') {
    await fetch('/api/batch/shutdown', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ snList })
    });
    showToast(`已成功向 ${snList.length} 台头显下发【批量关机】指令`);
  }

  selectedSns.clear();
  renderMatrix();
}

// ======================== 7. 配置中心交互 (含 SN 绑定核心逻辑) ========================
function openConfigModal(tab = 'app') {
  playSound('click');
  document.getElementById('config-modal').classList.remove('hidden');
  switchConfigTab(tab);
  renderConfigTables();
}

function closeConfigModal() {
  document.getElementById('config-modal').classList.add('hidden');
}

function switchConfigTab(tab) {
  const appBtn = document.getElementById('tab-btn-app');
  const headsetBtn = document.getElementById('tab-btn-headset');
  const udpBtn = document.getElementById('tab-btn-udp');
  const envBtn = document.getElementById('tab-btn-env');
  const appContent = document.getElementById('tab-content-app');
  const headsetContent = document.getElementById('tab-content-headset');
  const udpContent = document.getElementById('tab-content-udp');
  const envContent = document.getElementById('tab-content-env');

  [appBtn, headsetBtn, udpBtn, envBtn].forEach(b => {
    if (b) b.className = 'py-3 px-4 border-b-2 border-transparent text-slate-400 hover:text-slate-700 transition whitespace-nowrap';
  });
  [appContent, headsetContent, udpContent, envContent].forEach(c => {
    if (c) c.classList.add('hidden');
  });

  if (tab === 'app') {
    if (appBtn) appBtn.className = 'py-3 px-4 border-b-2 border-blue-600 text-blue-600 transition whitespace-nowrap';
    if (appContent) appContent.classList.remove('hidden');
  } else if (tab === 'headset') {
    if (headsetBtn) headsetBtn.className = 'py-3 px-4 border-b-2 border-blue-600 text-blue-600 transition whitespace-nowrap';
    if (headsetContent) headsetContent.classList.remove('hidden');
  } else if (tab === 'udp') {
    if (udpBtn) udpBtn.className = 'py-3 px-4 border-b-2 border-indigo-600 text-indigo-600 transition whitespace-nowrap';
    if (udpContent) udpContent.classList.remove('hidden');
    renderUdpDeviceProjects();
    updateTestUdpPreview();
  } else if (tab === 'env') {
    if (envBtn) envBtn.className = 'py-3 px-4 border-b-2 border-blue-600 text-blue-600 transition whitespace-nowrap';
    if (envContent) envContent.classList.remove('hidden');
    loadPicoServiceStatus();
  }
}

// ==========================================
// PICO Business Suite 服务模式管理
// ==========================================
async function loadPicoServiceStatus() {
  const badge = document.getElementById('pico-service-badge');
  const pathSpan = document.getElementById('pico-ini-path');
  const procSpan = document.getElementById('pico-proc-status');

  if (badge) {
    badge.textContent = '正在检测...';
    badge.className = 'text-xs px-2.5 py-1 rounded-full font-bold border bg-slate-100 text-slate-600 border-slate-300';
  }

  try {
    const res = await fetch('/api/config/pico-service-mode');
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const data = await res.json();

    if (pathSpan) pathSpan.textContent = data.iniPath || '未检测到配置文件';

    if (badge) {
      if (data.runAsService) {
        badge.textContent = '🟢 已开启服务模式 (runAsService=true)';
        badge.className = 'text-xs px-2.5 py-1 rounded-full font-bold border bg-emerald-100 text-emerald-800 border-emerald-300';
      } else {
        badge.textContent = '🔴 未开启服务模式 (runAsService=false)';
        badge.className = 'text-xs px-2.5 py-1 rounded-full font-bold border bg-rose-100 text-rose-800 border-rose-300';
      }
    }

    if (procSpan) {
      if (data.isProcessRunning) {
        procSpan.innerHTML = `<span class="text-emerald-600 font-bold">🟢 正在运行 (PID: ${data.processId})</span>`;
      } else {
        procSpan.innerHTML = `<span class="text-slate-400">⚪ 未运行</span>`;
      }
    }
  } catch (err) {
    console.error('加载 PICO 服务模式失败:', err);
    if (badge) {
      badge.textContent = '⚠️ 检测异常';
      badge.className = 'text-xs px-2.5 py-1 rounded-full font-bold border bg-amber-100 text-amber-800 border-amber-300';
    }
  }
}

async function setPicoServiceMode(enable) {
  try {
    const res = await fetch('/api/config/pico-service-mode', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enable: enable, restartProcess: false })
    });
    const result = await res.json();
    if (result.success) {
      showToast('✓ ' + (enable ? '已成功开启服务模式！' : '已成功关闭服务模式！'));
      loadPicoServiceStatus();
    } else {
      showToast('⚠️ 修改失败: ' + result.message);
    }
  } catch (err) {
    showToast('⚠️ 网络错误: ' + err.message);
  }
}

async function restartPicoService() {
  showToast('正在重启 PICO 企业服务客户端...');
  try {
    const res = await fetch('/api/config/pico-service-mode/restart', { method: 'POST' });
    const result = await res.json();
    if (result.success) {
      showToast('✓ ' + result.message);
    } else {
      showToast('⚠️ ' + result.message);
    }
    setTimeout(loadPicoServiceStatus, 1500);
  } catch (err) {
    showToast('⚠️ 重启请求失败: ' + err.message);
  }
}

function renderConfigTables() {
  // 1. 体验项目表格
  const appTbody = document.getElementById('app-config-tbody');
  appTbody.innerHTML = '';
  projects.forEach(p => {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="p-3 font-sans font-bold text-slate-800">
        <span class="mr-1.5 text-base">${p.icon || '🎮'}</span>${p.name}
      </td>
      <td class="p-3 font-sans">
        <span class="px-2 py-0.5 rounded text-[10px] ${p.type === 'vr_video' ? 'bg-indigo-50 text-indigo-700' : 'bg-emerald-50 text-emerald-700'}">
          ${p.type === 'vr_video' ? 'VR视频' : 'VR应用'}
        </span>
      </td>
      <td class="p-3 text-blue-600 font-mono">${p.packageName}</td>
      <td class="p-3 font-sans">${p.durationMinutes}</td>
      <td class="p-3 text-right font-sans space-x-2">
        <button onclick="openEditProjectForm('${p.id}')" class="text-blue-600 hover:text-blue-800 text-xs font-semibold">编辑</button>
        <button onclick="deleteProject('${p.id}')" class="text-rose-500 hover:text-rose-700 text-xs">删除</button>
      </td>
    `;
    appTbody.appendChild(tr);
  });

  // 2. 头显 SN 绑定表格 (SN 为核心主键列)
  const headsetTbody = document.getElementById('headset-config-tbody');
  headsetTbody.innerHTML = '';
  const devList = Array.from(devices.values()).sort((a, b) => {
    const numA = parseInt(a.alias.replace(/\D/g, '')) || 999;
    const numB = parseInt(b.alias.replace(/\D/g, '')) || 999;
    return numA - numB;
  });

  devList.forEach(dev => {
    const tr = document.createElement('tr');
    tr.innerHTML = `
      <td class="p-3 font-mono font-bold text-slate-900">
        <div class="flex items-center gap-1.5">
          <span>${dev.sn}</span>
          <button onclick="copySn('${dev.sn}')" class="text-[10px] text-blue-600 hover:underline font-sans font-normal">复制</button>
        </div>
      </td>
      <td class="p-3 font-mono text-slate-700">
        ${dev.suiteNo ? `<span class="px-2 py-0.5 rounded bg-indigo-50 text-indigo-700 border border-indigo-200 font-bold">${dev.suiteNo}</span>` : '<span class="text-slate-400">--</span>'}
      </td>
      <td class="p-3 font-bold text-blue-600 font-sans">
        <span class="px-2 py-0.5 rounded bg-blue-50 border border-blue-200">${dev.alias || '<span class="text-slate-400 font-normal">未设置别名</span>'}</span>
      </td>
      <td class="p-3 font-sans text-slate-500">${dev.model}</td>
      <td class="p-3 font-sans ${dev.online ? 'text-emerald-600 font-semibold' : 'text-slate-400'}">
        ${dev.online ? '● 在线就绪' : '○ 离线'}
      </td>
      <td class="p-3 text-right font-sans space-x-2">
        <button onclick="openAliasModal('${dev.sn}', '${dev.alias || ''}')" class="text-blue-600 hover:underline font-semibold">修改别名</button>
        <button onclick="unbindHeadset('${dev.sn}')" class="text-rose-500 hover:underline">解绑</button>
      </td>
    `;
    headsetTbody.appendChild(tr);
  });

  // 3. UDP 机号对应游戏绑定表格
  renderUdpDeviceProjects();
}

// 渲染机器号 ➔ 默认游戏绑定表
function renderUdpDeviceProjects() {
  const tbody = document.getElementById('udp-device-project-tbody');
  if (!tbody) return;
  tbody.innerHTML = '';

  const devList = Array.from(devices.values()).sort((a, b) => {
    const numA = parseInt(a.alias.replace(/\D/g, '')) || 999;
    const numB = parseInt(b.alias.replace(/\D/g, '')) || 999;
    return numA - numB;
  });

  if (devList.length === 0) {
    tbody.innerHTML = `
      <tr>
        <td colspan="4" class="p-4 text-center text-slate-400 text-xs">
          当前暂无设备在线，开机接入头戴或载入演示设备后可在此快捷配置机号对应体验游戏
        </td>
      </tr>
    `;
    return;
  }

  const defaultProjects = (systemConfig && systemConfig.deviceDefaultProjects) || {};

  devList.forEach(dev => {
    const tr = document.createElement('tr');
    const devKey = dev.alias || dev.sn;
    const boundPkg = defaultProjects[dev.alias] || defaultProjects[dev.sn] || '';

    let optionsHtml = `<option value="">-- 未设置默认 (使用系统第1项) --</option>`;
    projects.forEach(p => {
      const isSelected = (boundPkg === p.packageName || boundPkg === p.id);
      optionsHtml += `<option value="${p.packageName}" ${isSelected ? 'selected' : ''}>${p.icon || '🎮'} ${p.name} (${p.packageName})</option>`;
    });

    tr.innerHTML = `
      <td class="p-2.5 font-bold text-blue-600 font-sans">
        <span class="px-2 py-0.5 rounded bg-blue-50 border border-blue-200">${dev.displayName}</span>
      </td>
      <td class="p-2.5 font-mono text-slate-600 text-[11px]">${dev.sn}</td>
      <td class="p-2.5">
        <select onchange="saveDeviceProject('${devKey}', this.value)" class="w-full max-w-sm px-2.5 py-1 text-xs border border-slate-300 rounded-lg bg-white font-sans focus:ring-2 focus:ring-indigo-400">
          ${optionsHtml}
        </select>
      </td>
      <td class="p-2.5 text-right font-sans ${dev.online ? 'text-emerald-600 font-semibold' : 'text-slate-400'}">
        ${dev.online ? '● 在线就绪' : '○ 离线'}
      </td>
    `;
    tbody.appendChild(tr);
  });
}

async function saveDeviceProject(deviceKey, packageOrProjectId) {
  try {
    const res = await fetch('/api/config/device-project', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        deviceKey: deviceKey,
        packageOrProjectId: packageOrProjectId
      })
    });
    if (res.ok) {
      playSound('success');
      showToast(`已成功保存【${deviceKey}】默认绑定游戏`);
      if (!systemConfig) systemConfig = {};
      if (!systemConfig.deviceDefaultProjects) systemConfig.deviceDefaultProjects = {};
      if (packageOrProjectId) {
        systemConfig.deviceDefaultProjects[deviceKey] = packageOrProjectId;
      } else {
        delete systemConfig.deviceDefaultProjects[deviceKey];
      }
    } else {
      showToast('保存失败', 'warning');
    }
  } catch (e) {
    showToast('网络请求错误: ' + e.message, 'warning');
  }
}

function updateTestUdpPreview() {
  const devNo = document.getElementById('test-udp-devno')?.value?.trim() || '022';
  const action = document.getElementById('test-udp-action')?.value || 'start';
  const pkg = document.getElementById('test-udp-pkg')?.value?.trim();

  const payload = { deviceNo: devNo, action: action };
  if (pkg) payload.packageName = pkg;

  const preview = document.getElementById('test-udp-preview-json');
  if (preview) {
    preview.innerText = '报文预览: ' + JSON.stringify(payload);
  }
}

function onTestUdpActionChange(action) {
  const pkgInput = document.getElementById('test-udp-pkg');
  if (pkgInput) {
    if (action === 'start') {
      pkgInput.disabled = false;
      pkgInput.placeholder = '选填，留空则使用该机号绑定的游戏';
    } else {
      pkgInput.disabled = true;
      pkgInput.placeholder = '当前动作无需包名';
    }
  }
  updateTestUdpPreview();
}

async function sendTestUdpMessage() {
  const devNo = document.getElementById('test-udp-devno')?.value?.trim() || '022';
  const action = document.getElementById('test-udp-action')?.value || 'start';
  const pkg = document.getElementById('test-udp-pkg')?.value?.trim() || '';

  const msg = {
    deviceNo: devNo,
    action: action,
    packageName: pkg
  };

  const resultBox = document.getElementById('test-udp-result-box');
  if (resultBox) {
    resultBox.classList.remove('hidden');
    resultBox.innerText = `正在通过本地 UDP 端口向 127.0.0.1:8998 发送数据包...\n${JSON.stringify(msg, null, 2)}`;
  }

  try {
    const res = await fetch('/api/config/test-udp', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(msg)
    });

    const data = await res.json();
    playSound(data.success ? 'success' : 'warning');
    if (resultBox) {
      resultBox.innerText = `[UDP 发送完成 & 接收到回执]\n` + (typeof data.ack === 'string' ? data.ack : JSON.stringify(data, null, 2));
    }
    showToast(`UDP 报文测试完成：${data.success ? '执行成功' : '失败'}`);
  } catch (e) {
    playSound('warning');
    if (resultBox) {
      resultBox.innerText = `[UDP 发送异常]: ${e.message}`;
    }
    showToast('UDP 测试异常: ' + e.message, 'warning');
  }
}

function copySn(sn) {
  navigator.clipboard.writeText(sn).then(() => {
    showToast(`已复制序列号: ${sn}`);
  });
}

// 手动录入新头盔绑定
function openBindHeadsetForm() {
  document.getElementById('headset-bind-form-container').classList.remove('hidden');
  document.getElementById('form-bind-sn').value = '';
  document.getElementById('form-bind-alias').value = '';
}

function closeBindHeadsetForm() {
  document.getElementById('headset-bind-form-container').classList.add('hidden');
}

async function submitBindHeadsetForm() {
  const sn = document.getElementById('form-bind-sn').value.trim().toUpperCase();
  const alias = document.getElementById('form-bind-alias').value.trim();
  const model = document.getElementById('form-bind-model').value;

  if (!sn) {
    alert('请填写头显唯一序列号 SN 码！');
    return;
  }

  const res = await fetch('/api/config/bind-headset', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sn, alias, model })
  });

  if (res.ok) {
    closeBindHeadsetForm();
    await loadDevices();
    renderConfigTables();
    showToast(`头显 SN【${sn}】已成功绑定为【${alias || sn}】！`);
  }
}

async function unbindHeadset(sn) {
  if (!confirm(`确认要解除头显 SN【${sn}】的机号绑定吗？`)) return;

  const res = await fetch(`/api/config/bind-headset/${sn}`, {
    method: 'DELETE'
  });

  if (res.ok) {
    // 立即从前端设备集合中移除，无需等待刷新
    devices.delete(sn);
    selectedSns.delete(sn);
    renderMatrix();
    updateTopStats();
    await loadDevices();
    renderConfigTables();
    showToast(`已解除头显 SN【${sn}】的绑定并移出列表`);
  }
}

// 体验项目配置交互
function openAddProjectForm() {
  document.getElementById('project-form-container').classList.remove('hidden');
  document.getElementById('project-form-title').innerText = '添加新体验项目';
  document.getElementById('form-proj-id').value = '';
  document.getElementById('form-proj-name').value = '';
  document.getElementById('form-proj-type').value = 'vr_app';
  document.getElementById('form-proj-package').value = '';
  document.getElementById('form-proj-duration').value = '15';
  document.getElementById('form-proj-icon').value = '🎮';
  document.getElementById('form-proj-name').focus();
}

function openEditProjectForm(id) {
  const p = projects.find(x => x.id === id);
  if (!p) return;
  document.getElementById('project-form-container').classList.remove('hidden');
  document.getElementById('project-form-title').innerText = `编辑体验项目 / 修改包名【${p.name}】`;
  document.getElementById('form-proj-id').value = p.id;
  document.getElementById('form-proj-name').value = p.name;
  document.getElementById('form-proj-type').value = p.type || 'vr_app';
  document.getElementById('form-proj-package').value = p.packageName;
  document.getElementById('form-proj-duration').value = p.durationMinutes || 15;
  document.getElementById('form-proj-icon').value = p.icon || '🎮';
  document.getElementById('form-proj-package').focus();
}

function closeProjectForm() {
  document.getElementById('project-form-container').classList.add('hidden');
  document.getElementById('form-proj-id').value = '';
}

async function submitProjectForm() {
  const id = document.getElementById('form-proj-id').value.trim();
  const name = document.getElementById('form-proj-name').value.trim();
  const type = document.getElementById('form-proj-type').value;
  const packageName = document.getElementById('form-proj-package').value.trim();
  const durationMinutes = parseInt(document.getElementById('form-proj-duration').value) || 15;
  const icon = document.getElementById('form-proj-icon').value.trim() || '🎮';

  if (!name || !packageName) {
    alert('请填写项目名称和 Android 包名！');
    return;
  }

  const payload = { id: id || undefined, name, type, packageName, durationMinutes, icon };
  const res = await fetch('/api/config/projects', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });

  if (res.ok) {
    closeProjectForm();
    await loadConfig();
    renderProjects();
    renderConfigTables();
    showToast(id ? `项目【${name}】包名与配置已更新！` : `项目【${name}】添加成功！`);
  }
}

async function deleteProject(id) {
  if (!confirm('确认删除该体验项目配置吗？')) return;
  const res = await fetch(`/api/config/projects/${id}`, { method: 'DELETE' });
  if (res.ok) {
    await loadConfig();
    renderConfigTables();
    showToast('项目已删除');
  }
}

// 别名编辑弹窗
let currentEditingSn = null;
function openAliasModal(sn, currentAlias) {
  currentEditingSn = sn;
  document.getElementById('alias-modal-sn').innerText = sn;
  document.getElementById('alias-modal-input').value = currentAlias;
  document.getElementById('alias-modal').classList.remove('hidden');
}

function closeAliasModal() {
  document.getElementById('alias-modal').classList.add('hidden');
  currentEditingSn = null;
}

async function submitAliasModal() {
  const newAlias = document.getElementById('alias-modal-input').value.trim();
  if (!currentEditingSn) return;

  const res = await fetch('/api/config/alias', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sn: currentEditingSn, alias: newAlias })
  });

  if (res.ok) {
    closeAliasModal();
    const dev = devices.get(currentEditingSn);
    if (dev) {
      dev.alias = newAlias;
    }
    renderMatrix();
    renderConfigTables();
    showToast(`设备编号已更新为【${newAlias || currentEditingSn}】并同步至头显`);
  }
}

// ======================== 8. 辅助 UI 提示与仿真 Canvas ========================
function showToast(msg, icon = '✓') {
  const toast = document.getElementById('toast');
  document.getElementById('toast-msg').innerText = msg;
  document.getElementById('toast-icon').innerText = icon;
  toast.classList.remove('translate-y-20', 'opacity-0');
  clearTimeout(window._t);
  window._t = setTimeout(() => {
    toast.classList.add('translate-y-20', 'opacity-0');
  }, 2300);
}

function startCanvasSimulation() {
  const canvas = document.getElementById('side-monitor-canvas');
  if (!canvas) return;
  const ctx = canvas.getContext('2d');
  canvas.width = 480;
  canvas.height = 270;

  let angle = 0;

  function renderFrame() {
    angle += 0.03;
    const w = canvas.width;
    const h = canvas.height;

    const dev = currentMonitorSn ? devices.get(currentMonitorSn) : null;

    if (!dev || !dev.online) {
      ctx.fillStyle = '#0f172a';
      ctx.fillRect(0, 0, w, h);
      ctx.fillStyle = '#475569';
      ctx.font = '13px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('头显离线或未接入，暂无画面流', w / 2, h / 2);
      requestAnimationFrame(renderFrame);
      return;
    }

    const isRunning = dev.currentApp && !isDesktopApp(dev.currentApp);

    const grad = ctx.createLinearGradient(0, 0, w, h);
    grad.addColorStop(0, '#020617');
    grad.addColorStop(1, '#1e293b');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, w, h);

    // 地平网格
    ctx.strokeStyle = 'rgba(59, 130, 246, 0.2)';
    ctx.lineWidth = 1;
    const horizon = h * 0.55;
    for (let x = -w; x <= w * 2; x += 40) {
      ctx.beginPath();
      ctx.moveTo(w / 2, horizon);
      ctx.lineTo(x, h);
      ctx.stroke();
    }

    const cx = w / 2 + Math.sin(angle) * 50;
    const cy = horizon - 20 + Math.cos(angle * 1.5) * 12;

    if (isRunning) {
      ctx.fillStyle = '#38bdf8';
      ctx.beginPath();
      ctx.arc(cx, cy, 24, 0, Math.PI * 2);
      ctx.fill();
      ctx.strokeStyle = '#0284c7';
      ctx.lineWidth = 3;
      ctx.beginPath();
      ctx.ellipse(cx, cy, 44, 12, angle, 0, Math.PI * 2);
      ctx.stroke();
    } else {
      ctx.strokeStyle = '#64748b';
      ctx.lineWidth = 2;
      ctx.strokeRect(w / 2 - 50, horizon - 45, 100, 55);
      ctx.fillStyle = '#94a3b8';
      ctx.font = '12px sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('PICO 系统桌面 (省流待命中)', w / 2, horizon - 15);
    }

    // 准星
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.6)';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(w / 2, h / 2, 8, 0, Math.PI * 2);
    ctx.stroke();

    requestAnimationFrame(renderFrame);
  }

  requestAnimationFrame(renderFrame);
}

// ======================== 9. 无线 ADB 局域网探针与设备控制 ========================
let adbDevicesList = [];

function openAdbModal() {
  const modal = document.getElementById('adb-modal');
  if (modal) modal.classList.remove('hidden');
  switchAdbTab('devices');
  loadAdbDevices();
}

function closeAdbModal() {
  document.getElementById('adb-modal').classList.add('hidden');
}

async function loadAdbDevices() {
  try {
    const res = await fetch('/api/adb/devices');
    if (res.ok) {
      const data = await res.json();
      adbDevicesList = data.devices || [];
      renderAdbTable();
      renderDeployTargetCheckboxes();
    }
  } catch (e) {
    console.error('loadAdbDevices error:', e);
  }
}

function renderAdbTable() {
  const tbody = document.getElementById('adb-devices-tbody');
  const badge = document.getElementById('adb-devices-count-badge');
  if (!tbody) return;

  const connectedCount = adbDevicesList.filter(d => d.isConnected).length;
  if (badge) badge.innerText = `${connectedCount} 台已连接 (共 ${adbDevicesList.length} 台)`;

  tbody.innerHTML = '';
  if (adbDevicesList.length === 0) {
    tbody.innerHTML = `
      <tr>
        <td colspan="6" class="p-6 text-center text-slate-400">
          暂无已连接的 ADB 设备。点击上方「全网段自动扫描并连接头显」或输入头戴 IP 建立无线连接
        </td>
      </tr>
    `;
    return;
  }

  adbDevicesList.forEach(dev => {
    const tr = document.createElement('tr');
    tr.className = 'hover:bg-slate-50 transition';
    tr.innerHTML = `
      <td class="p-3 font-mono font-bold text-slate-800">
        <div class="flex items-center gap-1.5">
          <span class="text-emerald-600">${dev.isWireless ? '📶' : '🔌'}</span>
          <span>${dev.target}</span>
        </div>
      </td>
      <td class="p-3 font-mono font-bold ${dev.sn ? 'text-indigo-600' : 'text-slate-400'}">
        ${dev.sn || '<span class="text-[11px] font-normal text-slate-400">待读取</span>'}
      </td>
      <td class="p-3 font-sans text-slate-600">${dev.model || 'PICO VR'}</td>
      <td class="p-3 font-mono ${dev.battery >= 0 ? (dev.battery <= 20 ? 'text-rose-600 font-bold' : 'text-emerald-600') : 'text-slate-400'}">
        ${dev.battery >= 0 ? `${dev.battery}%` : '--'}
      </td>
      <td class="p-3 font-sans">
        <span class="px-2 py-0.5 rounded text-[10px] font-semibold ${dev.isConnected ? 'bg-emerald-50 text-emerald-700 border border-emerald-200' : 'bg-slate-100 text-slate-500'}">
          ${dev.isConnected ? '● 已就绪' : dev.status}
        </span>
      </td>
      <td class="p-3 text-right font-sans space-x-2">
        <button onclick="readAdbSn('${dev.target}')" class="text-blue-600 hover:underline">读取SN</button>
        <button onclick="rebootAdb('${dev.target}')" class="text-amber-600 hover:underline">重启</button>
        <button onclick="disconnectAdb('${dev.target}')" class="text-rose-500 hover:underline">断开</button>
      </td>
    `;
    tbody.appendChild(tr);
  });
}

async function startAdbScan() {
  const btn = document.getElementById('adb-scan-btn');
  const icon = document.getElementById('adb-scan-icon');
  const text = document.getElementById('adb-scan-text');
  const progress = document.getElementById('adb-scan-progress');

  btn.disabled = true;
  icon.classList.add('animate-spin');
  text.innerText = '正在全网段并发探测头戴中...';
  if (progress) progress.classList.remove('hidden');

  try {
    showToast('正在高速扫描局域网所有活跃网段 (TCP 5555)...', '🔍');
    const res = await fetch('/api/adb/scan', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({})
    });
    if (res.ok) {
      const data = await res.json();
      adbDevicesList = data.devices || [];
      renderAdbTable();
      renderDeployTargetCheckboxes();
      showToast(`扫描完毕，共发现并连接 ${data.discoveredCount || 0} 台头戴设备！`, '✓');
    }
  } catch (e) {
    showToast(`扫描出错: ${e.message}`, '⚠️');
  } finally {
    btn.disabled = false;
    icon.classList.remove('animate-spin');
    text.innerText = '全网段自动扫描并连接头显';
    if (progress) progress.classList.add('hidden');
  }
}

async function connectManualAdb() {
  const ipInput = document.getElementById('adb-manual-ip');
  const portInput = document.getElementById('adb-manual-port');
  const ip = ipInput.value.trim();
  const port = parseInt(portInput.value) || 5555;

  if (!ip) {
    alert('请输入头显局域网 IP 地址！');
    return;
  }

  showToast(`正在尝试连接 ${ip}:${port}...`, '🔄');
  try {
    const res = await fetch('/api/adb/connect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ip, port })
    });
    const data = await res.json();
    if (data.success) {
      showToast(data.message || '连接成功！', '✓');
      ipInput.value = '';
      await loadAdbDevices();
    } else {
      showToast(data.message || '连接失败，请确认设备已开机并在同一局域网', '⚠️');
    }
  } catch (e) {
    showToast(`连接异常: ${e.message}`, '⚠️');
  }
}

async function disconnectAdb(target) {
  if (!confirm(`确认断开与 ${target} 的无线 ADB 连接吗？`)) return;
  try {
    await fetch('/api/adb/disconnect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ target })
    });
    showToast(`已断开 ${target}`);
    await loadAdbDevices();
  } catch (e) { }
}

async function readAdbSn(target) {
  showToast(`正在通过 ADB 读取 ${target} 硬件 SN...`, '🔍');
  try {
    const res = await fetch('/api/adb/read-sn', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ target })
    });
    const data = await res.json();
    if (data.success) {
      showToast(`读取成功 SN: ${data.sn}`, '✓');
      await loadAdbDevices();
    } else {
      showToast('读取 SN 失败，可能权限不足或未完全开机', '⚠️');
    }
  } catch (e) {
    showToast(`读取异常: ${e.message}`, '⚠️');
  }
}

async function rebootAdb(target) {
  if (!confirm(`确认要远程重启头显 ${target} 吗？`)) return;
  try {
    await fetch('/api/adb/reboot', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ target })
    });
    showToast(`已向 ${target} 下发重启指令！`, '✓');
  } catch (e) { }
}

async function syncAdbDevicesToConfig() {
  showToast('正在将已识别的 ADB 设备同步至机号列表...', '🔄');
  try {
    const res = await fetch('/api/adb/sync-to-config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({})
    });
    const data = await res.json();
    if (data.success) {
      showToast(`已成功同步 ${data.syncedCount || 0} 台新设备到机位列表！`, '✓');
      await loadDevices();
      renderConfigTables();
    }
  } catch (e) {
    showToast(`同步失败: ${e.message}`, '⚠️');
  }
}

function switchAdbTab(tab) {
  try {
    const btnDevices = document.getElementById('adb-tab-btn-devices');
    const btnDeploy = document.getElementById('adb-tab-btn-deploy');
    const contentDevices = document.getElementById('adb-tab-content-devices');
    const contentDeploy = document.getElementById('adb-tab-content-deploy');

    if (!btnDevices || !btnDeploy || !contentDevices || !contentDeploy) {
      console.warn('ADB tab elements not found', { btnDevices, btnDeploy, contentDevices, contentDeploy });
      return;
    }

    if (tab === 'devices') {
      btnDevices.className = 'py-3 text-emerald-600 border-b-2 border-emerald-600 flex items-center gap-1.5 transition font-bold';
      btnDeploy.className = 'py-3 text-slate-500 hover:text-slate-700 border-b-2 border-transparent flex items-center gap-1.5 transition font-bold';
      contentDevices.classList.remove('hidden');
      contentDeploy.classList.add('hidden');
    } else {
      btnDeploy.className = 'py-3 text-emerald-600 border-b-2 border-emerald-600 flex items-center gap-1.5 transition font-bold';
      btnDevices.className = 'py-3 text-slate-500 hover:text-slate-700 border-b-2 border-transparent flex items-center gap-1.5 transition font-bold';
      contentDevices.classList.add('hidden');
      contentDeploy.classList.remove('hidden');
      renderDeployTargetCheckboxes();
    }
  } catch (err) {
    console.error('switchAdbTab error:', err);
  }
}

function renderDeployTargetCheckboxes() {
  try {
    const container = document.getElementById('deploy-devices-checkboxes');
    const countBadge = document.getElementById('deploy-selected-count');
    if (!container) return;

    const list = Array.isArray(adbDevicesList) ? adbDevicesList : [];
    const connected = list.filter(d => d && (d.isConnected || d.status === 'device'));
    container.innerHTML = '';

    if (connected.length === 0) {
      container.innerHTML = `
        <div class="flex items-center gap-2 p-3 bg-amber-50 border border-amber-200 rounded-xl text-amber-800 text-xs w-full">
          <span>⚠️ 当前暂无就绪状态的无线 ADB 头显。</span>
          <button type="button" onclick="switchAdbTab('devices'); startAdbScan();" class="text-blue-600 underline font-semibold ml-auto hover:text-blue-800">
            👉 切回探针页自动扫描
          </button>
        </div>
      `;
      if (countBadge) countBadge.innerText = '0 台可用';
      return;
    }

    connected.forEach(dev => {
      if (!dev || !dev.target) return;
      const label = document.createElement('label');
      label.className = 'flex items-center gap-1.5 bg-slate-50 hover:bg-slate-100 border border-slate-200 px-2.5 py-1.5 rounded-lg cursor-pointer transition select-none';
      let displayName = dev.target;
      if (dev.sn && typeof devices !== 'undefined' && devices.has(dev.sn)) {
        const dObj = devices.get(dev.sn);
        if (dObj && dObj.displayName) {
          displayName = `${dObj.displayName} (${dev.target})`;
        }
      }
      label.innerHTML = `
        <input type="checkbox" name="deploy-device-target" value="${dev.target}" checked class="rounded text-emerald-600 accent-emerald-600" onchange="updateDeploySelectedCount()" />
        <span class="font-mono text-slate-700 font-bold">${displayName}</span>
      `;
      container.appendChild(label);
    });

    updateDeploySelectedCount();
  } catch (err) {
    console.error('renderDeployTargetCheckboxes error:', err);
  }
}

function updateDeploySelectedCount() {
  const checks = document.querySelectorAll('input[name="deploy-device-target"]:checked');
  const countBadge = document.getElementById('deploy-selected-count');
  if (countBadge) countBadge.innerText = `已选 ${checks.length} 台设备`;
}

function toggleSelectAllDeployDevices(select) {
  document.querySelectorAll('input[name="deploy-device-target"]').forEach(cb => cb.checked = select);
  updateDeploySelectedCount();
}

function getSelectedDeployTargets() {
  return Array.from(document.querySelectorAll('input[name="deploy-device-target"]:checked')).map(cb => cb.value);
}

function appendDeployLog(msg, isErr = false) {
  const box = document.getElementById('deploy-console-box');
  const logs = document.getElementById('deploy-console-logs');
  if (!box || !logs) return;
  box.classList.remove('hidden');
  const time = new Date().toLocaleTimeString();
  const line = document.createElement('div');
  line.className = isErr ? 'text-rose-400' : 'text-emerald-300';
  line.innerText = `[${time}] ${msg}`;
  logs.appendChild(line);
  box.scrollTop = box.scrollHeight;
}

async function executeDeployInstallApk() {
  const fileInput = document.getElementById('deploy-apk-file');
  const localPathInput = document.getElementById('deploy-apk-localpath');
  const localPath = localPathInput ? localPathInput.value.trim() : '';
  const hasFile = fileInput.files && fileInput.files[0];

  if (!hasFile && !localPath) {
    alert('请选择要上传的 .apk 文件，或输入服务器本地 APK 绝对路径！');
    return;
  }
  const targets = getSelectedDeployTargets();
  if (targets.length === 0) {
    alert('请至少勾选一台目标头戴设备！');
    return;
  }

  if (localPath) {
    // 方案 A：服务器本地路径极速直推（免上传·秒装）
    const fileName = localPath.replace(/\\/g, '/').split('/').pop();
    if (!confirm(`确认要将服务器本地安装包【${fileName}】免上传直接安装到 ${targets.length} 台设备吗？\n（路径: ${localPath}）`)) return;

    appendDeployLog(`🚀 [本地直推] 开始批量安装 APK: ${fileName}，目标: ${targets.join(', ')}`);
    showToast(`正在免上传直接安装【${fileName}】...`, '📲');

    try {
      const res = await fetch('/api/adb/local-install', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ localPath, targets })
      });
      const data = await res.json();
      if (data.success && data.results) {
        data.results.forEach(r => {
          appendDeployLog(`设备 [${r.target}]: ${r.message}`, !r.success);
        });
        showToast('APK 批量本地直发安装执行完毕！', '✓');
      } else {
        appendDeployLog(`❌ 安装失败: ${data.message || '未知错误'}`, true);
        showToast(`安装出错: ${data.message}`, '⚠️');
      }
    } catch (e) {
      appendDeployLog(`❌ 网络或服务端错误: ${e.message}`, true);
    }
    return;
  }

  // 方案 B：传统网页文件上传推流安装
  const file = fileInput.files[0];
  if (!confirm(`确认要将安装包【${file.name}】(${(file.size / 1024 / 1024).toFixed(1)}MB) 批量安装到 ${targets.length} 台设备吗？`)) return;

  appendDeployLog(`🚀 开始批量上传安装 APK: ${file.name}，目标设备: ${targets.join(', ')}`);
  showToast(`正在上传并推流安装【${file.name}】...`, '📲');

  const formData = new FormData();
  formData.append('file', file);
  formData.append('targets', targets.join(','));

  try {
    const res = await fetch('/api/adb/upload-install', {
      method: 'POST',
      body: formData
    });
    const data = await res.json();
    if (data.success && data.results) {
      data.results.forEach(r => {
        appendDeployLog(`设备 [${r.target}]: ${r.message}`, !r.success);
      });
      showToast('APK 批量安装执行完毕！', '✓');
    } else {
      appendDeployLog(`❌ 安装失败: ${data.message || '未知错误'}`, true);
      showToast(`安装出错: ${data.message}`, '⚠️');
    }
  } catch (e) {
    appendDeployLog(`❌ 网络或服务端错误: ${e.message}`, true);
  }
}

async function queryDeployPackages() {
  const targets = getSelectedDeployTargets();
  const target = targets.length > 0 ? targets[0] : (adbDevicesList.find(d => d.isConnected)?.target);
  if (!target) {
    alert('请先连接头戴设备！');
    return;
  }

  showToast(`正在从设备 ${target} 读取已安装第三方包名...`, '🔍');
  try {
    const res = await fetch(`/api/adb/packages?target=${encodeURIComponent(target)}`);
    const data = await res.json();
    if (data.success && data.packages && data.packages.length > 0) {
      const dropdown = document.getElementById('deploy-pkg-dropdown');
      dropdown.innerHTML = `<option value="">-- 选择已检索到的第三方包名 (${data.packages.length} 个) --</option>`;
      data.packages.forEach(pkg => {
        const opt = document.createElement('option');
        opt.value = pkg;
        opt.innerText = pkg;
        dropdown.appendChild(opt);
      });
      dropdown.classList.remove('hidden');
      showToast(`已成功读取到 ${data.packages.length} 个第三方应用！`, '✓');
    } else {
      showToast('未检索到第三方应用或读取失败', '⚠️');
    }
  } catch (e) {
    showToast(`读取异常: ${e.message}`, '⚠️');
  }
}

async function executeDeployUninstallApp() {
  const pkg = document.getElementById('deploy-uninstall-pkg').value.trim();
  if (!pkg) {
    alert('请输入或选择要卸载的包名！');
    return;
  }
  const targets = getSelectedDeployTargets();
  if (targets.length === 0) {
    alert('请至少勾选一台目标头戴设备！');
    return;
  }

  if (!confirm(`确认要从 ${targets.length} 台设备上批量卸载应用【${pkg}】吗？此操作将清除该游戏应用及数据！`)) return;

  appendDeployLog(`🗑 开始批量卸载应用: ${pkg}，目标设备: ${targets.join(', ')}`);
  showToast(`正在向 ${targets.length} 台设备发送卸载指令...`, '🗑');

  try {
    const res = await fetch('/api/adb/uninstall', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ packageName: pkg, targets })
    });
    const data = await res.json();
    if (data.success && data.results) {
      data.results.forEach(r => {
        appendDeployLog(`设备 [${r.target}]: ${r.message}`, !r.success);
      });
      showToast('批量卸载执行完毕！', '✓');
    } else {
      appendDeployLog(`❌ 卸载失败: ${data.message || '未知错误'}`, true);
      showToast(`卸载出错: ${data.message}`, '⚠️');
    }
  } catch (e) {
    appendDeployLog(`❌ 卸载异常: ${e.message}`, true);
  }
}

async function executeDeployCreateFolder() {
  const remotePath = document.getElementById('deploy-push-remote').value.trim();
  if (!remotePath) {
    alert('请输入要在头显创建的远程目录绝对路径（如 /sdcard/Download/MyFolder/）！');
    return;
  }
  const targets = getSelectedDeployTargets();
  if (targets.length === 0) {
    alert('请至少勾选一台目标头戴设备！');
    return;
  }

  if (!confirm(`确认要在 ${targets.length} 台设备上创建远程目录【${remotePath}】吗？`)) return;

  appendDeployLog(`📁 正在批量创建远程目录: ${remotePath}，目标: ${targets.join(', ')}`);
  showToast('正在创建远程目录...', '📁');

  try {
    const res = await fetch('/api/adb/mkdir', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ remotePath, targets })
    });
    const data = await res.json();
    if (data.success && data.results) {
      data.results.forEach(r => {
        appendDeployLog(`设备 [${r.target}]: ${r.message}`, !r.success);
      });
      showToast('远程目录创建执行完毕！', '✓');
    } else {
      appendDeployLog(`❌ 创建目录失败: ${data.message || '未知错误'}`, true);
      showToast(`创建失败: ${data.message}`, '⚠️');
    }
  } catch (e) {
    appendDeployLog(`❌ 网络或服务端错误: ${e.message}`, true);
  }
}

async function executeDeployPushFile() {
  const fileInput = document.getElementById('deploy-push-file');
  const localPathInput = document.getElementById('deploy-push-localpath');
  const localPath = localPathInput ? localPathInput.value.trim() : '';
  const hasFile = fileInput.files && fileInput.files[0];

  if (!hasFile && !localPath) {
    alert('请选择要推送的文件/ZIP，或输入服务器本地文件/文件夹绝对路径！');
    return;
  }
  const remotePath = document.getElementById('deploy-push-remote').value.trim() || '/sdcard/Download/';
  const targets = getSelectedDeployTargets();
  if (targets.length === 0) {
    alert('请至少勾选一台目标头戴设备！');
    return;
  }
  const overwrite = document.getElementById('deploy-push-overwrite')?.checked ?? true;

  if (localPath) {
    // 方案 A：服务器本地路径极速直推（免上传·无文件大小限制）
    const itemName = localPath.replace(/\\/g, '/').split('/').filter(Boolean).pop();
    if (!confirm(`确认要将服务器本地【${itemName}】免上传直接推送到 ${targets.length} 台设备的【${remotePath}】存储目录吗？${overwrite ? ' (遇同名仅替换同名文件)' : ''}\n（路径: ${localPath}）`)) return;

    appendDeployLog(`📤 [本地直推] 开始批量推送: ${itemName} -> ${remotePath}，目标: ${targets.join(', ')}${overwrite ? ' [同名覆盖]' : ''}`);
    showToast(`正在直接推流【${itemName}】...`, '📤');

    try {
      const res = await fetch('/api/adb/local-push', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ localPath, remotePath, targets, overwrite })
      });
      const data = await res.json();
      if (data.success && data.results) {
        data.results.forEach(r => {
          appendDeployLog(`设备 [${r.target}]: ${r.message}`, !r.success);
        });
        showToast('批量本地推送执行完毕！', '✓');
      } else {
        appendDeployLog(`❌ 推送失败: ${data.message || '未知错误'}`, true);
        showToast(`推送出错: ${data.message}`, '⚠️');
      }
    } catch (e) {
      appendDeployLog(`❌ 推送异常: ${e.message}`, true);
    }
    return;
  }

  // 方案 B：传统网页文件上传推送
  const file = fileInput.files[0];
  if (!confirm(`确认要将【${file.name}】(${(file.size / 1024 / 1024).toFixed(1)}MB) 批量推送到 ${targets.length} 台设备的【${remotePath}】存储目录吗？${overwrite ? ' (遇同名仅替换同名文件)' : ''}`)) return;

  appendDeployLog(`📤 开始批量上传推送文件: ${file.name} -> ${remotePath}，目标: ${targets.join(', ')}${overwrite ? ' [同名覆盖]' : ''}`);
  showToast(`正在向 ${targets.length} 台设备推送文件...`, '📤');

  const formData = new FormData();
  formData.append('file', file);
  formData.append('remotePath', remotePath);
  formData.append('targets', targets.join(','));
  formData.append('overwrite', overwrite);

  try {
    const res = await fetch('/api/adb/upload-push', {
      method: 'POST',
      body: formData
    });
    const data = await res.json();
    if (data.success && data.results) {
      data.results.forEach(r => {
        appendDeployLog(`设备 [${r.target}]: ${r.message}`, !r.success);
      });
      showToast('批量文件推送执行完毕！', '✓');
    } else {
      appendDeployLog(`❌ 推送失败: ${data.message || '未知错误'}`, true);
      showToast(`推送出错: ${data.message}`, '⚠️');
    }
  } catch (e) {
    appendDeployLog(`❌ 推送异常: ${e.message}`, true);
  }
}

// ======================== 10. 远程 OTA 升级交互 ========================
let selectedOtaFile = null;

function openOtaModal() {
  document.getElementById('ota-modal').classList.remove('hidden');
  loadOtaVersion();
}

function closeOtaModal() {
  document.getElementById('ota-modal').classList.add('hidden');
}

async function loadOtaVersion() {
  try {
    const res = await fetch('/api/ota/version');
    if (res.ok) {
      const info = await res.json();
      document.getElementById('ota-current-ver').innerText = info.version || 'v1.2.0';
      document.getElementById('ota-build-time').innerText = `构建时间: ${info.buildTime || '2026-11-13'}`;
    }
  } catch (e) { }
}

function handleOtaFileSelected(input) {
  if (input.files && input.files[0]) {
    selectedOtaFile = input.files[0];
    document.getElementById('ota-file-label').innerText = `已选更新包: ${selectedOtaFile.name} (${(selectedOtaFile.size / 1024 / 1024).toFixed(2)} MB)`;
    document.getElementById('ota-upload-btn').disabled = false;
  }
}

function startOtaUpload() {
  if (!selectedOtaFile) {
    alert('请先选择 OTA 升级包 (.zip)！');
    return;
  }

  if (!confirm(`确认要执行远程 OTA 升级吗？升级包【${selectedOtaFile.name}】将被热解压应用并自动重启系统。`)) return;

  const progressBox = document.getElementById('ota-progress-box');
  const progressBar = document.getElementById('ota-progress-bar');
  const progressPct = document.getElementById('ota-progress-pct');
  const progressTitle = document.getElementById('ota-progress-title');
  const uploadBtn = document.getElementById('ota-upload-btn');

  progressBox.classList.remove('hidden');
  uploadBtn.disabled = true;

  const formData = new FormData();
  formData.append('file', selectedOtaFile);

  const xhr = new XMLHttpRequest();
  xhr.open('POST', apiUrl('/api/ota/upload'));

  xhr.upload.onprogress = (e) => {
    if (e.lengthComputable) {
      const pct = Math.round((e.loaded / e.total) * 100);
      progressBar.style.width = `${pct}%`;
      progressPct.innerText = `${pct}%`;
    }
  };

  xhr.onload = () => {
    if (xhr.status === 200) {
      progressTitle.innerText = 'OTA 升级包应用成功！正在热重启服务...';
      pollOtaReboot();
    } else {
      alert(`OTA 升级失败: ${xhr.responseText}`);
      uploadBtn.disabled = false;
    }
  };

  xhr.onerror = () => {
    // 某些极端情况下服务退出时可能立即触发 error，也当作重启中处理
    pollOtaReboot();
  };

  xhr.send(formData);
}

async function startOtaFromUrl() {
  const url = document.getElementById('ota-url-input').value.trim();
  if (!url) {
    alert('请输入远程 OTA 下载链接！');
    return;
  }

  if (!confirm(`确认从远程链接【${url}】下载并执行 OTA 升级吗？`)) return;

  const progressBox = document.getElementById('ota-progress-box');
  const progressTitle = document.getElementById('ota-progress-title');
  progressBox.classList.remove('hidden');
  progressTitle.innerText = '正在远程下载 OTA 升级包并应用，请稍候...';

  try {
    const res = await fetch('/api/ota/download', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url })
    });
    if (res.ok) {
      pollOtaReboot();
    } else {
      const err = await res.json();
      alert(`OTA 失败: ${err.message || '下载出错'}`);
    }
  } catch (e) {
    pollOtaReboot();
  }
}

function pollOtaReboot() {
  const progressTitle = document.getElementById('ota-progress-title');
  const progressHint = document.getElementById('ota-progress-hint');
  let countdown = 6;

  const timer = setInterval(async () => {
    countdown--;
    if (progressTitle) progressTitle.innerText = `系统正在热重载重启生效... (${countdown}s)`;
    if (countdown <= 0) {
      clearInterval(timer);
      if (progressHint) progressHint.innerText = '服务已重启，正在重新连接并刷新页面...';
      // 循环重连探测
      for (let i = 0; i < 20; i++) {
        try {
          const res = await fetch('/api/ota/version');
          if (res.ok) {
            window.location.reload();
            return;
          }
        } catch (e) { }
        await new Promise(r => setTimeout(r, 1000));
      }
      window.location.reload();
    }
  }, 1000);
}

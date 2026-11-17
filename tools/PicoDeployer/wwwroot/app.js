// ==================== PicoDeployer 前端交互逻辑 ====================

let devices = [];
let selectedDeviceNos = new Set();
let progressInterval = null;
let currentTab = 'install';
let lastProgressMap = new Map(); // 缓存各设备的最新进度，防止切换选项卡或选中时丢失

const DEFAULT_SAMPLE_TEMPLATE = `{
  "machine_id": "{{DEVICE_NO}}",
  "machine_name": "{{DEVICE_ALIAS}}",
  "device_ip": "{{DEVICE_IP}}",
  "server_url": "http://192.168.1.100:8999",
  "seat_id": {{SEAT_ID}},
  "role": "{{ROLE}}",
  "stream_port": {{SERVER_PORT}},
  "last_synced": "{{DATETIME}}"
}`;

// 初始化
window.addEventListener('DOMContentLoaded', async () => {
  document.getElementById('config-template-editor').value = DEFAULT_SAMPLE_TEMPLATE;
  await loadDevices();
  startProgressPolling();
});

// 选项卡切换
function switchTab(tab) {
  currentTab = tab;
  document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.className = 'tab-btn px-4 py-2.5 rounded-t-xl bg-slate-800 text-slate-400 hover:text-slate-200 flex items-center gap-2 transition';
  });
  document.querySelectorAll('.tab-content').forEach(content => {
    content.classList.add('hidden');
  });

  const activeBtn = document.getElementById(`tab-btn-${tab}`);
  const activeContent = document.getElementById(`tab-content-${tab}`);
  if (activeBtn) activeBtn.className = 'tab-btn px-4 py-2.5 rounded-t-xl bg-indigo-600 text-white flex items-center gap-2 transition';
  if (activeContent) activeContent.classList.remove('hidden');

  updateStats();
}

// 加载设备列表
async function loadDevices() {
  try {
    const res = await fetch('/api/devices');
    if (res.ok) {
      devices = await res.json();
      
      // 规范化名称并校验已选机位集合，剔除已删除的设备
      const validDeviceNos = new Set(devices.map(d => d.deviceNo));
      selectedDeviceNos = new Set([...selectedDeviceNos].filter(id => validDeviceNos.has(id)));

      // 初次加载时默认全选所有机位
      if (selectedDeviceNos.size === 0 && devices.length > 0) {
        devices.forEach(d => selectedDeviceNos.add(d.deviceNo));
      }

      updateStats();
      renderDeviceGrids();
      renderDeviceTable();
      updatePullDeviceSelect();
    }
  } catch (e) {
    showToast('获取设备列表失败: ' + e.message, '⚠️');
  }
}

// 主动刷新所有设备在线与电池状态
async function refreshAllDevices() {
  const pill = document.getElementById('connection-status-pill');
  pill.innerHTML = `<span class="w-2 h-2 rounded-full bg-amber-400 animate-ping"></span><span>正在探测无线连接...</span>`;
  showToast('正在向所有设备发送无线 ADB 探测指令...', '🔄');

  try {
    const res = await fetch('/api/devices/refresh', { method: 'POST' });
    if (res.ok) {
      devices = await res.json();
      updateStats();
      renderDeviceGrids();
      renderDeviceTable();
      updatePullDeviceSelect();
      pill.innerHTML = `<span class="w-2 h-2 rounded-full bg-emerald-400 animate-ping"></span><span>ADB 服务就绪 (TCP 5555)</span>`;
      showToast('设备状态探测完成！', '✅');
    }
  } catch (e) {
    showToast('探测失败: ' + e.message, '❌');
    pill.innerHTML = `<span class="w-2 h-2 rounded-full bg-rose-400"></span><span>探测超时</span>`;
  }
}

// 统计指标
function updateStats() {
  const total = devices.length;
  const online = devices.filter(d => d.isConnected).length;
  // 严格统计当前真实存在的已勾选机位数
  const validSelected = devices.filter(d => selectedDeviceNos.has(d.deviceNo)).length;

  document.getElementById('stat-total').innerText = total;
  document.getElementById('stat-online').innerText = online;
  document.getElementById('stat-selected').innerText = validSelected;
}

// 渲染各网格中机位卡片
function renderDeviceGrids() {
  renderGrid('device-install-grid');
  renderGrid('device-config-grid');
  renderGrid('device-file-grid');
}

function renderGrid(containerId) {
  const container = document.getElementById(containerId);
  if (!container) return;
  container.innerHTML = '';

  if (devices.length === 0) {
    container.innerHTML = `
      <div class="col-span-full py-8 text-center text-slate-500 bg-slate-800/40 rounded-xl border border-dashed border-slate-700">
        暂无登记设备，请在「设备与自定义变量管理」中录入机号与 IP
      </div>
    `;
    return;
  }

  devices.forEach(dev => {
    const displayName = dev.displayName || dev.alias || (dev.deviceNo ? dev.deviceNo + '号机' : dev.ip);
    const isSelected = selectedDeviceNos.has(dev.deviceNo);
    const card = document.createElement('div');
    card.setAttribute('data-card-dev', dev.deviceNo);
    card.className = `rounded-xl border p-3.5 flex flex-col justify-between transition-all select-none cursor-pointer ${
      isSelected ? 'bg-slate-800/90 border-indigo-500/80 shadow-lg shadow-indigo-950/40' : 'bg-slate-800/40 border-slate-700/60 opacity-60'
    }`;

    const onlineBadge = dev.isConnected
      ? `<span class="text-[10px] px-1.5 py-0.5 rounded bg-emerald-500/20 text-emerald-300 border border-emerald-500/30 font-bold">🟢在线</span>`
      : `<span class="text-[10px] px-1.5 py-0.5 rounded bg-slate-700 text-slate-400 border border-slate-600">⚪离线</span>`;

    const batText = dev.battery > 0 ? `🔋 ${dev.battery}%` : '🔋 --';

    // 检查是否有历史进度缓存
    const cachedProg = lastProgressMap.get(dev.deviceNo);
    const progMsg = cachedProg ? cachedProg.message : '待命中';
    const progPct = cachedProg ? cachedProg.percent : 0;
    const barClass = cachedProg && cachedProg.stage === 'Success' 
      ? 'h-full bg-emerald-500 transition-all duration-300 rounded-full'
      : (cachedProg && cachedProg.stage === 'Failed' 
          ? 'h-full bg-rose-500 transition-all duration-300 rounded-full' 
          : 'h-full bg-indigo-500 transition-all duration-300 rounded-full');

    card.innerHTML = `
      <div>
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-2">
            <input type="checkbox" data-chk-dev="${dev.deviceNo}" ${isSelected ? 'checked' : ''} class="w-4 h-4 rounded text-indigo-600 accent-indigo-600 cursor-pointer pointer-events-none">
            <span class="font-bold text-sm text-white font-mono">${displayName}</span>
          </div>
          <div class="flex items-center gap-1.5">
            ${onlineBadge}
            <span class="text-[11px] font-mono text-slate-400">${batText}</span>
          </div>
        </div>

        <div class="mt-2 text-[11px] text-slate-400 font-mono flex items-center justify-between">
          <span>IP: ${dev.ip}:${dev.port}</span>
          <span class="text-slate-500 text-[10px]">${dev.model || 'PICO'}</span>
        </div>

        <!-- SN 码醒目展示与一键复制 -->
        <div class="mt-2 py-1 px-2 rounded-lg bg-slate-900/90 border border-slate-700/80 flex items-center justify-between gap-1">
          <div class="flex items-center gap-1.5 overflow-hidden">
            <span class="text-[10px] text-slate-400 font-sans">SN:</span>
            <span class="text-xs font-mono font-bold text-cyan-300 tracking-wider truncate select-all" title="${dev.sn || '待连接 ADB 自动读取'}">${dev.sn || '<span class="text-slate-500 font-normal">连接ADB自动读取</span>'}</span>
          </div>
          ${dev.sn 
            ? `<button onclick="event.stopPropagation(); copyToClipboard('${dev.sn}')" class="text-[10px] px-2 py-0.5 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 hover:text-white border border-slate-600 transition flex items-center gap-1 active:scale-95 shadow-sm" title="一键复制完整 SN">📋 复制</button>` 
            : `<button onclick="event.stopPropagation(); readSingleDeviceSn('${dev.deviceNo}')" class="text-[10px] px-2 py-0.5 rounded bg-indigo-600 hover:bg-indigo-500 text-white transition flex items-center gap-1 active:scale-95 shadow-sm" title="立即通过 ADB 探测读取 SN">🔍 读取</button>`}
        </div>
      </div>

      <!-- 实时进度条与状态文字 (采用 data-* 属性定位，多网格全同步) -->
      <div class="mt-3 pt-2 border-t border-slate-700/60">
        <div class="flex items-center justify-between text-[11px] mb-1">
          <span class="text-slate-400 truncate max-w-[170px]" data-prog-msg="${dev.deviceNo}">${progMsg}</span>
          <span class="font-mono font-bold text-indigo-400" data-prog-pct="${dev.deviceNo}">${progPct}%</span>
        </div>
        <div class="w-full h-1.5 bg-slate-900 rounded-full overflow-hidden">
          <div data-prog-bar="${dev.deviceNo}" class="${barClass}" style="width: ${progPct}%"></div>
        </div>
      </div>
    `;

    card.onclick = (e) => {
      if (e.target.closest('button')) return;
      toggleSelectDevice(dev.deviceNo);
    };

    container.appendChild(card);
  });
}

// 单击勾选/反选设备卡片 (原地更新 DOM 样式与复选框，绝对不销毁 DOM，保持进度条稳定)
function toggleSelectDevice(deviceNo) {
  if (selectedDeviceNos.has(deviceNo)) {
    selectedDeviceNos.delete(deviceNo);
  } else {
    selectedDeviceNos.add(deviceNo);
  }
  updateStats();
  updateSingleCardStyle(deviceNo);
}

function updateSingleCardStyle(deviceNo) {
  const isSelected = selectedDeviceNos.has(deviceNo);
  document.querySelectorAll(`[data-card-dev="${deviceNo}"]`).forEach(card => {
    const chk = card.querySelector(`[data-chk-dev="${deviceNo}"]`);
    if (chk) chk.checked = isSelected;
    if (isSelected) {
      card.classList.add('bg-slate-800/90', 'border-indigo-500/80', 'shadow-lg', 'shadow-indigo-950/40');
      card.classList.remove('bg-slate-800/40', 'border-slate-700/60', 'opacity-60');
    } else {
      card.classList.remove('bg-slate-800/90', 'border-indigo-500/80', 'shadow-lg', 'shadow-indigo-950/40');
      card.classList.add('bg-slate-800/40', 'border-slate-700/60', 'opacity-60');
    }
  });
}

function updateAllCardsStyle() {
  devices.forEach(d => updateSingleCardStyle(d.deviceNo));
}

function selectAllDevices(all) {
  selectedDeviceNos.clear();
  if (all) {
    devices.forEach(d => selectedDeviceNos.add(d.deviceNo));
  }
  updateStats();
  updateAllCardsStyle();
}

function selectOnlineOnly() {
  selectedDeviceNos.clear();
  devices.filter(d => d.isConnected).forEach(d => selectedDeviceNos.add(d.deviceNo));
  updateStats();
  updateAllCardsStyle();
}

// 复制文字到剪贴板
async function copyToClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
    showToast(`已成功复制 SN: ${text}`, '📋');
  } catch (err) {
    // 兼容传统退化模式
    const ta = document.createElement('textarea');
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    document.body.removeChild(ta);
    showToast(`已复制: ${text}`, '📋');
  }
}

// 渲染设备管理表格
function renderDeviceTable() {
  const tbody = document.getElementById('device-table-body');
  if (!tbody) return;
  tbody.innerHTML = '';

  devices.forEach(dev => {
    const displayName = dev.displayName || dev.alias || (dev.deviceNo ? dev.deviceNo + '号机' : dev.ip);
    const tr = document.createElement('tr');
    tr.className = 'hover:bg-slate-800/50 transition';

    const onlineSpan = dev.isConnected 
      ? '<span class="text-emerald-400">🟢 在线就绪</span>' 
      : '<span class="text-slate-500">⚪ 离线未连</span>';

    const customVarsText = dev.customVars && Object.keys(dev.customVars).length > 0
      ? Object.entries(dev.customVars).map(([k, v]) => `${k}=${v}`).join(', ')
      : '<span class="text-slate-500">无</span>';

    tr.innerHTML = `
      <td class="p-3 font-bold text-white font-mono">${displayName}</td>
      <td class="p-3 text-cyan-400 font-mono">${dev.ip}:${dev.port}</td>
      <td class="p-3">${onlineSpan}</td>
      <td class="p-3 text-slate-300 font-mono">${dev.battery > 0 ? dev.battery + '%' : '--'}</td>
      <td class="p-3">
        <div class="text-slate-300 text-xs">${dev.model || 'PICO 4'}</div>
        <div class="text-cyan-300 font-mono font-bold text-xs flex items-center gap-1.5 mt-0.5">
          <span>${dev.sn || '<span class="text-slate-500 font-normal">待读取</span>'}</span>
          ${dev.sn 
            ? `<button onclick="copyToClipboard('${dev.sn}')" class="text-[10px] px-1.5 py-0.5 rounded bg-slate-800 hover:bg-slate-700 text-slate-300 border border-slate-600 transition" title="复制完整 SN 码">复制</button>` 
            : `<button onclick="readSingleDeviceSn('${dev.deviceNo}')" class="text-[10px] px-1.5 py-0.5 rounded bg-indigo-600 hover:bg-indigo-500 text-white transition" title="立即通过 ADB 探测读取 SN">🔍 读取</button>`}
        </div>
      </td>
      <td class="p-3 text-emerald-300 max-w-xs truncate font-mono" title="${customVarsText}">${customVarsText}</td>
      <td class="p-3 text-right space-x-2">
        <button onclick="editDevice('${dev.deviceNo}')" class="text-indigo-400 hover:text-indigo-300">编辑</button>
        <button onclick="deviceAction('${dev.deviceNo}', 'reboot')" class="text-amber-400 hover:text-amber-300">重启</button>
        <button onclick="deleteDevice('${dev.deviceNo}')" class="text-rose-400 hover:text-rose-300">删除</button>
      </td>
    `;
    tbody.appendChild(tr);
  });
}

// 更新“读取设备配置”下拉选项
function updatePullDeviceSelect() {
  const sel = document.getElementById('pull-device-select');
  if (!sel) return;
  sel.innerHTML = '';
  devices.forEach(d => {
    const opt = document.createElement('option');
    opt.value = d.deviceNo;
    const name = d.displayName || d.alias || (d.deviceNo ? d.deviceNo + '号机' : d.ip);
    opt.innerText = `${name} (${d.ip})`;
    sel.appendChild(opt);
  });
}

// 从选中头显远程读取现有配置文件内容
async function pullConfigFromSelectedDevice() {
  const select = document.getElementById('pull-device-select');
  const deviceNo = select.value;
  const remoteTargetPath = document.getElementById('config-remote-target').value.trim();

  if (!deviceNo) {
    showToast('请先选择要读取的目标设备', '⚠️');
    return;
  }
  if (!remoteTargetPath) {
    showToast('请先填写目标配置文件路径', '⚠️');
    return;
  }

  showToast(`正在从 [${select.options[select.selectedIndex]?.text || deviceNo}] 远程读取配置...`, '🔄');

  try {
    const res = await fetch('/api/config/pull', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceNo, remoteTargetPath })
    });
    const data = await res.json();
    if (res.ok && data.success) {
      document.getElementById('config-template-editor').value = data.content;
      showToast(`已成功读取 [${data.displayName}] 的当前配置文件！`, '✅');
    } else {
      showToast(data.error || '读取失败', '❌');
    }
  } catch (e) {
    showToast('网络请求异常: ' + e.message, '❌');
  }
}

// ==================== 批量安装 APK ====================
async function startBatchInstall() {
  const apkPath = document.getElementById('apk-file-path').value.trim();
  if (!apkPath) {
    showToast('请先输入本地 APK 文件的绝对路径', '⚠️');
    return;
  }
  if (selectedDeviceNos.size === 0) {
    showToast('请至少勾选一台目标机位', '⚠️');
    return;
  }

  const concurrency = parseInt(document.getElementById('install-concurrency-slider').value) || 3;
  const autoLaunch = document.getElementById('install-opt-launch').checked;
  const packageName = document.getElementById('install-opt-package').value.trim();

  const req = {
    apkPath,
    targetDevices: Array.from(selectedDeviceNos),
    concurrency,
    autoLaunch,
    packageName
  };

  try {
    const res = await fetch('/api/deploy/install', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req)
    });
    const data = await res.json();
    if (res.ok) {
      showToast(`已开始向 ${data.targetCount} 台设备并发安装 APK！`, '🚀');
      document.getElementById('stat-queue').innerText = '安装中...';
    } else {
      showToast('启动安装失败: ' + data.error, '❌');
    }
  } catch (e) {
    showToast('请求异常: ' + e.message, '❌');
  }
}

// ==================== 批量下发差异化配置 ====================
async function startBatchConfigPush() {
  const templateContent = document.getElementById('config-template-editor').value.trim();
  const remoteTargetPath = document.getElementById('config-remote-target').value.trim();

  if (!templateContent) {
    showToast('配置模板内容不能为空', '⚠️');
    return;
  }
  if (!remoteTargetPath) {
    showToast('目标存储路径不能为空', '⚠️');
    return;
  }
  if (selectedDeviceNos.size === 0) {
    showToast('请至少勾选一台目标机位', '⚠️');
    return;
  }

  const req = {
    templateContent,
    remoteTargetPath,
    targetDevices: Array.from(selectedDeviceNos),
    concurrency: 4
  };

  try {
    const res = await fetch('/api/config/push', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req)
    });
    const data = await res.json();
    if (res.ok) {
      showToast(`已开始向 ${data.targetCount} 台设备动态渲染并下发差异化配置！`, '🚀');
      document.getElementById('stat-queue').innerText = '配置替换中...';
    } else {
      showToast('下发失败: ' + data.error, '❌');
    }
  } catch (e) {
    showToast('请求异常: ' + e.message, '❌');
  }
}

// ==================== 批量推流大文件 ====================
async function startBatchFilePush() {
  const localFilePath = document.getElementById('file-local-path').value.trim();
  const remoteDirPath = document.getElementById('file-remote-dir').value.trim();

  if (!localFilePath) {
    showToast('请填写本地视频/文件路径', '⚠️');
    return;
  }
  if (selectedDeviceNos.size === 0) {
    showToast('请勾选目标机位', '⚠️');
    return;
  }

  const req = {
    localFilePath,
    remoteDirPath,
    targetDevices: Array.from(selectedDeviceNos),
    concurrency: 3
  };

  try {
    const res = await fetch('/api/deploy/push-file', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req)
    });
    const data = await res.json();
    if (res.ok) {
      showToast(`已开始向 ${data.targetCount} 台设备推送文件！`, '🚀');
      document.getElementById('stat-queue').innerText = '推流中...';
    } else {
      showToast('启动推送失败: ' + data.error, '❌');
    }
  } catch (e) {
    showToast('请求异常: ' + e.message, '❌');
  }
}

// 取消当前任务
async function cancelCurrentTask() {
  try {
    await fetch('/api/deploy/cancel', { method: 'POST' });
    showToast('已下发取消指令', '⚠️');
  } catch (e) { }
}

// 宏变量插入
function insertVar(val) {
  const editor = document.getElementById('config-template-editor');
  const start = editor.selectionStart;
  const end = editor.selectionEnd;
  const text = editor.value;
  editor.value = text.substring(0, start) + val + text.substring(end);
  editor.focus();
  editor.selectionStart = editor.selectionEnd = start + val.length;
}

function loadDefaultTemplate() {
  document.getElementById('config-template-editor').value = DEFAULT_SAMPLE_TEMPLATE;
  showToast('已载入标准 JSON 示范模板');
}

// 预览差异化配置弹窗
function openPreviewModal() {
  const modal = document.getElementById('preview-modal');
  const select = document.getElementById('preview-device-select');
  select.innerHTML = '';

  devices.forEach(d => {
    const opt = document.createElement('option');
    opt.value = d.deviceNo;
    const name = d.displayName || d.alias || (d.deviceNo ? d.deviceNo + '号机' : d.ip);
    opt.innerText = `${name} (${d.ip})`;
    select.appendChild(opt);
  });

  modal.classList.remove('hidden');
  fetchRenderedPreview();
}

function closePreviewModal() {
  document.getElementById('preview-modal').classList.add('hidden');
}

async function fetchRenderedPreview() {
  const select = document.getElementById('preview-device-select');
  const deviceNo = select.value;
  const templateContent = document.getElementById('config-template-editor').value;

  try {
    const res = await fetch('/api/config/preview', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ templateContent, deviceNo })
    });
    if (res.ok) {
      const data = await res.json();
      document.getElementById('preview-rendered-code').innerText = data.renderedContent;
      document.getElementById('preview-device-info').innerText = `已成功解析设备 [${data.displayName}] 的专属占位符`;
    }
  } catch (e) {
    document.getElementById('preview-rendered-code').innerText = '预览渲染出错: ' + e.message;
  }
}

// 实时任务进度轮询 (1.2 秒轮询一次)
function startProgressPolling() {
  if (progressInterval) clearInterval(progressInterval);
  progressInterval = setInterval(async () => {
    try {
      const res = await fetch('/api/deploy/progress');
      if (res.ok) {
        const data = await res.json();
        if (data.isBusy) {
          document.getElementById('stat-queue').innerText = '正在执行中';
        } else {
          document.getElementById('stat-queue').innerText = '待命中';
        }

        if (data.progress && data.progress.length > 0) {
          data.progress.forEach(p => {
            lastProgressMap.set(p.deviceNo, p);
            updateDeviceProgressBar(p);
          });
        }
      }
    } catch (e) { }
  }, 1200);
}

// 全网格同步更新指定机位的进度展示
function updateDeviceProgressBar(p) {
  // 更新所有匹配当前机号的信息与进度条
  document.querySelectorAll(`[data-prog-msg="${p.deviceNo}"]`).forEach(el => {
    el.innerText = p.message;
  });

  document.querySelectorAll(`[data-prog-pct="${p.deviceNo}"]`).forEach(el => {
    el.innerText = `${p.percent}%`;
  });

  document.querySelectorAll(`[data-prog-bar="${p.deviceNo}"]`).forEach(bar => {
    bar.style.width = `${p.percent}%`;
    if (p.stage === 'Success') {
      bar.className = 'h-full bg-emerald-500 transition-all duration-300 rounded-full';
    } else if (p.stage === 'Failed') {
      bar.className = 'h-full bg-rose-500 transition-all duration-300 rounded-full';
    } else {
      bar.className = 'h-full bg-indigo-500 transition-all duration-300 rounded-full';
    }
  });
}

// 设备管理模态框
function openDeviceModal(editDeviceNo = null) {
  const modal = document.getElementById('device-modal');
  modal.classList.remove('hidden');

  if (editDeviceNo) {
    const dev = devices.find(d => d.deviceNo === editDeviceNo);
    if (dev) {
      const name = dev.displayName || dev.alias || (dev.deviceNo ? dev.deviceNo + '号机' : dev.ip);
      document.getElementById('modal-device-title').innerHTML = `<span>✏️</span> 编辑设备 [${name}]`;
      document.getElementById('modal-input-no').value = dev.deviceNo;
      document.getElementById('modal-input-alias').value = dev.alias;
      document.getElementById('modal-input-ip').value = dev.ip;
      document.getElementById('modal-input-port').value = dev.port;
      document.getElementById('modal-input-sn').value = dev.sn || '';
      document.getElementById('modal-input-sn').placeholder = dev.sn ? dev.sn : '待连接无线 ADB 自动探测读取...';
      document.getElementById('modal-input-vars').value = JSON.stringify(dev.customVars || {}, null, 2);
      return;
    }
  }

  document.getElementById('modal-device-title').innerHTML = `<span>➕</span> 添加新头戴设备`;
  document.getElementById('modal-input-no').value = '';
  document.getElementById('modal-input-alias').value = '';
  document.getElementById('modal-input-ip').value = '';
  document.getElementById('modal-input-port').value = '5555';
  document.getElementById('modal-input-sn').value = '';
  document.getElementById('modal-input-sn').placeholder = '无需手动填写 · 保存后连接 ADB 自动识别读取';
  document.getElementById('modal-input-vars').value = '{"SEAT_ID": "1", "ROLE": "Player"}';
}

function closeDeviceModal() {
  document.getElementById('device-modal').classList.add('hidden');
}

async function saveDeviceModal() {
  const deviceNo = document.getElementById('modal-input-no').value.trim();
  const alias = document.getElementById('modal-input-alias').value.trim();
  const ip = document.getElementById('modal-input-ip').value.trim();
  const port = parseInt(document.getElementById('modal-input-port').value) || 5555;
  const sn = document.getElementById('modal-input-sn').value.trim();
  const varsStr = document.getElementById('modal-input-vars').value.trim();

  if (!deviceNo && !ip) {
    showToast('机号和 IP 地址至少需填写一项', '⚠️');
    return;
  }

  let customVars = {};
  if (varsStr) {
    try {
      customVars = JSON.parse(varsStr);
    } catch (e) {
      showToast('自定义变量必须为合法的 JSON 格式', '⚠️');
      return;
    }
  }

  const dev = {
    deviceNo,
    alias,
    displayName: alias || (deviceNo ? deviceNo + '号机' : ip),
    ip,
    port,
    sn,
    customVars
  };

  showToast('正在保存设备，并尝试连接无线 ADB 读取硬件 SN 码...', '🔄');

  try {
    const res = await fetch('/api/devices/upsert', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(dev)
    });
    const data = await res.json();
    if (res.ok) {
      closeDeviceModal();
      showToast(data.message || '设备信息已保存！', '✅');
      await loadDevices();
    } else {
      showToast(data.error || '保存失败', '❌');
    }
  } catch (e) {
    showToast('保存异常: ' + e.message, '❌');
  }
}

// 主动通过无线 ADB 探测读取单台设备的硬件 SN 码
async function readSingleDeviceSn(deviceNo) {
  showToast(`正在通过无线 ADB 探测设备 [${deviceNo}] 的硬件 SN 码...`, '🔄');
  try {
    const res = await fetch(`/api/devices/${deviceNo}/read-sn`, { method: 'POST' });
    const data = await res.json();
    if (res.ok && data.success) {
      showToast(data.message || `已成功读取 SN: ${data.sn}`, '✅');
      await loadDevices();
    } else {
      showToast(data.message || data.error || '未读取到 SN，请确认设备已开机并在同一局域网', '⚠️');
      await loadDevices();
    }
  } catch (e) {
    showToast('读取 SN 失败: ' + e.message, '❌');
  }
}

function editDevice(deviceNo) {
  openDeviceModal(deviceNo);
}

async function deleteDevice(deviceNo) {
  if (!confirm(`确认删除机号为 [${deviceNo}] 的设备记录吗？`)) return;
  try {
    const res = await fetch(`/api/devices/${deviceNo}`, { method: 'DELETE' });
    if (res.ok) {
      selectedDeviceNos.delete(deviceNo);
      showToast('已删除设备', '🗑️');
      await loadDevices();
    }
  } catch (e) {
    showToast('删除失败: ' + e.message, '❌');
  }
}

async function deviceAction(deviceNo, action) {
  try {
    const res = await fetch(`/api/devices/${deviceNo}/action?action=${action}`, { method: 'POST' });
    if (res.ok) {
      const data = await res.json();
      showToast(data.message || '指令已下发', '⚡');
    }
  } catch (e) {
    showToast('操作失败: ' + e.message, '❌');
  }
}

// 浮动通知 Toast
function showToast(msg, icon = 'ℹ️') {
  const toast = document.getElementById('toast');
  document.getElementById('toast-icon').innerText = icon;
  document.getElementById('toast-msg').innerText = msg;
  toast.classList.remove('translate-y-20', 'opacity-0');
  setTimeout(() => {
    toast.classList.add('translate-y-20', 'opacity-0');
  }, 3200);
}

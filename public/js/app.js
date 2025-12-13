/**
 * 设备统一适配器 - 前端应用
 */

// ===== 全局状态 =====
const state = {
  devices: [],
  currentDevice: null,
  waveforms: {},
  events: [],
  connected: false,
  ws: null
};

// ===== API 配置 =====
const API_BASE = window.location.origin;
const WS_URL = `ws://${window.location.host}`;

// ===== 工具函数 =====
function $(selector) {
  return document.querySelector(selector);
}

function $$(selector) {
  return document.querySelectorAll(selector);
}

function showToast(message, type = 'info') {
  const container = $('#toast-container');
  const icons = {
    success: 'fa-check-circle',
    error: 'fa-times-circle',
    warning: 'fa-exclamation-triangle',
    info: 'fa-info-circle'
  };
  
  const toast = document.createElement('div');
  toast.className = `toast ${type}`;
  toast.innerHTML = `
    <i class="fas ${icons[type]}"></i>
    <span>${message}</span>
  `;
  
  container.appendChild(toast);
  
  setTimeout(() => {
    toast.style.animation = 'slideIn 0.3s ease reverse';
    setTimeout(() => toast.remove(), 300);
  }, 3000);
}

// ===== API 请求 =====
async function api(endpoint, options = {}) {
  try {
    const response = await fetch(`${API_BASE}${endpoint}`, {
      ...options,
      headers: {
        'Content-Type': 'application/json',
        ...options.headers
      }
    });
    
    const data = await response.json();
    
    if (!response.ok) {
      throw new Error(data.message || 'API 请求失败');
    }
    
    return data;
  } catch (error) {
    console.error('API Error:', error);
    throw error;
  }
}

// ===== WebSocket 连接 =====
function connectWebSocket() {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) {
    return;
  }
  
  state.ws = new WebSocket(WS_URL);
  
  state.ws.onopen = () => {
    state.connected = true;
    updateConnectionStatus(true);
    showToast('WebSocket 已连接', 'success');
    
    // 订阅设备更新和事件触发
    state.ws.send(JSON.stringify({
      type: 'subscribe',
      events: ['device:status', 'device:feedback', 'device:strength', 'event:triggered', '*']
    }));
  };
  
  state.ws.onclose = () => {
    state.connected = false;
    updateConnectionStatus(false);
    showToast('WebSocket 连接断开，3秒后重连...', 'warning');
    setTimeout(connectWebSocket, 3000);
  };
  
  state.ws.onerror = (error) => {
    console.error('WebSocket Error:', error);
    showToast('WebSocket 连接错误', 'error');
  };
  
  state.ws.onmessage = (event) => {
    try {
      const data = JSON.parse(event.data);
      handleWebSocketMessage(data);
    } catch (error) {
      console.error('WebSocket message parse error:', error);
    }
  };
}

function handleWebSocketMessage(data) {
  // 处理 event 类型的包装格式
  const msgType = data.type === 'event' ? data.event : data.type;
  const msgData = data.type === 'event' ? data.data : data;
  
  switch (msgType) {
    case 'device:status':
      updateDeviceStatus(msgData.deviceId, msgData.status);
      break;
      
    case 'device:strength':
      updateStrengthDisplay(msgData.deviceId, msgData.channel, msgData.value);
      break;
      
    case 'device:feedback':
      showToast(`设备反馈: ${msgData.message}`, 'info');
      break;
      
    case 'device:connected':
      showToast(`设备 ${msgData.deviceId} 已连接`, 'success');
      loadDevices();
      break;
      
    case 'device:disconnected':
      showToast(`设备 ${msgData.deviceId} 已断开`, 'warning');
      loadDevices();
      break;
    
    case 'event:triggered':
      // 记录事件触发日志
      handleEventTriggered(msgData);
      break;
      
    default:
      console.log('Unknown WS message:', data);
  }
}

function updateConnectionStatus(connected) {
  const statusEl = $('.connection-status');
  if (connected) {
    statusEl.classList.add('connected');
    statusEl.innerHTML = '<i class="fas fa-circle"></i> 服务器已连接';
  } else {
    statusEl.classList.remove('connected');
    statusEl.innerHTML = '<i class="fas fa-circle"></i> 服务器未连接';
  }
}

// ===== 页面导航 =====
function initNavigation() {
  const navItems = $$('.nav-item');
  const pages = $$('.page');
  
  navItems.forEach(item => {
    item.addEventListener('click', () => {
      const pageId = item.dataset.page;
      
      // 更新导航状态
      navItems.forEach(nav => nav.classList.remove('active'));
      item.classList.add('active');
      
      // 切换页面 - 使用 page- 前缀
      pages.forEach(page => page.classList.remove('active'));
      const targetPage = $(`#page-${pageId}`);
      if (targetPage) {
        targetPage.classList.add('active');
      }
      
      // 加载页面数据
      if (pageId === 'dashboard') loadDashboard();
      if (pageId === 'devices') loadDevices();
      if (pageId === 'control') loadControlPage();
      if (pageId === 'events') loadEvents();
      if (pageId === 'scripts') loadScripts();
    });
  });
}

// ===== 设备类型标签切换 =====
function initDeviceTypeTabs() {
  const tabs = $$('.tab-btn');
  const forms = $$('.device-form');
  
  tabs.forEach(tab => {
    tab.addEventListener('click', () => {
      const type = tab.dataset.type;
      
      tabs.forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      
      forms.forEach(form => form.classList.remove('active'));
      $(`#form-${type}`).classList.add('active');
    });
  });
}

// ===== 仪表盘 =====
async function loadDashboard() {
  try {
    // 获取设备列表
    const devices = await api('/api/devices');
    state.devices = devices.data || [];
    
    // 更新统计
    const connectedCount = state.devices.filter(d => d.status === 'connected').length;
    $('#stat-devices').textContent = state.devices.length;
    $('#stat-connected').textContent = connectedCount;
    
    // 获取激活的电击规则数量
    try {
      const eventsResult = await api('/api/events');
      const events = eventsResult.data || [];
      const enabledEvents = events.filter(e => e.enabled);
      $('#stat-events').textContent = enabledEvents.length;
    } catch (e) {
      console.warn('获取事件数量失败', e);
    }
    
    // 获取运行中的脚本数量
    try {
      const scriptsResult = await api('/api/scripts');
      const scripts = scriptsResult.data || [];
      const runningScripts = scripts.filter(s => s.running);
      $('#stat-scripts').textContent = runningScripts.length;
    } catch (e) {
      console.warn('获取脚本数量失败', e);
    }
    
    // 更新设备强度预览
    renderDeviceStrengthPreview();
    
    // 更新 OCR 概览
    await loadDashboardOCRStatus();
    
    // 更新快速控制列表
    renderQuickDeviceList();
    
  } catch (error) {
    showToast('加载仪表盘失败', 'error');
  }
}

// 渲染设备通道强度预览
function renderDeviceStrengthPreview() {
  const container = $('#device-strength-preview');
  if (!container) return;
  
  const connectedDevices = state.devices.filter(d => d.status === 'connected');
  
  if (connectedDevices.length === 0) {
    container.innerHTML = `
      <div class="text-muted text-sm" style="padding: 20px; text-align: center;">
        暂无已连接设备
      </div>
    `;
    return;
  }
  
  container.innerHTML = connectedDevices.map(device => {
    const state = device.state || { strengthA: 0, strengthB: 0, limitA: 200, limitB: 200 };
    const typeIcon = device.type === 'dglab' ? 'fa-bolt' : 'fa-mobile-alt';
    const typeName = device.type === 'dglab' ? '郊狼' : '役次元';
    
    return `
      <div class="device-strength-card">
        <div class="device-strength-header">
          <i class="fas ${typeIcon}"></i>
          <span>${device.name || device.id}</span>
          <span class="badge badge-success">${typeName}</span>
        </div>
        <div class="device-channels">
          <div class="channel-info">
            <span class="channel-label">A通道</span>
            <div class="strength-bar">
              <div class="strength-fill" style="width: ${(state.strengthA / 200) * 100}%; background: var(--primary);"></div>
            </div>
            <span class="strength-value">${state.strengthA}/${state.limitA || 200}</span>
          </div>
          <div class="channel-info">
            <span class="channel-label">B通道</span>
            <div class="strength-bar">
              <div class="strength-fill" style="width: ${(state.strengthB / 200) * 100}%; background: var(--warning);"></div>
            </div>
            <span class="strength-value">${state.strengthB}/${state.limitB || 200}</span>
          </div>
        </div>
      </div>
    `;
  }).join('');
}

// 刷新设备强度
async function refreshDeviceStrength() {
  try {
    const devices = await api('/api/devices');
    state.devices = devices.data || [];
    renderDeviceStrengthPreview();
    showToast('设备状态已刷新', 'info');
  } catch (error) {
    showToast('刷新失败', 'error');
  }
}

// 加载仪表盘 OCR 状态
async function loadDashboardOCRStatus() {
  try {
    const result = await api('/api/ocr/status');
    const ocrStatus = result.data || {};
    
    // 更新血量显示
    const bloodEl = $('#dashboard-ocr-blood');
    if (bloodEl) {
      bloodEl.textContent = ocrStatus.currentBlood ?? 100;
    }
    
    // 更新运行状态
    const statusEl = $('#dashboard-ocr-status');
    if (statusEl) {
      const isRunning = ocrStatus.isRunning;
      statusEl.textContent = isRunning ? '运行中' : '未运行';
      statusEl.style.color = isRunning ? 'var(--success)' : 'var(--text-muted)';
    }
    
    // 更新启用规则数
    const rulesEl = $('#dashboard-ocr-rules');
    if (rulesEl) {
      rulesEl.textContent = ocrStatus.enabledRulesCount ?? 0;
    }
    
    // 更新最近触发
    const lastEl = $('#dashboard-ocr-last');
    if (lastEl && ocrStatus.lastTriggered) {
      const time = new Date(ocrStatus.lastTriggered).toLocaleTimeString();
      lastEl.textContent = time;
    }
  } catch (e) {
    console.warn('获取OCR状态失败', e);
  }
}

function renderQuickDeviceList() {
  const container = $('#quick-device-list');
  
  if (state.devices.length === 0) {
    container.innerHTML = `
      <div class="text-muted text-sm" style="padding: 20px; text-align: center;">
        暂无设备，请先添加设备
      </div>
    `;
    return;
  }
  
  container.innerHTML = state.devices.map(device => `
    <div class="device-item" data-id="${device.id}">
      <div class="device-icon ${device.type === 'yokonex' ? 'yokonex' : ''}">
        <i class="fas ${device.type === 'dglab' ? 'fa-bolt' : 'fa-mobile-alt'}"></i>
      </div>
      <div class="device-info">
        <div class="device-name">${device.name || device.id}</div>
        <div class="device-type">${device.type === 'dglab' ? '郊狼' : '役次元'}</div>
      </div>
      <div class="device-status ${device.status === 'connected' ? 'connected' : ''}">
        <i class="fas fa-circle"></i>
        ${device.status === 'connected' ? '已连接' : '未连接'}
      </div>
      <button class="btn btn-sm btn-primary" onclick="quickControl('${device.id}')">
        <i class="fas fa-sliders-h"></i>
      </button>
    </div>
  `).join('');
}

function quickControl(deviceId) {
  state.currentDevice = deviceId;
  // 切换到控制页面
  $('.nav-item[data-page="control"]').click();
}

// ===== 设备管理 =====
async function loadDevices() {
  try {
    const devices = await api('/api/devices');
    state.devices = devices.data || [];
    renderDeviceList();
  } catch (error) {
    showToast('加载设备列表失败', 'error');
  }
}

function renderDeviceList() {
  const container = $('#device-list');
  
  if (state.devices.length === 0) {
    container.innerHTML = `
      <div class="text-muted" style="padding: 40px; text-align: center;">
        <i class="fas fa-inbox" style="font-size: 48px; margin-bottom: 15px;"></i>
        <p>暂无设备</p>
        <p class="text-sm">使用上方表单添加您的第一个设备</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = state.devices.map(device => `
    <div class="device-item" data-id="${device.id}">
      <div class="device-icon ${device.type === 'yokonex' ? 'yokonex' : ''}">
        <i class="fas ${device.type === 'dglab' ? 'fa-bolt' : 'fa-mobile-alt'}"></i>
      </div>
      <div class="device-info">
        <div class="device-name">${device.name || device.id}</div>
        <div class="device-type">
          ${device.type === 'dglab' ? '郊狼' : '役次元'} 
          ${device.config?.wsUrl || device.config?.sdkAppId || ''}
        </div>
      </div>
      <div class="device-status ${device.status === 'connected' ? 'connected' : ''}">
        <i class="fas fa-circle"></i>
        ${device.status === 'connected' ? '已连接' : '未连接'}
      </div>
      <div class="device-controls">
        ${device.status === 'connected' 
          ? `<button class="btn btn-sm btn-warning" onclick="disconnectDevice('${device.id}')">
              <i class="fas fa-unlink"></i> 断开
            </button>`
          : `<button class="btn btn-sm btn-success" onclick="connectDevice('${device.id}')">
              <i class="fas fa-link"></i> 连接
            </button>`
        }
        <button class="btn btn-sm btn-danger" onclick="removeDevice('${device.id}')">
          <i class="fas fa-trash"></i>
        </button>
      </div>
    </div>
  `).join('');
}

// ===== DG-LAB 连接方式切换 =====
function initDGLabConnectionTabs() {
  const radios = document.querySelectorAll('input[name="dglab-conn-type"]');
  const forms = document.querySelectorAll('.dglab-conn-form');
  
  radios.forEach(radio => {
    radio.addEventListener('change', () => {
      const connType = radio.value;
      forms.forEach(form => {
        form.classList.toggle('active', form.dataset.connType === connType);
      });
    });
  });
}

async function addDGLabDevice(event) {
  event.preventDefault();
  
  const form = event.target;
  const deviceName = form.deviceName.value;
  const wsUrl = form.wsUrl.value;
  
  // 验证 WebSocket URL 格式
  if (!wsUrl.startsWith('ws://') && !wsUrl.startsWith('wss://')) {
    showToast('WebSocket 地址必须以 ws:// 或 wss:// 开头', 'warning');
    return;
  }
  
  try {
    const result = await api('/api/devices', {
      method: 'POST',
      body: JSON.stringify({
        type: 'dglab',
        name: deviceName,
        config: {
          websocketUrl: wsUrl,
          connectionType: 'websocket'
        }
      })
    });
    
    showToast('DG-LAB 设备添加成功，正在连接...', 'success');
    form.reset();
    loadDevices();
    
    // 自动连接设备
    if (result.data && result.data.id) {
      await connectDGLabWebSocket(result.data.id, wsUrl);
    }
    
  } catch (error) {
    showToast(`添加失败: ${error.message}`, 'error');
  }
}

// 连接DG-LAB WebSocket并显示二维码
async function connectDGLabWebSocket(deviceId, wsUrl) {
  try {
    const result = await api(`/api/devices/${deviceId}/connect`, {
      method: 'POST'
    });
    
    // 如果有clientId，显示二维码
    if (result.data && result.data.clientId) {
      showDGLabQRCode(wsUrl, result.data.clientId);
    }
  } catch (error) {
    showToast(`连接失败: ${error.message}`, 'error');
  }
}

// 显示DG-LAB二维码
function showDGLabQRCode(wsUrl, clientId) {
  // 生成二维码内容
  // 格式: https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#ws://服务器地址/clientId
  const qrContent = `https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#${wsUrl}/${clientId}`;
  
  // 创建二维码模态框
  const modal = document.createElement('div');
  modal.className = 'modal active';
  modal.id = 'qrcode-modal';
  modal.innerHTML = `
    <div class="modal-content">
      <div class="modal-header">
        <h3><i class="fas fa-qrcode"></i> 扫描二维码绑定设备</h3>
        <button class="modal-close" onclick="hideQRCodeModal()">
          <i class="fas fa-times"></i>
        </button>
      </div>
      <div class="modal-body" style="text-align: center;">
        <div id="qrcode-container" style="display: inline-block; padding: 20px; background: white; border-radius: 8px;"></div>
        <p style="margin-top: 15px; color: var(--text-secondary);">
          使用 DG-LAB APP 扫描此二维码完成绑定
        </p>
        <p class="text-muted text-sm">
          客户端 ID: ${clientId}
        </p>
      </div>
      <div class="modal-footer">
        <button class="btn btn-secondary" onclick="hideQRCodeModal()">关闭</button>
      </div>
    </div>
  `;
  document.body.appendChild(modal);
  
  // 生成二维码
  if (typeof QRCode !== 'undefined') {
    new QRCode(document.getElementById('qrcode-container'), {
      text: qrContent,
      width: 200,
      height: 200
    });
  } else {
    document.getElementById('qrcode-container').innerHTML = `
      <p>二维码内容:</p>
      <textarea readonly style="width: 100%; height: 80px; font-size: 12px;">${qrContent}</textarea>
    `;
  }
}

function hideQRCodeModal() {
  const modal = document.getElementById('qrcode-modal');
  if (modal) {
    modal.remove();
  }
}

// DG-LAB 蓝牙连接 (Web Bluetooth API)
async function addDGLabBluetoothDevice(event) {
  event.preventDefault();
  
  const form = event.target;
  const deviceName = form.deviceName.value;
  
  // 检查浏览器是否支持 Web Bluetooth
  if (!navigator.bluetooth) {
    showToast('您的浏览器不支持 Web Bluetooth，请使用 Chrome 或 Edge', 'error');
    return;
  }
  
  try {
    showToast('正在搜索蓝牙设备...', 'info');
    
    // DG-LAB V3 蓝牙服务 UUID
    const SERVICE_UUID = 0x180C;
    const WRITE_CHAR_UUID = 0x150A;
    const NOTIFY_CHAR_UUID = 0x150B;
    const BATTERY_SERVICE_UUID = 0x180A;
    const BATTERY_CHAR_UUID = 0x1500;
    
    // 请求蓝牙设备
    const device = await navigator.bluetooth.requestDevice({
      filters: [
        { namePrefix: '47L121' }, // DG-LAB 脉冲主机 3.0
        { services: [SERVICE_UUID] }
      ],
      optionalServices: [SERVICE_UUID, BATTERY_SERVICE_UUID]
    });
    
    showToast(`已选择设备: ${device.name}`, 'info');
    
    // 连接设备
    const server = await device.gatt.connect();
    const service = await server.getPrimaryService(SERVICE_UUID);
    const writeChar = await service.getCharacteristic(WRITE_CHAR_UUID);
    const notifyChar = await service.getCharacteristic(NOTIFY_CHAR_UUID);
    
    // 启用通知
    await notifyChar.startNotifications();
    notifyChar.addEventListener('characteristicvaluechanged', handleDGLabNotification);
    
    // 存储蓝牙连接信息
    const bluetoothDevice = {
      device,
      server,
      writeChar,
      notifyChar,
      name: deviceName || device.name
    };
    
    // 将蓝牙设备添加到本地状态
    state.bluetoothDevices = state.bluetoothDevices || {};
    const btDeviceId = `bt_${device.id || Date.now()}`;
    state.bluetoothDevices[btDeviceId] = bluetoothDevice;
    
    // 添加到API设备列表（作为本地蓝牙设备）
    await api('/api/devices', {
      method: 'POST',
      body: JSON.stringify({
        type: 'dglab',
        name: deviceName || device.name,
        config: {
          connectionType: 'bluetooth',
          bluetoothId: btDeviceId,
          deviceName: device.name
        }
      })
    });
    
    showToast(`蓝牙设备 "${device.name}" 连接成功！`, 'success');
    form.reset();
    loadDevices();
    
    // 监听断开连接
    device.addEventListener('gattserverdisconnected', () => {
      showToast(`蓝牙设备 "${device.name}" 已断开`, 'warning');
      delete state.bluetoothDevices[btDeviceId];
      loadDevices();
    });
    
  } catch (error) {
    if (error.name === 'NotFoundError') {
      showToast('未找到蓝牙设备，请确保设备已开启', 'warning');
    } else {
      showToast(`蓝牙连接失败: ${error.message}`, 'error');
    }
  }
}

// 处理DG-LAB蓝牙通知
function handleDGLabNotification(event) {
  const value = event.target.value;
  const data = new Uint8Array(value.buffer);
  
  if (data[0] === 0xB1) {
    // 强度变化响应
    const sequence = data[1];
    const strengthA = data[2];
    const strengthB = data[3];
    console.log(`DG-LAB BT 强度变化: A=${strengthA}, B=${strengthB}, seq=${sequence}`);
    
    // 更新UI
    updateStrengthDisplay('A', strengthA);
    updateStrengthDisplay('B', strengthB);
  }
}

// 发送DG-LAB蓝牙命令 (B0指令)
async function sendDGLabBluetoothCommand(bluetoothId, command) {
  const btDevice = state.bluetoothDevices?.[bluetoothId];
  if (!btDevice || !btDevice.writeChar) {
    throw new Error('蓝牙设备未连接');
  }
  
  await btDevice.writeChar.writeValue(command);
}

async function addYokonexDevice(event) {
  event.preventDefault();
  
  const form = event.target;
  const deviceName = form.deviceName.value;
  const uid = form.uid.value;
  const token = form.token.value;
  const targetUserId = form.targetUserId.value;
  
  // 验证 uid 格式
  if (!uid.startsWith('game_')) {
    showToast('用户 UID 必须以 game_ 开头，例如 game_5', 'warning');
    return;
  }
  
  try {
    await api('/api/devices', {
      method: 'POST',
      body: JSON.stringify({
        type: 'yokonex',
        name: deviceName,
        config: {
          uid,
          token,
          targetUserId,
          connectionType: 'im'
        }
      })
    });
    
    showToast('役次元设备添加成功', 'success');
    form.reset();
    loadDevices();
    
  } catch (error) {
    showToast(`添加失败: ${error.message}`, 'error');
  }
}

// ===== YOKONEX 连接方式切换 =====
function initYokonexConnectionTabs() {
  const radios = document.querySelectorAll('input[name="yokonex-conn-type"]');
  const forms = document.querySelectorAll('.yokonex-conn-form');
  
  radios.forEach(radio => {
    radio.addEventListener('change', () => {
      const connType = radio.value;
      forms.forEach(form => {
        form.classList.toggle('active', form.dataset.connType === connType);
      });
    });
  });
}

// ===== YOKONEX 蓝牙协议常量 (YSKJ_EMS_BLE V1.6) =====
const YOKONEX_BLE = {
  // 服务和特征 UUID
  SERVICE_UUID: '0000ff30-0000-1000-8000-00805f9b34fb',
  WRITE_CHAR_UUID: '0000ff31-0000-1000-8000-00805f9b34fb',
  NOTIFY_CHAR_UUID: '0000ff32-0000-1000-8000-00805f9b34fb',
  
  // 命令常量
  HEADER: 0x35,
  CMD_CHANNEL: 0x11,     // 通道控制
  CMD_MOTOR: 0x12,       // 马达控制
  CMD_STEP: 0x13,        // 计步功能
  CMD_ANGLE: 0x14,       // 角度功能
  CMD_QUERY: 0x71,       // 查询命令
  
  // 通道号
  CHANNEL_A: 0x01,
  CHANNEL_B: 0x02,
  CHANNEL_AB: 0x03,
  
  // 通道状态
  STATE_OFF: 0x00,
  STATE_ON: 0x01,
  
  // 预设模式 (0x01-0x10共16种，0x11为自定义模式)
  MODE_CUSTOM: 0x11,
  
  // 查询类型
  QUERY_CHANNEL_A: 0x01,
  QUERY_CHANNEL_B: 0x02,
  QUERY_MOTOR: 0x03,
  QUERY_BATTERY: 0x04,
  QUERY_STEPS: 0x05,
  QUERY_ANGLE: 0x06
};

// 计算校验和 (累加和)
function calculateYokonexChecksum(bytes) {
  let sum = 0;
  for (const byte of bytes) {
    sum += byte;
  }
  return sum & 0xFF;
}

// YOKONEX 蓝牙连接 (Web Bluetooth API)
async function addYokonexBluetoothDevice(event) {
  event.preventDefault();
  
  const form = event.target;
  const deviceName = form.deviceName.value;
  
  // 检查浏览器是否支持 Web Bluetooth
  if (!navigator.bluetooth) {
    showToast('您的浏览器不支持 Web Bluetooth，请使用 Chrome 或 Edge', 'error');
    return;
  }
  
  try {
    showToast('正在搜索役次元蓝牙设备...', 'info');
    
    // 请求蓝牙设备 - 过滤服务 UUID FF30
    const device = await navigator.bluetooth.requestDevice({
      filters: [
        { services: [YOKONEX_BLE.SERVICE_UUID] }
      ],
      optionalServices: [YOKONEX_BLE.SERVICE_UUID]
    });
    
    showToast(`已选择设备: ${device.name || device.id}`, 'info');
    
    // 连接设备
    const server = await device.gatt.connect();
    const service = await server.getPrimaryService(YOKONEX_BLE.SERVICE_UUID);
    const writeChar = await service.getCharacteristic(YOKONEX_BLE.WRITE_CHAR_UUID);
    const notifyChar = await service.getCharacteristic(YOKONEX_BLE.NOTIFY_CHAR_UUID);
    
    // 启用通知
    await notifyChar.startNotifications();
    notifyChar.addEventListener('characteristicvaluechanged', handleYokonexNotification);
    
    // 存储蓝牙连接信息
    const btDeviceId = `yoko_bt_${device.id || Date.now()}`;
    const yokonexBluetoothDevice = {
      device,
      server,
      writeChar,
      notifyChar,
      name: deviceName || device.name,
      type: 'yokonex',
      // 设备状态
      channelA: { on: false, strength: 0, mode: 1 },
      channelB: { on: false, strength: 0, mode: 1 },
      battery: 0
    };
    
    // 存储到全局状态
    state.bluetoothDevices = state.bluetoothDevices || {};
    state.bluetoothDevices[btDeviceId] = yokonexBluetoothDevice;
    
    // 添加到API设备列表（作为本地蓝牙设备）
    await api('/api/devices', {
      method: 'POST',
      body: JSON.stringify({
        type: 'yokonex',
        name: deviceName || device.name,
        config: {
          connectionType: 'bluetooth',
          bluetoothId: btDeviceId,
          deviceName: device.name || device.id
        }
      })
    });
    
    showToast(`YOKONEX 蓝牙设备 "${device.name || device.id}" 连接成功！`, 'success');
    form.reset();
    loadDevices();
    
    // 查询初始状态
    await queryYokonexStatus(btDeviceId);
    
    // 监听断开连接
    device.addEventListener('gattserverdisconnected', () => {
      showToast(`YOKONEX 设备 "${device.name}" 已断开`, 'warning');
      delete state.bluetoothDevices[btDeviceId];
      loadDevices();
    });
    
  } catch (error) {
    if (error.name === 'NotFoundError') {
      showToast('未找到 YOKONEX 蓝牙设备，请确保设备已开启', 'warning');
    } else {
      showToast(`YOKONEX 蓝牙连接失败: ${error.message}`, 'error');
    }
  }
}

// 处理 YOKONEX 蓝牙通知
function handleYokonexNotification(event) {
  const value = event.target.value;
  const data = new Uint8Array(value.buffer);
  
  // 验证包头
  if (data[0] !== YOKONEX_BLE.HEADER) {
    console.warn('YOKONEX 收到未知数据:', data);
    return;
  }
  
  const cmdType = data[1];
  
  if (cmdType === YOKONEX_BLE.CMD_QUERY) {
    // 查询应答
    const queryType = data[2];
    
    switch (queryType) {
      case YOKONEX_BLE.QUERY_CHANNEL_A:
        // 通道A状态: [header, cmd, type, connState, onState, strengthH, strengthL, mode, checksum]
        console.log('YOKONEX 通道A状态:', {
          connection: data[3], // 0=未接入, 1=已接入在放电, 2=已接入未放电
          on: data[4] === 0x01,
          strength: (data[5] << 8) | data[6],
          mode: data[7]
        });
        break;
        
      case YOKONEX_BLE.QUERY_CHANNEL_B:
        console.log('YOKONEX 通道B状态:', {
          connection: data[3],
          on: data[4] === 0x01,
          strength: (data[5] << 8) | data[6],
          mode: data[7]
        });
        break;
        
      case YOKONEX_BLE.QUERY_MOTOR:
        console.log('YOKONEX 马达状态:', data[3]);
        break;
        
      case YOKONEX_BLE.QUERY_BATTERY:
        // 电池电量: [header, cmd, type, battery, checksum]
        const battery = data[3]; // 0-100%
        console.log('YOKONEX 电池电量:', battery + '%');
        showToast(`YOKONEX 电池电量: ${battery}%`, 'info');
        break;
        
      case YOKONEX_BLE.QUERY_STEPS:
        const steps = (data[3] << 8) | data[4];
        console.log('YOKONEX 计步数据:', steps);
        break;
        
      case 0x55:
        // 异常上报
        const errorCode = data[3];
        const errorMessages = {
          0x01: '校验码错误',
          0x02: '包头错误',
          0x03: '命令错误',
          0x04: '数据错误',
          0x05: '暂未实现'
        };
        console.error('YOKONEX 错误:', errorMessages[errorCode] || `未知错误 0x${errorCode.toString(16)}`);
        showToast(`YOKONEX 设备错误: ${errorMessages[errorCode] || '未知错误'}`, 'error');
        break;
    }
  }
}

// 查询 YOKONEX 设备状态
async function queryYokonexStatus(bluetoothId) {
  const btDevice = state.bluetoothDevices?.[bluetoothId];
  if (!btDevice || !btDevice.writeChar) {
    throw new Error('YOKONEX 蓝牙设备未连接');
  }
  
  // 查询电池电量
  const batteryCmd = new Uint8Array([
    YOKONEX_BLE.HEADER,
    YOKONEX_BLE.CMD_QUERY,
    YOKONEX_BLE.QUERY_BATTERY,
    0x00 // checksum placeholder
  ]);
  batteryCmd[3] = calculateYokonexChecksum(batteryCmd.slice(0, 3));
  await btDevice.writeChar.writeValueWithoutResponse(batteryCmd);
}

// 发送 YOKONEX 通道控制命令
// channel: 'A', 'B', 'AB'
// state: true/false (开/关)
// strength: 0-276
// mode: 1-16 (预设模式) 或 17 (0x11, 自定义模式)
// frequency: 1-100 (Hz, 仅自定义模式有效)
// pulseWidth: 0-100 (us, 仅自定义模式有效)
async function sendYokonexChannelCommand(bluetoothId, channel, on, strength, mode = 1, frequency = 0, pulseWidth = 0) {
  const btDevice = state.bluetoothDevices?.[bluetoothId];
  if (!btDevice || !btDevice.writeChar) {
    throw new Error('YOKONEX 蓝牙设备未连接');
  }
  
  // 通道号
  let channelByte;
  switch (channel.toUpperCase()) {
    case 'A': channelByte = YOKONEX_BLE.CHANNEL_A; break;
    case 'B': channelByte = YOKONEX_BLE.CHANNEL_B; break;
    case 'AB': channelByte = YOKONEX_BLE.CHANNEL_AB; break;
    default: throw new Error('无效的通道: ' + channel);
  }
  
  // 强度范围限制 (1-276)
  strength = Math.max(0, Math.min(276, strength));
  const strengthH = (strength >> 8) & 0xFF;
  const strengthL = strength & 0xFF;
  
  // 构建命令 (10字节)
  // [header, cmd, channel, onState, strengthH, strengthL, mode, frequency, pulseWidth, checksum]
  const cmd = new Uint8Array([
    YOKONEX_BLE.HEADER,      // 字节1: 包头 0x35
    YOKONEX_BLE.CMD_CHANNEL, // 字节2: 命令字 0x11
    channelByte,             // 字节3: 通道号
    on ? 0x01 : 0x00,        // 字节4: 开启状态
    strengthH,               // 字节5: 强度高字节
    strengthL,               // 字节6: 强度低字节
    mode,                    // 字节7: 模式
    frequency,               // 字节8: 频率 (自定义模式)
    pulseWidth,              // 字节9: 脉冲时间 (自定义模式)
    0x00                     // 字节10: 校验和
  ]);
  cmd[9] = calculateYokonexChecksum(cmd.slice(0, 9));
  
  await btDevice.writeChar.writeValueWithoutResponse(cmd);
  console.log('YOKONEX 发送通道控制:', { channel, on, strength, mode, frequency, pulseWidth });
}

// 发送 YOKONEX 马达控制命令
async function sendYokonexMotorCommand(bluetoothId, state) {
  const btDevice = window.state?.bluetoothDevices?.[bluetoothId];
  if (!btDevice || !btDevice.writeChar) {
    throw new Error('YOKONEX 蓝牙设备未连接');
  }
  
  // state: 0x00关闭, 0x01开启, 0x11预设频率1, 0x12预设频率2, 0x13预设频率3
  const cmd = new Uint8Array([
    YOKONEX_BLE.HEADER,
    YOKONEX_BLE.CMD_MOTOR,
    state,
    0x00
  ]);
  cmd[3] = calculateYokonexChecksum(cmd.slice(0, 3));
  
  await btDevice.writeChar.writeValueWithoutResponse(cmd);
}

async function connectDevice(deviceId) {
  try {
    await api(`/api/devices/${deviceId}/connect`, {
      method: 'POST'
    });
    showToast('正在连接...', 'info');
  } catch (error) {
    showToast(`连接失败: ${error.message}`, 'error');
  }
}

async function disconnectDevice(deviceId) {
  try {
    await api(`/api/devices/${deviceId}/disconnect`, {
      method: 'POST'
    });
    showToast('已断开连接', 'info');
    loadDevices();
  } catch (error) {
    showToast(`断开失败: ${error.message}`, 'error');
  }
}

async function removeDevice(deviceId) {
  if (!confirm('确定要删除此设备吗？')) return;
  
  try {
    await api(`/api/devices/${deviceId}`, {
      method: 'DELETE'
    });
    showToast('设备已删除', 'success');
    loadDevices();
  } catch (error) {
    showToast(`删除失败: ${error.message}`, 'error');
  }
}

// ===== 设备控制 =====
async function loadControlPage() {
  // 更新设备选择器
  const selector = $('#control-device-select');
  if (!selector) return;
  
  selector.innerHTML = `
    <option value="">-- 选择设备 --</option>
    ${state.devices.map(d => `
      <option value="${d.id}" ${d.id === state.currentDevice ? 'selected' : ''}>
        ${d.name || d.id} (${d.type})${d.isVirtual ? ' [虚拟]' : ''}
      </option>
    `).join('')}
  `;
  
  if (state.currentDevice) {
    selector.value = state.currentDevice;
    loadDeviceStrength();
  }
}

async function onDeviceSelect(deviceId) {
  state.currentDevice = deviceId;
  if (deviceId) {
    loadDeviceStrength();
  }
}

async function loadDeviceStrength() {
  if (!state.currentDevice) return;
  
  try {
    const result = await api(`/api/devices/${state.currentDevice}`);
    const device = result.data;
    
    if (device.strength) {
      updateStrengthUI('a', device.strength.a || 0);
      updateStrengthUI('b', device.strength.b || 0);
    }
  } catch (error) {
    console.error('Load strength error:', error);
  }
}

function updateStrengthUI(channel, value) {
  // 标准化通道名为大写
  const ch = (channel + '').toUpperCase();
  const slider = $(`#channel${ch}Slider`);
  const valueEl = $(`#channel${ch}Strength`);
  const circle = $(`#channel${ch}Circle`);
  
  if (slider) slider.value = value;
  if (valueEl) valueEl.textContent = value;
  if (circle) {
    const percent = (value / 200) * 100;
    circle.style.background = `conic-gradient(${ch === 'A' ? 'var(--primary)' : 'var(--success)'} ${percent}%, var(--bg-input) ${percent}%)`;
  }
}

function updateStrengthDisplay(deviceId, channel, value) {
  if (deviceId === state.currentDevice) {
    updateStrengthUI(channel, value);
  }
}

async function setStrength(channel, value) {
  if (!state.currentDevice) {
    showToast('请先选择设备', 'warning');
    return;
  }
  
  // 转换通道为数字格式 (API需要 1=A, 2=B)
  const channelNum = channel === 'A' || channel === 1 ? 1 : 2;
  
  try {
    await api('/api/control/strength', {
      method: 'POST',
      body: JSON.stringify({
        deviceId: state.currentDevice,
        channel: channelNum,
        value: parseInt(value),
        mode: 2 // StrengthMode.Set = 2
      })
    });
    
    updateStrengthUI(channel, value);
  } catch (error) {
    showToast(`设置强度失败: ${error.message}`, 'error');
  }
}

async function adjustStrength(channel, delta) {
  if (!state.currentDevice) {
    showToast('请先选择设备', 'warning');
    return;
  }
  
  // 转换通道为数字格式
  const channelNum = channel === 'A' || channel === 1 ? 1 : 2;
  
  try {
    await api('/api/control/strength', {
      method: 'POST',
      body: JSON.stringify({
        deviceId: state.currentDevice,
        channel: channelNum,
        value: Math.abs(delta),
        mode: delta > 0 ? 1 : 0 // 1=Increase, 0=Decrease
      })
    });
    
    // UI 会通过 WebSocket 更新
  } catch (error) {
    showToast(`调节强度失败: ${error.message}`, 'error');
  }
}

async function sendWaveformPreset(presetName) {
  if (!state.currentDevice) {
    showToast('请先选择设备', 'warning');
    return;
  }
  
  // 预设波形定义
  const presets = {
    pulse: {
      name: '脉冲',
      data: '0A0A0A0A64646464' // 示例波形
    },
    wave: {
      name: '波浪',
      data: '00142850647850281400142850647850'
    },
    breath: {
      name: '呼吸',
      data: '000A141E28323C46505A646E78828C96'
    },
    random: {
      name: '随机',
      data: generateRandomWaveform()
    }
  };
  
  const preset = presets[presetName];
  if (!preset) {
    showToast('未知的预设波形', 'error');
    return;
  }
  
  try {
    await api(`/api/control/${state.currentDevice}/waveform`, {
      method: 'POST',
      body: JSON.stringify({
        channel: 'a',
        waveform: preset.data
      })
    });
    
    showToast(`已发送波形: ${preset.name}`, 'success');
  } catch (error) {
    showToast(`发送波形失败: ${error.message}`, 'error');
  }
}

function generateRandomWaveform() {
  let hex = '';
  for (let i = 0; i < 8; i++) {
    const value = Math.floor(Math.random() * 100);
    hex += value.toString(16).padStart(2, '0');
  }
  return hex.toUpperCase();
}

async function stopDevice() {
  if (!state.currentDevice) {
    showToast('请先选择设备', 'warning');
    return;
  }
  
  try {
    // 设置两个通道强度为0
    await Promise.all([
      api(`/api/control/${state.currentDevice}/strength`, {
        method: 'POST',
        body: JSON.stringify({ channel: 'a', value: 0, mode: 'set' })
      }),
      api(`/api/control/${state.currentDevice}/strength`, {
        method: 'POST',
        body: JSON.stringify({ channel: 'b', value: 0, mode: 'set' })
      })
    ]);
    
    updateStrengthUI('a', 0);
    updateStrengthUI('b', 0);
    showToast('已停止', 'success');
  } catch (error) {
    showToast(`停止失败: ${error.message}`, 'error');
  }
}

// ===== 波形编辑器 =====
let waveformCanvas, waveformCtx;
let waveformData = [];

function initWaveformEditor() {
  waveformCanvas = $('#waveformCanvas');
  if (!waveformCanvas) return;
  
  waveformCtx = waveformCanvas.getContext('2d');
  waveformCanvas.width = waveformCanvas.offsetWidth;
  waveformCanvas.height = 200;
  
  // 初始化数据
  waveformData = new Array(16).fill(50);
  drawWaveform();
  
  // 鼠标交互
  waveformCanvas.addEventListener('mousedown', handleCanvasMouseDown);
  waveformCanvas.addEventListener('mousemove', handleCanvasMouseMove);
  waveformCanvas.addEventListener('mouseup', handleCanvasMouseUp);
}

let isDrawing = false;

function handleCanvasMouseDown(e) {
  isDrawing = true;
  updateWaveformPoint(e);
}

function handleCanvasMouseMove(e) {
  if (isDrawing) {
    updateWaveformPoint(e);
  }
}

function handleCanvasMouseUp() {
  isDrawing = false;
  updateWaveformHex();
}

function updateWaveformPoint(e) {
  const rect = waveformCanvas.getBoundingClientRect();
  const x = e.clientX - rect.left;
  const y = e.clientY - rect.top;
  
  const segmentWidth = waveformCanvas.width / waveformData.length;
  const index = Math.floor(x / segmentWidth);
  
  if (index >= 0 && index < waveformData.length) {
    waveformData[index] = Math.round((1 - y / waveformCanvas.height) * 100);
    waveformData[index] = Math.max(0, Math.min(100, waveformData[index]));
    drawWaveform();
  }
}

function drawWaveform() {
  if (!waveformCtx) return;
  
  const width = waveformCanvas.width;
  const height = waveformCanvas.height;
  const segmentWidth = width / waveformData.length;
  
  // 清除画布
  waveformCtx.fillStyle = '#1e293b';
  waveformCtx.fillRect(0, 0, width, height);
  
  // 绘制网格
  waveformCtx.strokeStyle = '#334155';
  waveformCtx.lineWidth = 1;
  
  for (let i = 0; i <= 10; i++) {
    const y = (height / 10) * i;
    waveformCtx.beginPath();
    waveformCtx.moveTo(0, y);
    waveformCtx.lineTo(width, y);
    waveformCtx.stroke();
  }
  
  // 绘制波形
  waveformCtx.strokeStyle = '#6366f1';
  waveformCtx.lineWidth = 2;
  waveformCtx.beginPath();
  
  for (let i = 0; i < waveformData.length; i++) {
    const x = i * segmentWidth + segmentWidth / 2;
    const y = height - (waveformData[i] / 100) * height;
    
    if (i === 0) {
      waveformCtx.moveTo(x, y);
    } else {
      waveformCtx.lineTo(x, y);
    }
  }
  
  waveformCtx.stroke();
  
  // 绘制点
  waveformCtx.fillStyle = '#6366f1';
  for (let i = 0; i < waveformData.length; i++) {
    const x = i * segmentWidth + segmentWidth / 2;
    const y = height - (waveformData[i] / 100) * height;
    
    waveformCtx.beginPath();
    waveformCtx.arc(x, y, 4, 0, Math.PI * 2);
    waveformCtx.fill();
  }
}

function updateWaveformHex() {
  const hex = waveformData.map(v => v.toString(16).padStart(2, '0')).join('').toUpperCase();
  $('#waveform-hex').value = hex;
}

function parseWaveformHex(hex) {
  hex = hex.replace(/\s/g, '').toUpperCase();
  const values = [];
  
  for (let i = 0; i < hex.length; i += 2) {
    const byte = parseInt(hex.substr(i, 2), 16);
    values.push(isNaN(byte) ? 0 : Math.min(byte, 100));
  }
  
  while (values.length < 16) {
    values.push(0);
  }
  
  waveformData = values.slice(0, 16);
  drawWaveform();
}

function clearWaveform() {
  waveformData = new Array(16).fill(0);
  drawWaveform();
  updateWaveformHex();
}

function applyWaveformPreset(preset) {
  const presets = {
    sine: [0, 19, 38, 56, 71, 83, 92, 98, 100, 98, 92, 83, 71, 56, 38, 19],
    square: [100, 100, 100, 100, 100, 100, 100, 100, 0, 0, 0, 0, 0, 0, 0, 0],
    triangle: [0, 13, 25, 38, 50, 63, 75, 88, 100, 88, 75, 63, 50, 38, 25, 13],
    sawtooth: [0, 7, 13, 20, 27, 33, 40, 47, 53, 60, 67, 73, 80, 87, 93, 100]
  };
  
  if (presets[preset]) {
    waveformData = [...presets[preset]];
    drawWaveform();
    updateWaveformHex();
  }
}

async function sendCustomWaveform() {
  if (!state.currentDevice) {
    showToast('请先选择设备', 'warning');
    return;
  }
  
  const channel = $('#waveform-channel').value;
  const hex = $('#waveform-hex').value;
  
  try {
    await api(`/api/control/${state.currentDevice}/waveform`, {
      method: 'POST',
      body: JSON.stringify({
        channel,
        waveform: hex
      })
    });
    
    showToast('波形已发送', 'success');
  } catch (error) {
    showToast(`发送波形失败: ${error.message}`, 'error');
  }
}

async function saveWaveform() {
  const name = $('#waveform-name').value;
  if (!name) {
    showToast('请输入波形名称', 'warning');
    return;
  }
  
  const hex = $('#waveform-hex').value;
  
  // 保存到本地存储
  const waveforms = JSON.parse(localStorage.getItem('waveforms') || '{}');
  waveforms[name] = hex;
  localStorage.setItem('waveforms', JSON.stringify(waveforms));
  
  showToast(`波形 "${name}" 已保存`, 'success');
}

// ===== 事件管理 =====
let currentEventCategory = 'all';

async function loadEvents() {
  try {
    let url = '/api/events';
    if (currentEventCategory && currentEventCategory !== 'all') {
      url += `?category=${currentEventCategory}`;
    }
    const result = await api(url);
    state.events = result.data || [];
    renderEventList();
    updateEventSelectors();
    updateEventStats();
  } catch (error) {
    console.error('加载事件失败:', error);
    showToast('加载电击规则列表失败', 'error');
  }
}

// 更新规则状态统计
function updateEventStats() {
  const total = state.events.length;
  const enabled = state.events.filter(e => e.enabled).length;
  const disabled = total - enabled;
  const custom = state.events.filter(e => e.category === 'custom').length;
  
  const totalEl = $('#event-total-count');
  const enabledEl = $('#event-enabled-count');
  const disabledEl = $('#event-disabled-count');
  const customEl = $('#event-custom-count');
  
  if (totalEl) totalEl.textContent = total;
  if (enabledEl) enabledEl.textContent = enabled;
  if (disabledEl) disabledEl.textContent = disabled;
  if (customEl) customEl.textContent = custom;
}

function refreshEvents() {
  loadEvents();
  showToast('规则列表已刷新', 'info');
}

// 启用全部规则
async function enableAllEvents() {
  if (!confirm('确定要启用全部电击规则吗？')) return;
  
  try {
    for (const event of state.events) {
      if (!event.enabled) {
        await api(`/api/events/${event.id}`, {
          method: 'PUT',
          body: JSON.stringify({ enabled: true })
        });
      }
    }
    showToast('已启用全部规则', 'success');
    loadEvents();
  } catch (error) {
    showToast(`操作失败: ${error.message}`, 'error');
  }
}

// 禁用全部规则
async function disableAllEvents() {
  if (!confirm('确定要禁用全部电击规则吗？这将暂停所有电击反馈。')) return;
  
  try {
    for (const event of state.events) {
      if (event.enabled) {
        await api(`/api/events/${event.id}`, {
          method: 'PUT',
          body: JSON.stringify({ enabled: false })
        });
      }
    }
    showToast('已禁用全部规则', 'warning');
    loadEvents();
  } catch (error) {
    showToast(`操作失败: ${error.message}`, 'error');
  }
}

function initEventCategoryTabs() {
  const tabs = document.querySelectorAll('.event-category-tabs .tab-btn');
  tabs.forEach(tab => {
    tab.addEventListener('click', () => {
      tabs.forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      currentEventCategory = tab.dataset.category || 'all';
      loadEvents();
    });
  });
}

function updateEventSelectors() {
  // 更新测试事件下拉框
  const testSelect = $('#test-event-select');
  if (testSelect) {
    testSelect.innerHTML = `
      <option value="">-- 选择事件 --</option>
      ${state.events.filter(e => e.enabled).map(e => `
        <option value="${e.eventId}">${e.name} (${e.eventId})</option>
      `).join('')}
    `;
  }
  
  // 更新测试设备下拉框
  const deviceSelect = $('#test-event-device');
  if (deviceSelect) {
    const devices = state.devices.filter(d => d.status === 'connected');
    deviceSelect.innerHTML = `
      <option value="">全部已连接设备</option>
      ${devices.map(d => `
        <option value="${d.id}">${d.name || d.id}</option>
      `).join('')}
    `;
  }
}

function renderEventList() {
  const container = $('#event-list');
  if (!container) return;
  
  if (state.events.length === 0) {
    container.innerHTML = `
      <div class="text-muted" style="padding: 40px; text-align: center;">
        <i class="fas fa-bolt" style="font-size: 48px; margin-bottom: 15px;"></i>
        <p>暂无电击规则</p>
        <p class="text-sm">该分类下没有规则</p>
      </div>
    `;
    return;
  }
  
  // 按分类分组
  const categoryNames = {
    system: '系统规则',
    game: '游戏规则',
    custom: '自定义规则'
  };
  
  const categoryIcons = {
    system: 'fa-cog',
    game: 'fa-gamepad',
    custom: 'fa-user-edit'
  };
  
  const actionLabels = {
    set: '设置强度',
    increase: '增加强度',
    decrease: '减少强度',
    pulse: '脉冲电击',
    waveform: '波形输出',
    custom: '仅同步/不触发设备'
  };
  
  container.innerHTML = state.events.map(event => `
    <div class="event-item ${!event.enabled ? 'disabled' : ''}" data-id="${event.id}">
      <div class="event-category-badge ${event.category}">
        <i class="fas ${categoryIcons[event.category] || 'fa-tag'}"></i>
      </div>
      <div class="event-info">
        <div class="event-header">
          <span class="event-id">${event.eventId}</span>
          <span class="event-name">${event.name}</span>
          ${!event.enabled ? '<span class="badge badge-disabled">已禁用</span>' : ''}
        </div>
        <div class="event-description text-muted text-sm">${event.description || ''}</div>
      </div>
      <div class="event-action">
        <span class="badge badge-channel">通道 ${event.channel}</span>
        <span class="badge badge-action">${actionLabels[event.action] || event.action}</span>
        <span class="badge badge-value">强度 ${event.value}</span>
        ${event.action === 'pulse' ? `<span class="badge badge-duration">${event.duration}ms</span>` : ''}
      </div>
      <div class="event-controls">
        <label class="switch" title="${event.enabled ? '点击禁用' : '点击启用'}">
          <input type="checkbox" ${event.enabled ? 'checked' : ''} onchange="toggleEvent('${event.id}', this.checked)">
          <span class="slider"></span>
        </label>
        <button class="btn btn-sm btn-outline" onclick="editEvent('${event.id}')" title="编辑">
          <i class="fas fa-edit"></i>
        </button>
        <button class="btn btn-sm btn-warning" onclick="testEventById('${event.eventId}')" title="测试电击">
          <i class="fas fa-bolt"></i>
        </button>
        ${event.category === 'custom' ? `
          <button class="btn btn-sm btn-danger" onclick="deleteEvent('${event.id}')" title="删除">
            <i class="fas fa-trash"></i>
          </button>
        ` : `
          <button class="btn btn-sm btn-secondary" onclick="resetEventToDefault('${event.id}')" title="重置">
            <i class="fas fa-undo"></i>
          </button>
        `}
      </div>
    </div>
  `).join('');
}

async function addEvent(e) {
  e.preventDefault();
  
  const form = e.target;
  const eventData = {
    eventId: form.eventId.value,
    name: form.eventName.value,
    description: form.eventDescription?.value || '',
    channel: form.eventChannel.value,
    action: form.eventAction.value,
    value: parseInt(form.eventValue.value),
    duration: parseInt(form.eventDuration.value),
    category: 'custom',
    enabled: form.eventEnabled?.checked ?? true
  };
  
  try {
    await api('/api/events', {
      method: 'POST',
      body: JSON.stringify(eventData)
    });
    showToast('电击规则已添加', 'success');
    form.reset();
    form.eventValue.value = '30';
    form.eventDuration.value = '500';
    if (form.eventEnabled) form.eventEnabled.checked = true;
    loadEvents();
  } catch (error) {
    showToast(`添加失败: ${error.message}`, 'error');
  }
}

async function toggleEvent(eventId, enabled) {
  try {
    await api(`/api/events/${eventId}`, {
      method: 'PUT',
      body: JSON.stringify({ enabled })
    });
    showToast(enabled ? '事件已启用' : '事件已禁用', 'info');
  } catch (error) {
    showToast(`操作失败: ${error.message}`, 'error');
    loadEvents(); // 刷新恢复状态
  }
}

async function deleteEvent(eventId) {
  if (!confirm('确定要删除此事件吗？')) return;
  
  try {
    await api(`/api/events/${eventId}`, { method: 'DELETE' });
    showToast('事件已删除', 'success');
    loadEvents();
  } catch (error) {
    showToast(`删除失败: ${error.message}`, 'error');
  }
}

async function resetEventToDefault(eventId) {
  try {
    await api(`/api/events/${eventId}/reset`, { method: 'POST' });
    showToast('事件已重置为默认值', 'success');
    loadEvents();
  } catch (error) {
    showToast(`重置失败: ${error.message}`, 'error');
  }
}

function editEvent(eventId) {
  const event = state.events.find(e => e.id === eventId);
  if (!event) return;
  
  // 创建编辑模态框
  const modal = document.createElement('div');
  modal.className = 'modal active';
  modal.id = 'event-edit-modal';
  modal.innerHTML = `
    <div class="modal-content" style="max-width: 500px;">
      <div class="modal-header">
        <h3><i class="fas fa-edit"></i> 编辑事件</h3>
        <button class="modal-close" onclick="closeEventEditModal()">
          <i class="fas fa-times"></i>
        </button>
      </div>
      <form id="event-edit-form" onsubmit="saveEventEdit(event, '${eventId}')">
        <div class="modal-body">
          <div class="form-group">
            <label>事件 ID</label>
            <input type="text" class="form-control" value="${event.eventId}" disabled style="background: var(--bg-dark); color: var(--text-muted);">
          </div>
          <div class="form-group">
            <label>事件名称</label>
            <input type="text" name="name" class="form-control" value="${event.name}" required>
          </div>
          <div class="form-group">
            <label>描述</label>
            <input type="text" name="description" class="form-control" value="${event.description || ''}">
          </div>
          <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 15px;">
            <div class="form-group">
              <label>通道</label>
              <select name="channel" class="form-control">
                <option value="A" ${event.channel === 'A' ? 'selected' : ''}>A 通道</option>
                <option value="B" ${event.channel === 'B' ? 'selected' : ''}>B 通道</option>
                <option value="AB" ${event.channel === 'AB' ? 'selected' : ''}>双通道</option>
              </select>
            </div>
            <div class="form-group">
              <label>动作</label>
              <select name="action" class="form-control">
                <option value="pulse" ${event.action === 'pulse' ? 'selected' : ''}>脉冲</option>
                <option value="set" ${event.action === 'set' ? 'selected' : ''}>设置</option>
                <option value="increase" ${event.action === 'increase' ? 'selected' : ''}>增加</option>
                <option value="decrease" ${event.action === 'decrease' ? 'selected' : ''}>减少</option>
              </select>
            </div>
          </div>
          <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 15px;">
            <div class="form-group">
              <label>强度值</label>
              <input type="number" name="value" class="form-control" value="${event.value}" min="0" max="200">
            </div>
            <div class="form-group">
              <label>持续时间 (ms)</label>
              <input type="number" name="duration" class="form-control" value="${event.duration}" min="0" max="10000">
            </div>
          </div>
        </div>
        <div class="modal-footer">
          <button type="button" class="btn btn-secondary" onclick="closeEventEditModal()">取消</button>
          <button type="submit" class="btn btn-primary">保存</button>
        </div>
      </form>
    </div>
  `;
  document.body.appendChild(modal);
  
  // 点击遮罩层关闭
  modal.addEventListener('click', (e) => {
    if (e.target === modal) closeEventEditModal();
  });
}

function closeEventEditModal() {
  const modal = document.getElementById('event-edit-modal');
  if (modal) modal.remove();
}

async function saveEventEdit(e, eventId) {
  e.preventDefault();
  const form = e.target;
  
  const updates = {
    name: form.name.value,
    description: form.description.value,
    channel: form.channel.value,
    action: form.action.value,
    value: parseInt(form.value.value),
    duration: parseInt(form.duration.value)
  };
  
  try {
    await api(`/api/events/${eventId}`, {
      method: 'PUT',
      body: JSON.stringify(updates)
    });
    showToast('事件已更新', 'success');
    closeEventEditModal();
    loadEvents();
  } catch (error) {
    showToast(`保存失败: ${error.message}`, 'error');
  }
}

async function testEventById(eventId) {
  const deviceId = $('#test-event-device')?.value || '';
  
  try {
    await api(`/api/events/${eventId}/trigger`, {
      method: 'POST',
      body: JSON.stringify({ deviceId: deviceId || undefined })
    });
    showToast(`事件 "${eventId}" 已触发`, 'success');
  } catch (error) {
    showToast(`触发失败: ${error.message}`, 'error');
  }
}

function initEventTestButton() {
  const btn = $('#btn-test-event');
  if (btn) {
    btn.addEventListener('click', () => {
      const eventId = $('#test-event-select')?.value;
      if (!eventId) {
        showToast('请选择要测试的事件', 'warning');
        return;
      }
      testEventById(eventId);
    });
  }
}

// ===== 设置 =====
async function loadSettings() {
  try {
    // 从API加载设置
    const result = await api('/api/settings');
    const settings = result.data || [];
    
    // 解析设置到表单
    for (const setting of settings) {
      applySettingToForm(setting.key, setting.value);
    }
  } catch (error) {
    console.log('使用本地设置');
    const settings = JSON.parse(localStorage.getItem('settings') || '{}');
    Object.entries(settings).forEach(([key, value]) => {
      applySettingToForm(key, value);
    });
  }
}

function applySettingToForm(key, value) {
  try {
    const parsedValue = typeof value === 'string' ? JSON.parse(value) : value;
    
    switch (key) {
      case 'server.apiUrl':
        const apiUrlEl = $('#settings-api-url');
        if (apiUrlEl) apiUrlEl.value = parsedValue;
        break;
      case 'safety.defaultLimit':
        const limitEl = $('#settings-default-limit');
        if (limitEl) limitEl.value = parsedValue;
        break;
      case 'safety.maxStrength':
        const maxEl = $('#settings-max-strength');
        if (maxEl) maxEl.value = parsedValue;
        break;
      case 'safety.autoStop':
        const autoStopEl = $('#settings-auto-stop');
        if (autoStopEl) autoStopEl.checked = parsedValue;
        break;
      case 'safety.confirmHigh':
        const confirmEl = $('#settings-confirm-high');
        if (confirmEl) confirmEl.checked = parsedValue;
        break;
    }
  } catch (e) {
    console.warn('Failed to apply setting:', key, e);
  }
}

async function saveSettings(e) {
  if (e) e.preventDefault();
  
  const settings = {
    'server.apiUrl': $('#settings-api-url')?.value || 'http://localhost:3000',
    'safety.defaultLimit': parseInt($('#settings-default-limit')?.value || '100'),
    'safety.maxStrength': parseInt($('#settings-max-strength')?.value || '200'),
    'safety.autoStop': $('#settings-auto-stop')?.checked ?? true,
    'safety.confirmHigh': $('#settings-confirm-high')?.checked ?? true
  };
  
  try {
    await api('/api/settings', {
      method: 'PUT',
      body: JSON.stringify({ settings })
    });
    showToast('设置已保存', 'success');
  } catch (error) {
    // 保存到本地
    localStorage.setItem('settings', JSON.stringify(settings));
    showToast('设置已保存 (本地)', 'success');
  }
}

// ===== 数据导出/导入 =====
async function exportAllData() {
  try {
    const response = await fetch(`${API_BASE}/api/settings/export/all`);
    const data = await response.json();
    
    // 下载为文件
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const filename = `device_adapter_backup_${new Date().toISOString().split('T')[0]}.json`;
    
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    
    URL.revokeObjectURL(url);
    showToast('数据已导出', 'success');
  } catch (error) {
    showToast(`导出失败: ${error.message}`, 'error');
  }
}

function importDataFile() {
  $('#import-file-input').click();
}

async function handleImportFile(event) {
  const file = event.target.files[0];
  if (!file) return;
  
  try {
    const text = await file.text();
    const data = JSON.parse(text);
    
    // 验证数据格式
    if (!data.version) {
      showToast('无效的备份文件格式', 'error');
      return;
    }
    
    // 确认导入
    const confirmMsg = `即将导入:\n- ${data.devices?.length || 0} 个设备\n- ${data.events?.length || 0} 个事件\n- ${data.scripts?.length || 0} 个脚本\n- ${data.settings?.length || 0} 项设置\n\n确定要导入吗？`;
    if (!confirm(confirmMsg)) return;
    
    // 发送导入请求
    const result = await api('/api/settings/import', {
      method: 'POST',
      body: JSON.stringify({ data, options: { merge: true } })
    });
    
    showToast(result.message || '数据导入成功', 'success');
    
    // 刷新页面数据
    loadDevices();
    loadEvents();
    loadScripts();
    loadSettings();
  } catch (error) {
    showToast(`导入失败: ${error.message}`, 'error');
  }
  
  // 清空文件输入
  event.target.value = '';
}

async function resetAllData() {
  if (!confirm('警告：此操作将删除所有数据并重置为默认值！\n\n确定要继续吗？')) return;
  if (!confirm('再次确认：所有设备、事件、脚本和设置都将被清除！')) return;
  
  try {
    await api('/api/settings/reset', {
      method: 'POST',
      body: JSON.stringify({ confirm: 'RESET_ALL' })
    });
    
    showToast('所有数据已重置', 'success');
    
    // 刷新页面
    setTimeout(() => {
      window.location.reload();
    }, 1000);
  } catch (error) {
    showToast(`重置失败: ${error.message}`, 'error');
  }
}

// ===== 游戏脚本管理 =====
let scriptsState = {
  scripts: [],
  currentScript: null
};

async function loadScripts() {
  try {
    const result = await api('/api/scripts');
    scriptsState.scripts = result.data || [];
    renderScriptList();
    updateScriptStats();
  } catch (error) {
    console.error('加载脚本失败:', error);
    showToast('加载脚本列表失败', 'error');
  }
}

function renderScriptList() {
  const container = $('#script-list');
  if (!container) return;
  
  if (scriptsState.scripts.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="fas fa-file-code"></i>
        <p>暂无脚本</p>
        <p class="text-muted text-sm">点击上方按钮添加游戏适配脚本</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = scriptsState.scripts.map(script => `
    <div class="script-item ${script.running ? 'running' : ''}" data-id="${script.id}">
      <div class="script-icon">
        <i class="fas ${script.running ? 'fa-play-circle' : 'fa-file-code'}"></i>
      </div>
      <div class="script-info">
        <div class="script-name">${escapeHtml(script.name)}</div>
        <div class="script-meta">
          <span class="script-game"><i class="fas fa-gamepad"></i> ${escapeHtml(script.game || '未知游戏')}</span>
          <span class="script-version">v${escapeHtml(script.version || '1.0.0')}</span>
          <span class="script-status ${script.running ? 'active' : ''}">
            ${script.running ? '运行中' : '已停止'}
          </span>
        </div>
        ${script.description ? `<div class="script-desc text-muted text-sm">${escapeHtml(script.description)}</div>` : ''}
      </div>
      <div class="script-actions">
        ${script.running ? `
          <button class="btn btn-sm btn-warning" onclick="stopScript('${script.id}')" title="停止">
            <i class="fas fa-stop"></i>
          </button>
        ` : `
          <button class="btn btn-sm btn-success" onclick="startScript('${script.id}')" title="启动">
            <i class="fas fa-play"></i>
          </button>
        `}
        <button class="btn btn-sm btn-secondary" onclick="editScript('${script.id}')" title="编辑">
          <i class="fas fa-edit"></i>
        </button>
        <button class="btn btn-sm btn-danger" onclick="deleteScript('${script.id}')" title="删除">
          <i class="fas fa-trash"></i>
        </button>
      </div>
    </div>
  `).join('');
}

function updateScriptStats() {
  const total = scriptsState.scripts.length;
  const running = scriptsState.scripts.filter(s => s.running).length;
  
  const totalEl = $('#stat-scripts-total');
  const runningEl = $('#stat-scripts-running');
  
  if (totalEl) totalEl.textContent = total;
  if (runningEl) runningEl.textContent = running;
}

function escapeHtml(str) {
  if (!str) return '';
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

function showScriptModal(isEdit = false) {
  const modal = $('#script-modal');
  const title = $('#script-modal-title');
  const form = $('#script-form');
  
  if (!modal) return;
  
  title.textContent = isEdit ? '编辑脚本' : '添加脚本';
  
  if (!isEdit) {
    form.reset();
    $('#script-id').value = '';
    $('#script-code').value = getScriptTemplate();
  }
  
  modal.classList.add('active');
}

function hideScriptModal() {
  const modal = $('#script-modal');
  if (modal) {
    modal.classList.remove('active');
  }
}

function getScriptTemplate() {
  return `/**
 * 游戏适配脚本模板
 * 
 * 可用 API:
 * - device.setStrength(channel, value) - 设置强度
 * - device.addStrength(channel, delta) - 增加强度
 * - device.sendWaveform(channel, waveformHex, duration) - 发送波形
 * - device.clearQueue(channel) - 清空队列
 * - script.log(message) - 输出日志
 * - script.sleep(ms) - 延迟执行
 * - storage.get(key) / storage.set(key, value) - 数据存储
 * - events.on(eventName, callback) - 监听事件
 * 
 * 常量:
 * - Channel.A, Channel.B, Channel.BOTH
 * - StrengthMode.SET, StrengthMode.ADD, StrengthMode.SUB
 */

// 脚本配置
const config = {
  baseStrength: 30,
  damageMultiplier: 1.5,
  cooldownMs: 500
};

// 初始化
script.log('脚本已加载');

// 监听游戏事件
events.on('player_hurt', (data) => {
  const damage = data.damage || 10;
  const strength = Math.min(200, config.baseStrength + damage * config.damageMultiplier);
  
  device.setStrength(Channel.BOTH, strength);
  script.log(\`受伤事件: 伤害=\${damage}, 强度=\${strength}\`);
});

events.on('player_death', () => {
  device.setStrength(Channel.BOTH, 150);
  script.log('死亡事件触发');
});
`;
}

async function saveScript(e) {
  e.preventDefault();
  
  const id = $('#script-id').value;
  const data = {
    name: $('#script-name').value,
    game: $('#script-game').value,
    version: $('#script-version').value,
    description: $('#script-description').value,
    code: $('#script-code').value
  };
  
  try {
    if (id) {
      // 更新脚本
      await api(`/api/scripts/${id}`, {
        method: 'PUT',
        body: JSON.stringify(data)
      });
      showToast('脚本已更新', 'success');
    } else {
      // 添加脚本
      await api('/api/scripts', {
        method: 'POST',
        body: JSON.stringify(data)
      });
      showToast('脚本已添加', 'success');
    }
    
    hideScriptModal();
    loadScripts();
  } catch (error) {
    showToast(`保存失败: ${error.message}`, 'error');
  }
}

async function editScript(id) {
  try {
    const result = await api(`/api/scripts/${id}`);
    const script = result.data;
    
    $('#script-id').value = script.id;
    $('#script-name').value = script.name;
    $('#script-game').value = script.game || '';
    $('#script-version').value = script.version || '1.0.0';
    $('#script-description').value = script.description || '';
    $('#script-code').value = script.code || '';
    
    showScriptModal(true);
  } catch (error) {
    showToast(`加载脚本失败: ${error.message}`, 'error');
  }
}

async function deleteScript(id) {
  if (!confirm('确定要删除这个脚本吗？')) return;
  
  try {
    await api(`/api/scripts/${id}`, { method: 'DELETE' });
    showToast('脚本已删除', 'success');
    loadScripts();
  } catch (error) {
    showToast(`删除失败: ${error.message}`, 'error');
  }
}

async function startScript(id) {
  try {
    await api(`/api/scripts/${id}/start`, { method: 'POST' });
    showToast('脚本已启动', 'success');
    loadScripts();
  } catch (error) {
    showToast(`启动失败: ${error.message}`, 'error');
  }
}

async function stopScript(id) {
  try {
    await api(`/api/scripts/${id}/stop`, { method: 'POST' });
    showToast('脚本已停止', 'success');
    loadScripts();
  } catch (error) {
    showToast(`停止失败: ${error.message}`, 'error');
  }
}

async function uploadScriptFile() {
  const input = document.createElement('input');
  input.type = 'file';
  input.accept = '.js';
  
  input.onchange = async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    
    try {
      const code = await file.text();
      const name = file.name.replace(/\.js$/, '');
      
      // 填充表单
      $('#script-id').value = '';
      $('#script-name').value = name;
      $('#script-game').value = '';
      $('#script-version').value = '1.0.0';
      $('#script-description').value = `从文件 ${file.name} 导入`;
      $('#script-code').value = code;
      
      showScriptModal(false);
      showToast('脚本文件已加载，请完善信息后保存', 'info');
    } catch (error) {
      showToast('读取文件失败', 'error');
    }
  };
  
  input.click();
}

async function triggerScriptEvent() {
  const eventName = $('#trigger-event-name').value;
  const eventDataStr = $('#trigger-event-data').value;
  
  if (!eventName) {
    showToast('请输入事件名称', 'warning');
    return;
  }
  
  let eventData = {};
  if (eventDataStr) {
    try {
      eventData = JSON.parse(eventDataStr);
    } catch (error) {
      showToast('事件数据必须是有效的 JSON', 'error');
      return;
    }
  }
  
  try {
    await api('/api/scripts/trigger', {
      method: 'POST',
      body: JSON.stringify({ event: eventName, data: eventData })
    });
    showToast(`事件 "${eventName}" 已触发`, 'success');
  } catch (error) {
    showToast(`触发失败: ${error.message}`, 'error');
  }
}

function openScriptDocs() {
  // 打开 API 文档页面
  window.open('/docs/api.html', '_blank');
}

// ===== 初始化 =====
document.addEventListener('DOMContentLoaded', () => {
  // 初始化导航
  initNavigation();
  initDeviceTypeTabs();
  
  // 连接 WebSocket
  connectWebSocket();
  
  // 初始化 Electron OCR 功能
  initElectronOCR();
  
  // 加载初始数据
  loadDashboard();
  loadSettings();
  loadScripts();
  loadEvents();
  
  // 初始化事件分类标签
  initEventCategoryTabs();
  initEventTestButton();
  
  // 初始化波形编辑器
  setTimeout(initWaveformEditor, 100);
  
  // 绑定表单事件
  const dglabForm = $('#dglab-form');
  if (dglabForm) {
    dglabForm.addEventListener('submit', addDGLabDevice);
  }
  
  // DG-LAB 蓝牙表单
  const dglabBluetoothForm = $('#dglab-bluetooth-form');
  if (dglabBluetoothForm) {
    dglabBluetoothForm.addEventListener('submit', addDGLabBluetoothDevice);
  }
  
  // 初始化 DG-LAB 连接方式切换
  initDGLabConnectionTabs();
  
  const yokonexForm = $('#yokonex-form');
  if (yokonexForm) {
    yokonexForm.addEventListener('submit', addYokonexDevice);
  }
  
  // YOKONEX 蓝牙表单
  const yokonexBluetoothForm = $('#yokonex-bluetooth-form');
  if (yokonexBluetoothForm) {
    yokonexBluetoothForm.addEventListener('submit', addYokonexBluetoothDevice);
  }
  
  // 初始化 YOKONEX 连接方式切换
  initYokonexConnectionTabs();
  
  const eventForm = $('#event-form');
  if (eventForm) {
    eventForm.addEventListener('submit', addEvent);
  }
  
  const settingsForm = $('#settings-form');
  if (settingsForm) {
    settingsForm.addEventListener('submit', saveSettings);
  }
  
  // 绑定脚本表单事件
  const scriptForm = $('#script-form');
  if (scriptForm) {
    scriptForm.addEventListener('submit', saveScript);
  }
  
  // 脚本模态框关闭事件
  const scriptModal = $('#script-modal');
  if (scriptModal) {
    scriptModal.addEventListener('click', (e) => {
      if (e.target === scriptModal) {
        hideScriptModal();
      }
    });
  }
  
  // 绑定滑块事件
  const sliderA = $('#channelASlider');
  if (sliderA) {
    sliderA.addEventListener('input', (e) => {
      $('#channelAStrength').textContent = e.target.value;
      const percent = (e.target.value / 200) * 100;
      $('#channelACircle').style.background = `conic-gradient(var(--primary) ${percent}%, var(--bg-input) ${percent}%)`;
    });
    sliderA.addEventListener('change', (e) => {
      setStrength('A', e.target.value);
    });
  }
  
  const sliderB = $('#channelBSlider');
  if (sliderB) {
    sliderB.addEventListener('input', (e) => {
      $('#channelBStrength').textContent = e.target.value;
      const percent = (e.target.value / 200) * 100;
      $('#channelBCircle').style.background = `conic-gradient(var(--success) ${percent}%, var(--bg-input) ${percent}%)`;
    });
    sliderB.addEventListener('change', (e) => {
      setStrength('B', e.target.value);
    });
  }
  
  // 设备选择器事件
  const deviceSelect = $('#control-device-select');
  if (deviceSelect) {
    deviceSelect.addEventListener('change', (e) => {
      state.currentDevice = e.target.value;
      if (state.currentDevice) {
        loadDeviceStrength();
      }
    });
  }
  
  // 强度调节按钮
  $$('.strength-buttons .btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const channel = btn.dataset.channel;
      const action = btn.dataset.action;
      const value = parseInt(btn.dataset.value);
      
      if (action === 'set') {
        setStrength(channel, value);
      } else if (action === 'increase') {
        adjustStrength(channel, value);
      } else if (action === 'decrease') {
        adjustStrength(channel, -value);
      }
    });
  });
  
  // 紧急停止按钮
  const stopBtn = $('#btn-stop-all');
  if (stopBtn) {
    stopBtn.addEventListener('click', async () => {
      try {
        await api('/api/control/stop', { method: 'POST' });
        showToast('已发送紧急停止指令', 'warning');
        // 重置UI显示
        updateStrengthUI('A', 0);
        updateStrengthUI('B', 0);
      } catch (error) {
        showToast('紧急停止失败', 'error');
      }
    });
  }
  
  // 波形 HEX 输入
  const waveformHex = $('#waveform-hex');
  if (waveformHex) {
    waveformHex.addEventListener('input', (e) => {
      parseWaveformHex(e.target.value);
    });
  }
  
  console.log('Device Adapter GUI initialized');
});

// 窗口调整大小时重绘波形
window.addEventListener('resize', () => {
  if (waveformCanvas) {
    waveformCanvas.width = waveformCanvas.offsetWidth;
    drawWaveform();
  }
});

// ===== 虚拟设备功能 =====
async function addVirtualDevice(type) {
  const deviceNames = {
    dglab: '虚拟郊狼',
    yokonex: '虚拟役次元'
  };
  
  try {
    const result = await api('/api/devices', {
      method: 'POST',
      body: JSON.stringify({
        type: type,
        name: deviceNames[type] || '虚拟设备',
        config: {
          connectionType: 'virtual',
          isVirtual: true
        }
      })
    });
    
    showToast(`${deviceNames[type]} 添加成功`, 'success');
    
    // 自动连接虚拟设备
    if (result.data && result.data.id) {
      await connectVirtualDevice(result.data.id, deviceNames[type]);
    }
    
    loadDevices();
  } catch (error) {
    showToast(`添加虚拟设备失败: ${error.message}`, 'error');
  }
}

// 虚拟设备连接 - 模拟连接状态
async function connectVirtualDevice(deviceId, deviceName) {
  try {
    // 调用连接API（后端会识别虚拟设备并模拟连接）
    await api(`/api/devices/${deviceId}/connect`, {
      method: 'POST'
    });
    showToast(`${deviceName} 已连接（模拟模式）`, 'success');
    
    // 将虚拟设备添加到本地状态
    state.virtualDevices = state.virtualDevices || {};
    state.virtualDevices[deviceId] = {
      id: deviceId,
      name: deviceName,
      connected: true,
      strengthA: 0,
      strengthB: 0
    };
  } catch (error) {
    // 即使后端报错，也在前端模拟连接状态
    console.log('虚拟设备本地模拟连接:', deviceId);
    state.virtualDevices = state.virtualDevices || {};
    state.virtualDevices[deviceId] = {
      id: deviceId,
      name: deviceName,
      connected: true,
      strengthA: 0,
      strengthB: 0
    };
  }
}

// ===== 最近事件功能 =====
const recentEvents = [];
const MAX_RECENT_EVENTS = 20;

function handleEventTriggered(data) {
  // 添加到最近事件
  recentEvents.unshift({
    eventId: data.eventId,
    eventName: data.eventName,
    action: data.action,
    channel: data.channel,
    value: data.value,
    devices: data.devices,
    timestamp: data.timestamp || new Date().toISOString()
  });
  
  // 限制数量
  if (recentEvents.length > MAX_RECENT_EVENTS) {
    recentEvents.pop();
  }
  
  // 更新概览页面
  renderRecentEvents();
  
  // 记录到动作日志
  if (data.devices && data.devices.length > 0) {
    data.devices.forEach(d => {
      if (d.success) {
        addActionLog({
          deviceId: d.deviceId,
          deviceName: state.devices.find(dev => dev.id === d.deviceId)?.name || d.deviceId,
          eventId: data.eventId,
          eventName: data.eventName,
          action: data.action,
          channel: data.channel,
          value: data.value,
          type: 'action'
        });
      }
    });
  }
}

function renderRecentEvents() {
  const container = $('#recent-events');
  if (!container) return;
  
  if (recentEvents.length === 0) {
    container.innerHTML = `<p class="text-muted">暂无事件记录</p>`;
    return;
  }
  
  container.innerHTML = recentEvents.map(evt => `
    <div class="event-log-item">
      <span class="event-time">${formatTime(evt.timestamp)}</span>
      <span class="event-name">${evt.eventName || evt.eventId}</span>
      <span class="event-action">${evt.action} ${evt.channel} ${evt.value}</span>
      <span class="event-devices">${evt.devices?.length || 0} 设备</span>
    </div>
  `).join('');
}

// ===== 设备动作日志功能 =====
const actionLogs = [];
const MAX_LOGS = 500;

function addActionLog(log) {
  const timestamp = new Date().toISOString();
  actionLogs.unshift({
    ...log,
    timestamp,
    id: Date.now()
  });
  
  // 限制日志数量
  if (actionLogs.length > MAX_LOGS) {
    actionLogs.pop();
  }
  
  // 如果在日志页面，刷新显示
  renderActionLogs();
}

function renderActionLogs() {
  const container = $('#device-action-logs');
  if (!container) return;
  
  const deviceFilter = $('#log-device-filter')?.value || '';
  const eventFilter = $('#log-event-filter')?.value || '';
  
  let filteredLogs = actionLogs;
  if (deviceFilter) {
    filteredLogs = filteredLogs.filter(log => log.deviceId === deviceFilter);
  }
  if (eventFilter) {
    filteredLogs = filteredLogs.filter(log => log.eventId === eventFilter);
  }
  
  if (filteredLogs.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="fas fa-history"></i>
        <p>暂无日志记录</p>
        <p class="text-muted text-sm">设备动作执行后将在此显示</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = filteredLogs.map(log => `
    <div class="log-item ${log.type || 'info'}">
      <span class="log-time">${formatTime(log.timestamp)}</span>
      <span class="log-device">${log.deviceName || log.deviceId || '未知设备'}</span>
      <span class="log-event">${log.eventName || log.eventId || '未知事件'}</span>
      <span class="log-action">${log.action || ''}</span>
      <span class="log-value">通道:${log.channel || '-'} 强度:${log.value || 0}</span>
    </div>
  `).join('');
  
  // 自动滚动
  if ($('#log-auto-scroll')?.checked) {
    container.scrollTop = 0;
  }
}

function formatTime(isoString) {
  const date = new Date(isoString);
  return date.toLocaleTimeString('zh-CN', { hour12: false }) + '.' + String(date.getMilliseconds()).padStart(3, '0');
}

function clearLogs() {
  if (confirm('确定要清空所有日志记录吗？')) {
    actionLogs.length = 0;
    renderActionLogs();
    showToast('日志已清空', 'success');
  }
}

function exportLogs() {
  const blob = new Blob([JSON.stringify(actionLogs, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `device-action-logs-${new Date().toISOString().split('T')[0]}.json`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
  showToast('日志已导出', 'success');
}

// 监听WebSocket消息，记录设备动作
const originalHandleWebSocketMessage = handleWebSocketMessage;
handleWebSocketMessage = function(data) {
  // 记录设备动作日志
  if (data.type === 'device:strength' || data.type === 'device:event') {
    const device = state.devices.find(d => d.id === data.deviceId);
    addActionLog({
      deviceId: data.deviceId,
      deviceName: device?.name || data.deviceId,
      eventId: data.eventId || '-',
      eventName: data.eventName || '-',
      action: data.action || 'set',
      channel: data.channel,
      value: data.value,
      type: 'action'
    });
  }
  
  // 处理 OCR 血量变化消息
  if (data.type === 'event' && data.event === 'ocr:bloodChange') {
    if (typeof addOCRLog === 'function') addOCRLog(data.data);
    if (typeof loadOCRStatus === 'function') loadOCRStatus();
  }
  if (data.type === 'event' && data.event === 'event:bloodTrigger') {
    console.log('血量触发设备事件:', data.data);
  }
  
  // 调用原始处理函数
  originalHandleWebSocketMessage(data);
};

// ===== 内置郊狼 WebSocket 服务器管理 =====
let coyoteRefreshTimer = null;
let coyoteWsConnection = null;
let coyoteClientId = null;

async function startCoyoteServer() {
  try {
    const port = parseInt($('#coyote-port').value) || 9999;
    const result = await api('/api/coyote/start', {
      method: 'POST',
      body: JSON.stringify({ port })
    });
    
    showToast(result.message || '服务器启动成功', 'success');
    await refreshCoyoteStatus();
    
    // 启动自动刷新
    startCoyoteAutoRefresh();
    
    // 连接到内置服务器获取客户端ID
    connectToCoyoteServer();
  } catch (error) {
    showToast(`启动失败: ${error.message}`, 'error');
  }
}

async function stopCoyoteServer() {
  try {
    // 停止自动刷新
    stopCoyoteAutoRefresh();
    
    // 断开WebSocket连接
    if (coyoteWsConnection) {
      coyoteWsConnection.close();
      coyoteWsConnection = null;
      coyoteClientId = null;
    }
    
    await api('/api/coyote/stop', { method: 'POST' });
    showToast('服务器已停止', 'success');
    refreshCoyoteStatus();
  } catch (error) {
    showToast(`停止失败: ${error.message}`, 'error');
  }
}

// 连接到内置郊狼服务器获取客户端ID
function connectToCoyoteServer() {
  const port = parseInt($('#coyote-port').value) || 9999;
  const wsUrl = `ws://localhost:${port}`;
  
  if (coyoteWsConnection && coyoteWsConnection.readyState === WebSocket.OPEN) {
    return;
  }
  
  coyoteWsConnection = new WebSocket(wsUrl);
  
  coyoteWsConnection.onopen = () => {
    console.log('已连接到内置郊狼服务器');
  };
  
  coyoteWsConnection.onmessage = (event) => {
    try {
      const msg = JSON.parse(event.data);
      if (msg.type === 'bind' && msg.message === 'targetId') {
        coyoteClientId = msg.clientId;
        console.log('获取到客户端ID:', coyoteClientId);
        // 更新UI显示二维码按钮
        updateCoyoteQRButton();
      } else if (msg.type === 'bind' && msg.message === '200') {
        showToast('APP绑定成功!', 'success');
        refreshCoyoteStatus();
      }
    } catch (e) {
      console.error('解析消息失败:', e);
    }
  };
  
  coyoteWsConnection.onclose = () => {
    console.log('与内置郊狼服务器断开连接');
    coyoteClientId = null;
    updateCoyoteQRButton();
  };
  
  coyoteWsConnection.onerror = (error) => {
    console.error('WebSocket错误:', error);
  };
}

// 更新二维码按钮状态
function updateCoyoteQRButton() {
  const btn = $('#coyote-qr-btn');
  if (btn) {
    btn.disabled = !coyoteClientId;
    btn.style.opacity = coyoteClientId ? '1' : '0.5';
  }
}

// 显示绑定二维码
async function showCoyoteQRCode() {
  if (!coyoteClientId) {
    showToast('请先启动服务器并等待连接', 'warning');
    return;
  }
  
  try {
    const result = await api(`/api/coyote/qrcode/${coyoteClientId}`);
    const { qrContent, clientId, publicUrl } = result.data;
    
    // 创建二维码弹窗
    const existingModal = document.getElementById('coyote-qrcode-modal');
    if (existingModal) existingModal.remove();
    
    const modal = document.createElement('div');
    modal.className = 'modal active';
    modal.id = 'coyote-qrcode-modal';
    modal.innerHTML = `
      <div class="modal-content">
        <div class="modal-header">
          <h3><i class="fas fa-qrcode"></i> 扫描二维码绑定设备</h3>
          <button class="modal-close" onclick="closeCoyoteQRModal()">
            <i class="fas fa-times"></i>
          </button>
        </div>
        <div class="modal-body" style="text-align: center;">
          <div id="coyote-qrcode-container" style="display: inline-block; padding: 20px; background: white; border-radius: 8px;"></div>
          <p style="margin-top: 15px; color: var(--text-secondary);">
            使用 DG-LAB APP 扫描此二维码完成绑定
          </p>
          <p class="text-muted text-sm">
            客户端 ID: ${clientId}
          </p>
          <p class="text-muted text-sm">
            服务器地址: ${publicUrl}
          </p>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeCoyoteQRModal()">关闭</button>
        </div>
      </div>
    `;
    document.body.appendChild(modal);
    
    // 生成二维码
    if (typeof QRCode !== 'undefined') {
      new QRCode(document.getElementById('coyote-qrcode-container'), {
        text: qrContent,
        width: 200,
        height: 200
      });
    } else {
      document.getElementById('coyote-qrcode-container').innerHTML = `
        <p style="color: #333;">二维码内容:</p>
        <textarea readonly style="width: 100%; height: 80px; font-size: 10px; color: #333;">${qrContent}</textarea>
      `;
    }
  } catch (error) {
    showToast(`生成二维码失败: ${error.message}`, 'error');
  }
}

function closeCoyoteQRModal() {
  const modal = document.getElementById('coyote-qrcode-modal');
  if (modal) modal.remove();
}

// 启动自动刷新
function startCoyoteAutoRefresh() {
  stopCoyoteAutoRefresh();
  coyoteRefreshTimer = setInterval(refreshCoyoteStatus, 3000);
}

// 停止自动刷新
function stopCoyoteAutoRefresh() {
  if (coyoteRefreshTimer) {
    clearInterval(coyoteRefreshTimer);
    coyoteRefreshTimer = null;
  }
}

async function refreshCoyoteStatus() {
  try {
    const result = await api('/api/coyote/status');
    const status = result.data;
    
    const statusEl = $('#coyote-status');
    const clientsContainer = $('#coyote-clients');
    const clientList = $('#coyote-client-list');
    
    if (status.running) {
      // 过滤掉前端自己的连接（coyoteClientId 是本页面连接的ID）
      const filteredClients = status.clients ? status.clients.filter(c => c.id !== coyoteClientId) : [];
      
      statusEl.innerHTML = `
        <i class="fas fa-circle" style="font-size: 10px; color: var(--success);"></i>
        <span>运行中 - ${status.url}</span>
        <span style="margin-left: auto; font-size: 12px; color: var(--text-muted);">
          ${filteredClients.length} 个客户端
        </span>
      `;
      
      // 显示客户端列表
      if (filteredClients.length > 0) {
        clientsContainer.style.display = 'block';
        clientList.innerHTML = filteredClients.map(c => `
          <div class="device-item">
            <div class="device-icon">
              <i class="fas ${c.type === 'app' ? 'fa-mobile-alt' : 'fa-desktop'}"></i>
            </div>
            <div class="device-info">
              <div class="device-name">${c.id.substring(0, 12)}...</div>
              <div class="device-type">${c.type === 'app' ? 'DG-LAB APP' : c.type === 'third' ? '第三方程序' : '未知'}</div>
            </div>
            <div class="device-status ${c.connected ? 'connected' : ''}">
              <i class="fas fa-circle"></i>
              ${c.connected ? (c.boundTo ? '已绑定' : '已连接') : '已断开'}
            </div>
            ${c.type !== 'app' && c.connected && !c.boundTo ? `
              <button class="btn btn-sm btn-primary" onclick="showCoyoteQRCodeForClient('${c.id}')" title="显示二维码">
                <i class="fas fa-qrcode"></i>
              </button>
            ` : ''}
          </div>
        `).join('');
      } else {
        clientsContainer.style.display = 'block';
        clientList.innerHTML = '<div class="empty-state"><p>暂无连接的客户端</p></div>';
      }
      
      // 确保WebSocket连接
      if (!coyoteWsConnection || coyoteWsConnection.readyState !== WebSocket.OPEN) {
        connectToCoyoteServer();
      }
    } else {
      statusEl.innerHTML = `
        <i class="fas fa-circle" style="font-size: 10px; color: var(--danger);"></i>
        <span>未启动</span>
      `;
      clientsContainer.style.display = 'none';
      stopCoyoteAutoRefresh();
    }
  } catch (error) {
    console.error('获取服务器状态失败:', error);
  }
}

// 为特定客户端显示二维码
async function showCoyoteQRCodeForClient(clientId) {
  try {
    const result = await api(`/api/coyote/qrcode/${clientId}`);
    const { qrContent, publicUrl } = result.data;
    
    // 创建二维码弹窗
    const existingModal = document.getElementById('coyote-qrcode-modal');
    if (existingModal) existingModal.remove();
    
    const modal = document.createElement('div');
    modal.className = 'modal active';
    modal.id = 'coyote-qrcode-modal';
    modal.innerHTML = `
      <div class="modal-content">
        <div class="modal-header">
          <h3><i class="fas fa-qrcode"></i> 扫描二维码绑定设备</h3>
          <button class="modal-close" onclick="closeCoyoteQRModal()">
            <i class="fas fa-times"></i>
          </button>
        </div>
        <div class="modal-body" style="text-align: center;">
          <div id="coyote-qrcode-container" style="display: inline-block; padding: 20px; background: white; border-radius: 8px;"></div>
          <p style="margin-top: 15px; color: var(--text-secondary);">
            使用 DG-LAB APP 扫描此二维码完成绑定
          </p>
          <p class="text-muted text-sm">
            客户端 ID: ${clientId}
          </p>
          <p class="text-muted text-sm">
            服务器地址: ${publicUrl}
          </p>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" onclick="closeCoyoteQRModal()">关闭</button>
        </div>
      </div>
    `;
    document.body.appendChild(modal);
    
    // 生成二维码
    if (typeof QRCode !== 'undefined') {
      new QRCode(document.getElementById('coyote-qrcode-container'), {
        text: qrContent,
        width: 200,
        height: 200
      });
    }
  } catch (error) {
    showToast(`生成二维码失败: ${error.message}`, 'error');
  }
}

// 页面加载时检查服务器状态
document.addEventListener('DOMContentLoaded', () => {
  setTimeout(async () => {
    await refreshCoyoteStatus();
    // 如果服务器在运行，启动自动刷新
    try {
      const result = await api('/api/coyote/status');
      if (result.data && result.data.running) {
        startCoyoteAutoRefresh();
        connectToCoyoteServer();
      }
    } catch (e) {}
  }, 1000);
});

// ===== OCR 血量识别功能 =====

let ocrRules = [];
let ocrBloodLogs = [];

/**
 * 加载 OCR 状态
 */
async function loadOCRStatus() {
  try {
    const result = await api('/api/ocr/status');
    const status = result.data;
    
    // 更新状态显示
    $('#ocr-current-blood').textContent = status.lastBloodValue;
    $('#ocr-current-mode').textContent = status.config.mode === 'healthbar' ? '血条识别' : 
                                          status.config.mode === 'digital' ? '数字OCR' : '自动检测';
    $('#ocr-running-status').textContent = status.isRunning ? '运行中' : '未运行';
    $('#ocr-running-status').style.color = status.isRunning ? 'var(--success)' : 'var(--danger)';
    
    // 更新护甲显示
    if (status.armorEnabled) {
      $('#ocr-armor-status').style.display = 'block';
      $('#ocr-current-armor').textContent = status.lastArmorValue || 0;
    } else {
      $('#ocr-armor-status').style.display = 'none';
    }
    
    // 更新配置表单
    $('#ocr-mode').value = status.config.mode;
    $('#ocr-initial-blood').value = status.config.initialBlood;
    $('#ocr-interval').value = status.config.interval;
    
    if (status.config.healthbar) {
      $('#ocr-healthbar-color').value = status.config.healthbar.color || 'auto';
      $('#ocr-tolerance').value = status.config.healthbar.tolerance || 30;
      $('#ocr-sample-rows').value = status.config.healthbar.sampleRows || 3;
    }
    
    // 更新护甲配置表单
    if (status.config.armor) {
      $('#ocr-armor-enabled').checked = status.config.armor.enabled;
      updateArmorConfigFields();
      if (status.config.armor.enabled) {
        $('#ocr-initial-armor').value = status.config.armor.initialArmor || 0;
        if (status.config.armor.area) {
          $('#ocr-armor-area-x').value = status.config.armor.area.x || 0;
          $('#ocr-armor-area-y').value = status.config.armor.area.y || 0;
          $('#ocr-armor-area-width').value = status.config.armor.area.width || 100;
          $('#ocr-armor-area-height').value = status.config.armor.area.height || 20;
        }
        if (status.config.armor.healthbar) {
          $('#ocr-armor-color').value = status.config.armor.healthbar.color || 'blue';
        }
      }
    }
    
    // 加载规则
    await loadOCRRules();
  } catch (error) {
    console.error('加载 OCR 状态失败:', error);
  }
}

/**
 * 更新护甲配置字段显示
 */
function updateArmorConfigFields() {
  const enabled = $('#ocr-armor-enabled').checked;
  $('#armor-config-fields').style.display = enabled ? 'block' : 'none';
}

// 绑定护甲开关事件
document.addEventListener('DOMContentLoaded', () => {
  const armorSwitch = document.getElementById('ocr-armor-enabled');
  if (armorSwitch) {
    armorSwitch.addEventListener('change', updateArmorConfigFields);
  }
});

/**
 * 加载 OCR 规则
 */
async function loadOCRRules() {
  try {
    const result = await api('/api/ocr/rules');
    ocrRules = result.data || [];
    
    // 更新规则计数
    const enabledCount = ocrRules.filter(r => r.enabled).length;
    $('#ocr-enabled-rules').textContent = `${enabledCount}/${ocrRules.length}`;
    
    renderOCRRules();
  } catch (error) {
    console.error('加载规则失败:', error);
    showToast('加载规则失败', 'error');
  }
}

/**
 * 渲染规则列表
 */
function renderOCRRules() {
  const container = $('#ocr-rules-list');
  
  if (ocrRules.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="fas fa-list-alt"></i>
        <p>暂无规则</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = ocrRules.map(rule => `
    <div class="rule-item ${rule.enabled ? '' : 'disabled'}" data-id="${rule.id}">
      <div class="rule-toggle">
        <input type="checkbox" ${rule.enabled ? 'checked' : ''} onchange="toggleOCRRule('${rule.id}', this.checked)">
      </div>
      <div class="rule-info">
        <div class="rule-name">
          <span class="priority-badge">#${rule.priority}</span>
          ${rule.name}
        </div>
        <div class="rule-details">
          <span class="condition-tag">${getConditionText(rule.conditions)}</span>
          <span class="action-tag">
            <i class="fas fa-bolt"></i> ${rule.action.eventId}
            ${rule.action.strength ? ` | 强度: ${rule.action.strength}` : ''}
            ${rule.action.duration ? ` | ${rule.action.duration}ms` : ''}
          </span>
        </div>
      </div>
      <div class="rule-actions">
        <button class="btn btn-sm btn-secondary" onclick="editOCRRule('${rule.id}')" title="编辑">
          <i class="fas fa-edit"></i>
        </button>
        <button class="btn btn-sm btn-danger" onclick="deleteOCRRule('${rule.id}')" title="删除">
          <i class="fas fa-trash"></i>
        </button>
      </div>
    </div>
  `).join('');
}

/**
 * 获取条件描述文本
 */
function getConditionText(conditions) {
  switch (conditions.type) {
    case 'death':
      return '角色死亡 (血量归零)';
    case 'decrease':
      if (conditions.minChange && conditions.maxChange) {
        return `血量减少 ${conditions.minChange}-${conditions.maxChange}%`;
      } else if (conditions.minChange) {
        return `血量减少 ≥${conditions.minChange}%`;
      }
      return '血量减少';
    case 'increase':
      if (conditions.minChange && conditions.maxChange) {
        return `血量恢复 ${conditions.minChange}-${conditions.maxChange}%`;
      } else if (conditions.minChange) {
        return `血量恢复 ≥${conditions.minChange}%`;
      }
      return '血量恢复';
    case 'revive':
      return '角色复活';
    case 'threshold':
      return `血量${conditions.direction === 'below' ? '低于' : '高于'} ${conditions.threshold}%`;
    default:
      return '未知条件';
  }
}

/**
 * 切换规则启用状态
 */
async function toggleOCRRule(id, enabled) {
  try {
    await api(`/api/ocr/rules/${id}/toggle`, {
      method: 'POST',
      body: JSON.stringify({ enabled })
    });
    showToast(`规则已${enabled ? '启用' : '禁用'}`, 'success');
    await loadOCRRules();
  } catch (error) {
    showToast('操作失败: ' + error.message, 'error');
    await loadOCRRules();
  }
}

/**
 * 删除规则
 */
async function deleteOCRRule(id) {
  if (!confirm('确定要删除这条规则吗？')) return;
  
  try {
    await api(`/api/ocr/rules/${id}`, { method: 'DELETE' });
    showToast('规则已删除', 'success');
    await loadOCRRules();
  } catch (error) {
    showToast('删除失败: ' + error.message, 'error');
  }
}

/**
 * 编辑规则
 */
function editOCRRule(id) {
  const rule = ocrRules.find(r => r.id === id);
  if (!rule) return;
  
  // 填充表单
  $('#rule-modal-title').textContent = '编辑规则';
  $('#rule-name').value = rule.name;
  $('#rule-condition-type').value = rule.conditions.type;
  $('#rule-min-change').value = rule.conditions.minChange || 1;
  $('#rule-max-change').value = rule.conditions.maxChange || 100;
  $('#rule-threshold').value = rule.conditions.threshold || 20;
  $('#rule-threshold-direction').value = rule.conditions.direction || 'below';
  $('#rule-event-id').value = rule.action.eventId;
  $('#rule-strength').value = rule.action.strength || 50;
  $('#rule-duration').value = rule.action.duration || 500;
  $('#rule-channel').value = rule.action.channel || 'A';
  $('#rule-priority').value = rule.priority;
  $('#rule-edit-id').value = id;
  
  updateConditionFields();
  $('#rule-modal').classList.add('active');
}

/**
 * 显示添加规则模态框
 */
function showAddRuleModal() {
  $('#rule-modal-title').textContent = '添加规则';
  $('#rule-form').reset();
  $('#rule-edit-id').value = '';
  $('#rule-condition-type').value = 'decrease';
  updateConditionFields();
  $('#rule-modal').classList.add('active');
}

/**
 * 关闭规则模态框
 */
function closeRuleModal() {
  $('#rule-modal').classList.remove('active');
}

/**
 * 更新条件字段显示
 */
function updateConditionFields() {
  const type = $('#rule-condition-type').value;
  const rangeFields = $('#condition-range-fields');
  const thresholdFields = $('#condition-threshold-fields');
  
  if (type === 'decrease' || type === 'increase') {
    rangeFields.style.display = 'flex';
    thresholdFields.style.display = 'none';
  } else if (type === 'threshold') {
    rangeFields.style.display = 'none';
    thresholdFields.style.display = 'flex';
  } else {
    rangeFields.style.display = 'none';
    thresholdFields.style.display = 'none';
  }
}

/**
 * 保存规则
 */
async function saveRule(event) {
  event.preventDefault();
  
  const editId = $('#rule-edit-id').value;
  const conditionType = $('#rule-condition-type').value;
  
  const rule = {
    id: editId || `rule_${Date.now()}`,
    name: $('#rule-name').value,
    enabled: true,
    priority: parseInt($('#rule-priority').value),
    conditions: {
      type: conditionType
    },
    action: {
      eventId: $('#rule-event-id').value,
      strength: parseInt($('#rule-strength').value),
      duration: parseInt($('#rule-duration').value),
      channel: $('#rule-channel').value
    }
  };
  
  // 添加条件参数
  if (conditionType === 'decrease' || conditionType === 'increase') {
    rule.conditions.minChange = parseInt($('#rule-min-change').value);
    rule.conditions.maxChange = parseInt($('#rule-max-change').value);
  } else if (conditionType === 'threshold') {
    rule.conditions.threshold = parseInt($('#rule-threshold').value);
    rule.conditions.direction = $('#rule-threshold-direction').value;
  }
  
  try {
    if (editId) {
      await api(`/api/ocr/rules/${editId}`, {
        method: 'PATCH',
        body: JSON.stringify(rule)
      });
      showToast('规则已更新', 'success');
    } else {
      await api('/api/ocr/rules', {
        method: 'POST',
        body: JSON.stringify(rule)
      });
      showToast('规则已添加', 'success');
    }
    
    closeRuleModal();
    await loadOCRRules();
  } catch (error) {
    showToast('保存失败: ' + error.message, 'error');
  }
}

/**
 * 重置规则为默认值
 */
async function resetOCRRulesToDefault() {
  if (!confirm('确定要将所有规则重置为默认值吗？')) return;
  
  try {
    await api('/api/ocr/rules/reset', { method: 'POST' });
    showToast('规则已重置为默认值', 'success');
    await loadOCRRules();
  } catch (error) {
    showToast('重置失败: ' + error.message, 'error');
  }
}

/**
 * 保存 OCR 配置
 */
async function saveOCRConfig() {
  try {
    const armorEnabled = $('#ocr-armor-enabled').checked;
    
    const config = {
      mode: $('#ocr-mode').value,
      initialBlood: parseInt($('#ocr-initial-blood').value),
      interval: parseInt($('#ocr-interval').value),
      healthbar: {
        color: $('#ocr-healthbar-color').value,
        tolerance: parseInt($('#ocr-tolerance').value),
        sampleRows: parseInt($('#ocr-sample-rows').value),
        edgeDetection: true
      },
      armor: {
        enabled: armorEnabled,
        initialArmor: armorEnabled ? parseInt($('#ocr-initial-armor').value) : 0,
        area: armorEnabled ? {
          x: parseInt($('#ocr-armor-area-x').value),
          y: parseInt($('#ocr-armor-area-y').value),
          width: parseInt($('#ocr-armor-area-width').value),
          height: parseInt($('#ocr-armor-area-height').value)
        } : { x: 0, y: 0, width: 100, height: 20 },
        healthbar: armorEnabled ? {
          color: $('#ocr-armor-color').value,
          tolerance: 30,
          sampleRows: 3,
          edgeDetection: true
        } : undefined
      }
    };
    
    await api('/api/ocr/configure', {
      method: 'POST',
      body: JSON.stringify(config)
    });
    
    showToast('OCR 配置已保存', 'success');
    await loadOCRStatus();
  } catch (error) {
    showToast('保存配置失败: ' + error.message, 'error');
  }
}

/**
 * 重置 OCR 状态
 */
async function resetOCRStatus() {
  try {
    await api('/api/ocr/reset', { method: 'POST' });
    showToast('OCR 状态已重置', 'success');
    await loadOCRStatus();
  } catch (error) {
    showToast('重置失败: ' + error.message, 'error');
  }
}

// OCR 识别状态
let ocrIsRunning = false;
let ocrArea = null;

/**
 * 初始化 Electron OCR IPC 监听
 */
function initElectronOCR() {
  if (typeof require === 'undefined') return;
  
  try {
    const { ipcRenderer } = require('electron');
    
    // 监听坐标数据
    ipcRenderer.on('coordinate-data', (event, area) => {
      if (area) {
        // 根据目标类型处理
        const target = window._pickerTarget || 'health';
        
        if (target === 'armor') {
          // 护甲区域
          $('#ocr-armor-area-x').value = area.x;
          $('#ocr-armor-area-y').value = area.y;
          $('#ocr-armor-area-width').value = area.width;
          $('#ocr-armor-area-height').value = area.height;
          showToast(`护甲区域已设置: ${area.width}×${area.height}`, 'success');
          ipcRenderer.invoke('ocr-set-armor-area', area);
        } else {
          // 血量区域
          ocrArea = area;
          updateOCRAreaPreview(area);
          showToast(`血量区域已设置: ${area.width}×${area.height}`, 'success');
          ipcRenderer.invoke('ocr-set-area', area);
        }
        
        window._pickerTarget = 'health'; // 重置
      }
    });
    
    // 监听 OCR 状态更新
    ipcRenderer.on('ocr-state-update', (event, state) => {
      ocrIsRunning = state.isRunning;
      ocrArea = state.area;
      updateOCRUI(state);
    });
    
    // 监听 OCR 帧数据（用于识别）
    ipcRenderer.on('ocr-frame', (event, data) => {
      processOCRFrame(data);
    });
    
    // 获取初始状态
    ipcRenderer.invoke('ocr-get-state').then(state => {
      if (state) {
        ocrIsRunning = state.isRunning;
        ocrArea = state.area;
        updateOCRUI(state);
      }
    });
    
    console.log('Electron OCR IPC initialized');
  } catch (e) {
    console.warn('Failed to init Electron OCR:', e);
  }
}

/**
 * 更新 OCR 区域预览
 */
function updateOCRAreaPreview(area) {
  const preview = $('#ocr-area-preview');
  if (!preview) return;
  
  preview.style.display = 'block';
  $('#ocr-area-x').textContent = area.x;
  $('#ocr-area-y').textContent = area.y;
  $('#ocr-area-w').textContent = area.width;
  $('#ocr-area-h').textContent = area.height;
}

/**
 * 更新 OCR UI 状态
 */
function updateOCRUI(state) {
  const startBtn = $('#ocr-start-btn');
  const statusEl = $('#ocr-running-status');
  
  if (startBtn) {
    if (state.isRunning) {
      startBtn.innerHTML = '<i class="fas fa-stop"></i> 停止识别';
      startBtn.classList.remove('btn-success');
      startBtn.classList.add('btn-danger');
    } else {
      startBtn.innerHTML = '<i class="fas fa-play"></i> 开始识别';
      startBtn.classList.remove('btn-danger');
      startBtn.classList.add('btn-success');
    }
  }
  
  if (statusEl) {
    if (state.isRunning) {
      statusEl.textContent = '运行中';
      statusEl.style.color = 'var(--success)';
    } else {
      statusEl.textContent = '未运行';
      statusEl.style.color = 'var(--danger)';
    }
  }
  
  if (state.area) {
    updateOCRAreaPreview(state.area);
  }
}

/**
 * 处理 OCR 帧数据
 */
async function processOCRFrame(data) {
  const { healthImageData, armorImageData, mode, armorEnabled, healthColor, armorColor, tolerance, sampleRows } = data;
  
  // 处理血量图像
  const healthImg = new Image();
  healthImg.onload = async () => {
    const canvas = document.createElement('canvas');
    canvas.width = healthImg.width;
    canvas.height = healthImg.height;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(healthImg, 0, 0);
    
    // 更新预览
    const previewCanvas = $('#ocr-preview-canvas');
    if (previewCanvas) {
      previewCanvas.width = Math.min(300, healthImg.width);
      previewCanvas.height = Math.min(60, healthImg.height);
      const pctx = previewCanvas.getContext('2d');
      pctx.drawImage(healthImg, 0, 0, previewCanvas.width, previewCanvas.height);
    }
    
    // 根据模式识别血量
    let bloodValue = null;
    
    if (mode === 'healthbar') {
      bloodValue = analyzeHealthBar(canvas, healthColor, tolerance, sampleRows);
    } else if (mode === 'digital') {
      bloodValue = analyzeDigitalNumber(canvas);
    } else if (mode === 'auto') {
      // 自动检测：先尝试数字识别，失败则用血条识别
      bloodValue = analyzeDigitalNumber(canvas);
      if (bloodValue === null) {
        bloodValue = analyzeHealthBar(canvas, healthColor, tolerance, sampleRows);
      }
    }
    
    if (bloodValue !== null) {
      try {
        await api('/api/ocr/report-blood', {
          method: 'POST',
          body: JSON.stringify({ value: bloodValue, source: 'electron-ocr' })
        });
        $('#ocr-current-blood').textContent = bloodValue;
        $('#ocr-current-mode').textContent = getModeText(mode);
      } catch (e) {
        console.warn('Failed to report blood:', e);
      }
    }
  };
  healthImg.src = healthImageData;
  
  // 处理护甲图像（如果启用）
  if (armorEnabled && armorImageData) {
    const armorImg = new Image();
    armorImg.onload = async () => {
      const canvas = document.createElement('canvas');
      canvas.width = armorImg.width;
      canvas.height = armorImg.height;
      const ctx = canvas.getContext('2d');
      ctx.drawImage(armorImg, 0, 0);
      
      // 识别护甲
      const armorValue = analyzeArmorBar(canvas, armorColor, tolerance, sampleRows);
      
      if (armorValue !== null) {
        try {
          await api('/api/ocr/report-armor', {
            method: 'POST',
            body: JSON.stringify({ value: armorValue, source: 'electron-ocr' })
          });
          $('#ocr-current-armor').textContent = armorValue;
          $('#ocr-armor-status').style.display = 'block';
        } catch (e) {
          console.warn('Failed to report armor:', e);
        }
      }
    };
    armorImg.src = armorImageData;
  }
}

/**
 * 获取模式显示文本
 */
function getModeText(mode) {
  const modeTexts = {
    'healthbar': '血条识别',
    'digital': '数字OCR',
    'auto': '自动检测'
  };
  return modeTexts[mode] || mode;
}

/**
 * 分析血条图像（颜色识别）
 */
function analyzeHealthBar(canvas, targetColor, tolerance, sampleRows) {
  const ctx = canvas.getContext('2d');
  const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
  const data = imageData.data;
  
  targetColor = targetColor || 'auto';
  tolerance = tolerance || 30;
  sampleRows = sampleRows || 3;
  
  let healthPixels = 0;
  let totalPixels = 0;
  
  const rowStep = Math.floor(canvas.height / (sampleRows + 1));
  
  for (let row = 1; row <= sampleRows; row++) {
    const y = row * rowStep;
    for (let x = 0; x < canvas.width; x++) {
      const idx = (y * canvas.width + x) * 4;
      const r = data[idx];
      const g = data[idx + 1];
      const b = data[idx + 2];
      
      totalPixels++;
      
      let isHealth = false;
      
      // 红色血条
      if ((targetColor === 'auto' || targetColor === 'red') && !isHealth) {
        if (r > 150 && r > g + tolerance && r > b + tolerance) {
          isHealth = true;
        }
      }
      // 绿色血条
      if ((targetColor === 'auto' || targetColor === 'green') && !isHealth) {
        if (g > 150 && g > r + tolerance && g > b + tolerance) {
          isHealth = true;
        }
      }
      // 黄色血条
      if ((targetColor === 'auto' || targetColor === 'yellow') && !isHealth) {
        if (r > 150 && g > 150 && b < 100) {
          isHealth = true;
        }
      }
      // 蓝色血条
      if ((targetColor === 'auto' || targetColor === 'blue') && !isHealth) {
        if (b > 150 && b > r + tolerance && b > g + tolerance) {
          isHealth = true;
        }
      }
      // 橙色血条
      if ((targetColor === 'auto' || targetColor === 'orange') && !isHealth) {
        if (r > 180 && g > 80 && g < 180 && b < 100) {
          isHealth = true;
        }
      }
      
      if (isHealth) healthPixels++;
    }
  }
  
  if (totalPixels === 0) return null;
  
  const percentage = Math.round((healthPixels / totalPixels) * 100);
  return Math.min(100, Math.max(0, percentage));
}

/**
 * 分析护甲条图像
 */
function analyzeArmorBar(canvas, targetColor, tolerance, sampleRows) {
  const ctx = canvas.getContext('2d');
  const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
  const data = imageData.data;
  
  targetColor = targetColor || 'blue';
  tolerance = tolerance || 30;
  sampleRows = sampleRows || 3;
  
  let armorPixels = 0;
  let totalPixels = 0;
  
  const rowStep = Math.floor(canvas.height / (sampleRows + 1));
  
  for (let row = 1; row <= sampleRows; row++) {
    const y = row * rowStep;
    for (let x = 0; x < canvas.width; x++) {
      const idx = (y * canvas.width + x) * 4;
      const r = data[idx];
      const g = data[idx + 1];
      const b = data[idx + 2];
      
      totalPixels++;
      
      let isArmor = false;
      
      // 蓝色护甲
      if ((targetColor === 'auto' || targetColor === 'blue') && !isArmor) {
        if (b > 150 && b > r + tolerance && b > g + tolerance) {
          isArmor = true;
        }
      }
      // 黄色护甲
      if ((targetColor === 'auto' || targetColor === 'yellow') && !isArmor) {
        if (r > 150 && g > 150 && b < 100) {
          isArmor = true;
        }
      }
      // 白色护甲
      if ((targetColor === 'auto' || targetColor === 'white') && !isArmor) {
        if (r > 200 && g > 200 && b > 200) {
          isArmor = true;
        }
      }
      
      if (isArmor) armorPixels++;
    }
  }
  
  if (totalPixels === 0) return null;
  
  const percentage = Math.round((armorPixels / totalPixels) * 100);
  return Math.min(100, Math.max(0, percentage));
}

/**
 * 分析数字图像（数字OCR）
 */
function analyzeDigitalNumber(canvas) {
  const ctx = canvas.getContext('2d');
  const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
  const data = imageData.data;
  
  // 二值化处理
  const binary = [];
  const threshold = 128;
  
  for (let y = 0; y < canvas.height; y++) {
    binary[y] = [];
    for (let x = 0; x < canvas.width; x++) {
      const idx = (y * canvas.width + x) * 4;
      const gray = (data[idx] + data[idx + 1] + data[idx + 2]) / 3;
      binary[y][x] = gray > threshold ? 1 : 0;
    }
  }
  
  // 寻找数字区域（通过垂直投影）
  const verticalProjection = [];
  for (let x = 0; x < canvas.width; x++) {
    let count = 0;
    for (let y = 0; y < canvas.height; y++) {
      count += binary[y][x];
    }
    verticalProjection.push(count);
  }
  
  // 分割数字
  const segments = [];
  let inDigit = false;
  let start = 0;
  
  for (let x = 0; x < verticalProjection.length; x++) {
    const hasPixels = verticalProjection[x] > canvas.height * 0.1;
    if (hasPixels && !inDigit) {
      inDigit = true;
      start = x;
    } else if (!hasPixels && inDigit) {
      inDigit = false;
      if (x - start > 3) { // 最小宽度
        segments.push({ start, end: x });
      }
    }
  }
  
  if (inDigit) {
    segments.push({ start, end: canvas.width });
  }
  
  // 识别每个数字（简化的特征匹配）
  let result = '';
  for (const seg of segments) {
    const digit = recognizeDigit(binary, seg.start, seg.end, canvas.height);
    if (digit !== null) {
      result += digit;
    }
  }
  
  if (result === '') return null;
  
  const value = parseInt(result);
  return isNaN(value) ? null : Math.min(200, Math.max(0, value));
}

/**
 * 识别单个数字（基于简单特征）
 */
function recognizeDigit(binary, startX, endX, height) {
  const width = endX - startX;
  if (width < 3 || height < 5) return null;
  
  // 计算特征
  let topPixels = 0, midPixels = 0, bottomPixels = 0;
  let leftPixels = 0, rightPixels = 0, centerPixels = 0;
  
  const midY = Math.floor(height / 2);
  const midX = Math.floor((startX + endX) / 2);
  
  for (let y = 0; y < height; y++) {
    for (let x = startX; x < endX; x++) {
      const pixel = binary[y] && binary[y][x] ? 1 : 0;
      
      if (y < height / 3) topPixels += pixel;
      else if (y < height * 2 / 3) midPixels += pixel;
      else bottomPixels += pixel;
      
      if (x < midX) leftPixels += pixel;
      else rightPixels += pixel;
    }
  }
  
  // 基于特征简单判断
  const totalPixels = topPixels + midPixels + bottomPixels;
  if (totalPixels < 5) return null;
  
  const topRatio = topPixels / totalPixels;
  const midRatio = midPixels / totalPixels;
  const bottomRatio = bottomPixels / totalPixels;
  const lrRatio = leftPixels / (leftPixels + rightPixels + 1);
  
  // 简化的数字识别规则
  if (width < height * 0.4 && lrRatio > 0.4 && lrRatio < 0.6) return '1';
  if (topRatio > 0.35 && bottomRatio > 0.35 && midRatio < 0.3) return '0';
  if (topRatio < 0.25 && bottomRatio > 0.35) return '7';
  if (midRatio > 0.4) return '8';
  
  // 默认返回 null 表示无法识别
  return null;
}

/**
 * 切换 OCR 识别状态
 */
async function toggleOCRRecognition() {
  if (typeof require === 'undefined') {
    showToast('请在 Electron 桌面应用中使用此功能', 'warning');
    return;
  }
  
  try {
    const { ipcRenderer } = require('electron');
    
    if (ocrIsRunning) {
      await ipcRenderer.invoke('ocr-stop');
      showToast('识别已停止', 'info');
    } else {
      if (!ocrArea) {
        showToast('请先框选血量识别区域', 'warning');
        return;
      }
      
      // 收集所有配置
      const config = {
        interval: parseInt($('#ocr-interval')?.value) || 200,
        mode: $('#ocr-mode')?.value || 'healthbar',
        healthColor: $('#ocr-healthbar-color')?.value || 'auto',
        tolerance: parseInt($('#ocr-tolerance')?.value) || 30,
        sampleRows: parseInt($('#ocr-sample-rows')?.value) || 3,
        armorEnabled: $('#ocr-armor-enabled')?.checked || false,
        armorColor: $('#ocr-armor-color')?.value || 'blue'
      };
      
      // 检查护甲配置
      if (config.armorEnabled) {
        const armorArea = {
          x: parseInt($('#ocr-armor-area-x')?.value) || 0,
          y: parseInt($('#ocr-armor-area-y')?.value) || 0,
          width: parseInt($('#ocr-armor-area-width')?.value) || 100,
          height: parseInt($('#ocr-armor-area-height')?.value) || 20
        };
        
        if (armorArea.width < 10 || armorArea.height < 5) {
          showToast('护甲区域设置无效', 'warning');
          return;
        }
        
        await ipcRenderer.invoke('ocr-set-armor-area', armorArea);
      }
      
      // 发送配置
      await ipcRenderer.invoke('ocr-set-config', config);
      
      // 开始识别
      const result = await ipcRenderer.invoke('ocr-start');
      if (result.success) {
        const modeText = getModeText(config.mode);
        showToast(`开始${modeText}` + (config.armorEnabled ? ' (含护甲)' : ''), 'success');
      } else {
        showToast(result.error || '启动失败', 'error');
      }
    }
  } catch (e) {
    showToast('操作失败: ' + e.message, 'error');
  }
}

/**
 * 打开坐标拾取工具
 */
function openCoordinatePicker() {
  if (typeof require === 'undefined') {
    showToast('请在 Electron 桌面应用中使用此功能', 'warning');
    return;
  }
  
  try {
    const { ipcRenderer } = require('electron');
    window._pickerTarget = 'health'; // 标记为血量区域
    ipcRenderer.invoke('open-coordinate-picker');
    showToast('请在屏幕上框选血量识别区域', 'info');
  } catch (e) {
    showToast('打开失败: ' + e.message, 'error');
  }
}

/**
 * 打开护甲区域拾取工具
 */
function openArmorAreaPicker() {
  if (typeof require === 'undefined') {
    showToast('请在 Electron 桌面应用中使用此功能', 'warning');
    return;
  }
  
  try {
    const { ipcRenderer } = require('electron');
    window._pickerTarget = 'armor'; // 标记为护甲区域
    ipcRenderer.invoke('open-coordinate-picker');
    showToast('请在屏幕上框选护甲识别区域', 'info');
  } catch (e) {
    showToast('打开失败: ' + e.message, 'error');
  }
}

/**
 * 切换护甲配置显示
 */
function toggleArmorConfig() {
  const checkbox = $('#ocr-armor-enabled');
  const fields = $('#armor-config-fields');
  const armorStatus = $('#ocr-armor-status');
  
  if (checkbox && fields) {
    fields.style.display = checkbox.checked ? 'block' : 'none';
  }
  if (armorStatus) {
    armorStatus.style.display = checkbox && checkbox.checked ? 'block' : 'none';
  }
}

/**
 * 切换识别模式时显示/隐藏相关配置
 */
function onOCRModeChange() {
  const mode = $('#ocr-mode')?.value;
  const healthbarConfig = $('#healthbar-config');
  
  if (healthbarConfig) {
    // 数字OCR模式时隐藏血条颜色配置
    healthbarConfig.style.display = (mode === 'digital') ? 'none' : 'block';
  }
  
  // 更新模式显示
  $('#ocr-current-mode').textContent = getModeText(mode);
}

/**
 * 手动上报血量
 */
async function reportBloodManually() {
  const value = parseInt($('#ocr-manual-blood').value);
  
  if (isNaN(value) || value < 0 || value > 200) {
    showToast('请输入 0-200 之间的数值', 'warning');
    return;
  }
  
  try {
    const result = await api('/api/ocr/report-blood', {
      method: 'POST',
      body: JSON.stringify({ value })
    });
    
    showToast(`血量已上报: ${value}`, 'success');
    
    // 添加到日志
    if (result.data && result.data.changeEvent) {
      addOCRLog(result.data.changeEvent);
    }
    
    await loadOCRStatus();
  } catch (error) {
    showToast('上报失败: ' + error.message, 'error');
  }
}

/**
 * 快速设置血量
 */
function setQuickBlood(value) {
  $('#ocr-manual-blood').value = value;
  $('#ocr-manual-blood-slider').value = value;
  reportBloodManually();
}

/**
 * 添加 OCR 日志
 */
function addOCRLog(event) {
  ocrBloodLogs.unshift({
    ...event,
    timestamp: new Date()
  });
  
  // 限制日志数量
  if (ocrBloodLogs.length > 50) {
    ocrBloodLogs = ocrBloodLogs.slice(0, 50);
  }
  
  renderOCRLogs();
}

/**
 * 渲染 OCR 日志
 */
function renderOCRLogs() {
  const container = $('#ocr-blood-logs');
  
  if (ocrBloodLogs.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="fas fa-heartbeat"></i>
        <p>暂无血量变化记录</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = ocrBloodLogs.map(log => {
    const time = new Date(log.timestamp).toLocaleTimeString();
    const changeIcon = log.changeType === 'death' ? 'fa-skull' :
                       log.changeType === 'decrease' ? 'fa-arrow-down' :
                       log.changeType === 'increase' ? 'fa-arrow-up' :
                       'fa-redo';
    const changeClass = log.changeType === 'death' ? 'danger' :
                        log.changeType === 'decrease' ? 'warning' :
                        log.changeType === 'increase' ? 'success' :
                        'info';
    
    return `
      <div class="log-item ${changeClass}">
        <span class="log-time">${time}</span>
        <i class="fas ${changeIcon}"></i>
        <span class="log-content">
          ${log.oldValue}% → ${log.newValue}%
          ${log.matchedRule ? `<span class="rule-badge">规则: ${log.matchedRule.name}</span>` : ''}
        </span>
      </div>
    `;
  }).join('');
}

/**
 * 清空 OCR 日志
 */
function clearOCRLogs() {
  ocrBloodLogs = [];
  renderOCRLogs();
  showToast('日志已清空', 'success');
}

// 页面切换时加载 OCR 数据 - 在 DOM 加载完成后添加事件监听
document.addEventListener('DOMContentLoaded', () => {
  // 为 OCR 页面添加加载逻辑
  $$('.nav-item').forEach(item => {
    item.addEventListener('click', () => {
      if (item.dataset.page === 'ocr') {
        loadOCRStatus();
      }
      if (item.dataset.page === 'logs') {
        loadShockLogsPage();
      }
    });
  });
});

// ============ 电击日志模块 ============
let shockLogsAutoRefreshTimer = null;
let shockLogs = [];

/**
 * 加载电击日志页面
 */
async function loadShockLogsPage() {
  await refreshShockLogs();
  await loadShockLogStats();
  await loadLogFiles();
  
  // 启动自动刷新
  if ($('#log-auto-refresh')?.checked) {
    startShockLogsAutoRefresh();
  }
}

/**
 * 刷新电击日志
 */
async function refreshShockLogs() {
  try {
    const typeFilter = $('#log-type-filter')?.value || '';
    const deviceFilter = $('#log-device-filter')?.value || '';
    
    let url = '/api/shock-logs?count=100';
    if (typeFilter) {
      url += `&type=${typeFilter}`;
    }
    
    const result = await api(url);
    shockLogs = result.data || [];
    
    // 根据设备筛选
    if (deviceFilter) {
      shockLogs = shockLogs.filter(log => log.deviceName === deviceFilter);
    }
    
    renderShockLogs();
    updateLogDeviceFilter();
  } catch (error) {
    console.error('加载电击日志失败:', error);
    showToast('加载日志失败', 'error');
  }
}

/**
 * 渲染电击日志
 */
function renderShockLogs() {
  const container = $('#device-action-logs');
  
  if (shockLogs.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="fas fa-history"></i>
        <p>暂无日志记录</p>
        <p class="text-muted text-sm">设备动作执行后将在此显示</p>
      </div>
    `;
    return;
  }
  
  container.innerHTML = shockLogs.map(log => {
    const time = new Date(log.timestamp).toLocaleTimeString();
    const date = new Date(log.timestamp).toLocaleDateString();
    
    let icon = 'fa-bolt';
    let typeClass = 'info';
    let description = '';
    
    switch (log.type) {
      case 'strength':
        icon = 'fa-bolt';
        typeClass = log.mode === 'increase' ? 'warning' : log.mode === 'decrease' ? 'success' : 'primary';
        description = `强度${log.mode === 'increase' ? '增加' : log.mode === 'decrease' ? '减少' : '设置'}: CH${log.channel} = ${log.value}`;
        break;
      case 'waveform':
        icon = 'fa-wave-square';
        typeClass = 'info';
        description = `波形发送: CH${log.channel} - ${log.waveform || '自定义波形'}`;
        break;
      case 'event':
        icon = 'fa-paper-plane';
        typeClass = 'secondary';
        description = `事件: ${log.eventName || log.eventId}`;
        break;
      case 'event_rule':
        icon = 'fa-play-circle';
        typeClass = 'danger';
        description = `规则触发: ${log.ruleName} → ${log.eventName || log.eventId}`;
        break;
      case 'queue':
        icon = 'fa-layer-group';
        typeClass = 'secondary';
        description = `队列${log.details?.action === 'clear' ? '清空' : '操作'}: CH${log.channel}`;
        break;
      case 'limit':
        icon = 'fa-sliders-h';
        typeClass = 'warning';
        description = `上限设置: ${log.value}`;
        break;
      default:
        description = log.type;
    }
    
    return `
      <div class="log-item ${typeClass}">
        <span class="log-time" title="${date}">${time}</span>
        <i class="fas ${icon}"></i>
        <span class="log-device">${log.deviceName || '未知设备'}</span>
        <span class="log-content">${description}</span>
        <span class="log-source badge badge-${log.source === 'event_rule' ? 'primary' : log.source === 'ocr_rule' ? 'info' : 'secondary'}">${log.source || 'api'}</span>
      </div>
    `;
  }).join('');
  
  // 自动滚动
  if ($('#log-auto-scroll')?.checked) {
    container.scrollTop = 0;
  }
}

/**
 * 更新设备筛选下拉框
 */
function updateLogDeviceFilter() {
  const select = $('#log-device-filter');
  if (!select) return;
  
  const currentValue = select.value;
  const devices = [...new Set(shockLogs.map(log => log.deviceName).filter(Boolean))];
  
  select.innerHTML = '<option value="">全部设备</option>' + 
    devices.map(name => `<option value="${name}">${name}</option>`).join('');
  
  select.value = currentValue;
}

/**
 * 加载日志统计
 */
async function loadShockLogStats() {
  try {
    const result = await api('/api/shock-logs/stats');
    const stats = result.data;
    
    $('#stat-total-logs').textContent = stats.totalLogs || 0;
    $('#stat-strength-logs').textContent = stats.byType?.strength || 0;
    $('#stat-waveform-logs').textContent = stats.byType?.waveform || 0;
    $('#stat-rule-logs').textContent = (stats.byType?.event_rule || 0) + (stats.byType?.event || 0);
  } catch (error) {
    console.error('加载统计失败:', error);
  }
}

/**
 * 加载日志文件列表
 */
async function loadLogFiles() {
  try {
    const result = await api('/api/shock-logs/files');
    const files = result.data || [];
    
    const container = $('#log-files-list');
    if (files.length === 0) {
      container.innerHTML = '<p class="text-muted">暂无日志文件</p>';
      return;
    }
    
    container.innerHTML = files.map(file => `
      <div class="log-file-item">
        <i class="fas fa-file-alt"></i>
        <div class="log-file-info">
          <span class="log-file-name">${file.filename}</span>
          <span class="log-file-meta">${formatFileSize(file.size)} · ${new Date(file.modified).toLocaleString()}</span>
        </div>
        <button class="btn btn-sm btn-primary" onclick="downloadLogFile('${file.filename}')">
          <i class="fas fa-download"></i>
        </button>
      </div>
    `).join('');
  } catch (error) {
    console.error('加载日志文件列表失败:', error);
    $('#log-files-list').innerHTML = '<p class="text-danger">加载失败</p>';
  }
}

/**
 * 格式化文件大小
 */
function formatFileSize(bytes) {
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
}

/**
 * 清空内存中的电击日志
 */
async function clearShockLogs() {
  try {
    await api('/api/shock-logs/clear', { method: 'POST' });
    shockLogs = [];
    renderShockLogs();
    await loadShockLogStats();
    showToast('内存日志已清空', 'success');
  } catch (error) {
    showToast('清空失败: ' + error.message, 'error');
  }
}

/**
 * 下载日志文件
 */
function downloadLogFile(filename) {
  window.open(`/api/shock-logs/download/${filename}`, '_blank');
}

/**
 * 下载当前的电击日志文件
 */
function downloadShockLogFile() {
  window.open('/api/shock-logs/download/shock.txt', '_blank');
}

/**
 * 切换自动刷新
 */
function toggleAutoRefreshLogs() {
  const autoRefresh = $('#log-auto-refresh')?.checked;
  if (autoRefresh) {
    startShockLogsAutoRefresh();
  } else {
    stopShockLogsAutoRefresh();
  }
}

/**
 * 启动自动刷新
 */
function startShockLogsAutoRefresh() {
  stopShockLogsAutoRefresh();
  shockLogsAutoRefreshTimer = setInterval(() => {
    refreshShockLogs();
    loadShockLogStats();
  }, 3000);
}

/**
 * 停止自动刷新
 */
function stopShockLogsAutoRefresh() {
  if (shockLogsAutoRefreshTimer) {
    clearInterval(shockLogsAutoRefreshTimer);
    shockLogsAutoRefreshTimer = null;
  }
}

// 筛选器变化时刷新日志
document.addEventListener('DOMContentLoaded', () => {
  $('#log-type-filter')?.addEventListener('change', refreshShockLogs);
  $('#log-device-filter')?.addEventListener('change', refreshShockLogs);
});

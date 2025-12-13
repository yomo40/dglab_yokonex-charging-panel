/**
 * Electron 主进程
 * 提供屏幕截图、坐标拾取、系统托盘和OCR识别功能
 */

import { app, BrowserWindow, ipcMain, desktopCapturer, screen, Tray, Menu, nativeImage } from 'electron';
import * as path from 'path';

let mainWindow: BrowserWindow | null = null;
let pickerWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let serverPort: number = 3000; // 实际使用的端口

// OCR 识别状态管理
interface OCRArea {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface OCRState {
  isRunning: boolean;
  area: OCRArea | null;
  armorArea: OCRArea | null;
  interval: number;
  lastBlood: number;
  lastArmor: number;
  mode: 'healthbar' | 'digital' | 'auto';
  armorEnabled: boolean;
  healthColor: string;
  armorColor: string;
  tolerance: number;
  sampleRows: number;
}

const ocrState: OCRState = {
  isRunning: false,
  area: null,
  armorArea: null,
  interval: 200,
  lastBlood: 100,
  lastArmor: 0,
  mode: 'healthbar',
  armorEnabled: false,
  healthColor: 'auto',
  armorColor: 'blue',
  tolerance: 30,
  sampleRows: 3
};

let ocrTimer: NodeJS.Timeout | null = null;

// 开发模式判断
const isDev = process.env.NODE_ENV === 'development';

/**
 * 启动后端服务器（直接 require 而不是 spawn）
 * 返回实际使用的端口
 */
async function startServer(): Promise<number> {
  try {
    // 直接加载服务器模块
    const serverPath = path.join(__dirname, '../src/index.js');
    console.log('Loading server from:', serverPath);
    const serverModule = require(serverPath);
    console.log('Server module loaded successfully');
    
    // 如果服务器导出了获取端口的方法，使用它
    if (serverModule.getPort) {
      const port = await serverModule.getPort();
      return port || 3000;
    }
    return 3000;
  } catch (err) {
    console.error('Failed to load server:', err);
    return 3000;
  }
}

/**
 * 创建主窗口
 */
function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 1000,
    minHeight: 700,
    title: '设备适配器',
    icon: path.join(__dirname, '../../public/favicon.ico'),
    webPreferences: {
      nodeIntegration: true,
      contextIsolation: false,
      webSecurity: false
    },
    show: false
  });

  // 加载本地服务器（使用动态端口）
  const serverUrl = `http://localhost:${serverPort}`;
  
  // 等待服务器启动后加载
  const loadWithRetry = (retries = 10) => {
    mainWindow?.loadURL(serverUrl).catch((err) => {
      if (retries > 0) {
        console.log(`Waiting for server at ${serverUrl}... (${retries} retries left)`);
        setTimeout(() => loadWithRetry(retries - 1), 1000);
      } else {
        console.error('Failed to load server:', err);
        mainWindow?.loadFile(path.join(__dirname, '../../public/index.html'));
      }
    });
  };
  
  // 等待1秒让服务器启动
  setTimeout(() => loadWithRetry(), 1000);

  mainWindow.once('ready-to-show', () => {
    mainWindow?.show();
  });

  mainWindow.on('close', (event) => {
    // 最小化到托盘而不是关闭
    if (mainWindow?.isVisible()) {
      event.preventDefault();
      mainWindow?.hide();
    }
  });

  mainWindow.on('closed', () => {
    mainWindow = null;
  });

  if (isDev) {
    mainWindow.webContents.openDevTools();
  }
}

/**
 * 创建系统托盘
 */
function createTray(): void {
  // 创建闪电图标 (16x16 PNG)
  const iconSize = 16;
  const icon = nativeImage.createEmpty();
  
  // 尝试加载自定义图标，如果失败则使用默认图标
  const iconPath = path.join(__dirname, '../../public/favicon.ico');
  let trayIcon: Electron.NativeImage;
  
  try {
    const loadedIcon = nativeImage.createFromPath(iconPath);
    if (!loadedIcon.isEmpty()) {
      trayIcon = loadedIcon.resize({ width: 16, height: 16 });
    } else {
      // 创建一个简单的闪电形状图标（使用 Data URL）
      // 这是一个16x16的闪电图标
      const lightningIconDataUrl = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAABHNCSVQICAgIfAhkiAAAAAlwSFlzAAAAdgAAAHYBTnsmCAAAABl0RVh0U29mdHdhcmUAd3d3Lmlua3NjYXBlLm9yZ5vuPBoAAADISURBVDiNpdMxSgNBGAXg72WTImBhYSV4BE/gCTyCrYfwAB7AE9h4ACsrC8FCsBJENpts5mcxG9gNu4kPppn/zcc/M8wwe14wrOEt5m+3qY3gDmfYxSQ6qsf0hA+8YITX6OgL7rGKo+TLVJ7VDd7xgDUsYr/Nv6hyLGKMF7zgCI+4CId4C6+4wC0O0twPXGMen+EYM8Hi3zBDL+P4H2Y7Gf8Hp7HALxb/gVPM8BMWCnGvuMQcLuswVusDR3H/sNcV4l5T/PUXyHhqXEoJB98AAAAASUVORK5CYII=';
      trayIcon = nativeImage.createFromDataURL(lightningIconDataUrl);
    }
  } catch {
    // 创建默认闪电图标
    const lightningIconDataUrl = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAABHNCSVQICAgIfAhkiAAAAAlwSFlzAAAAdgAAAHYBTnsmCAAAABl0RVh0U29mdHdhcmUAd3d3Lmlua3NjYXBlLm9yZ5vuPBoAAADISURBVDiNpdMxSgNBGAXg72WTImBhYSV4BE/gCTyCrYfwAB7AE9h4ACsrC8FCsBJENpts5mcxG9gNu4kPppn/zcc/M8wwe14wrOEt5m+3qY3gDmfYxSQ6qsf0hA+8YITX6OgL7rGKo+TLVJ7VDd7xgDUsYr/Nv6hyLGKMF7zgCI+4CId4C6+4wC0O0twPXGMen+EYM8Hi3zBDL+P4H2Y7Gf8Hp7HALxb/gVPM8BMWCnGvuMQcLuswVusDR3H/sNcV4l5T/PUXyHhqXEoJB98AAAAASUVORK5CYII=';
    trayIcon = nativeImage.createFromDataURL(lightningIconDataUrl);
  }
  
  tray = new Tray(trayIcon);
  
  const contextMenu = Menu.buildFromTemplate([
    {
      label: '⚡ 设备适配器',
      enabled: false
    },
    { type: 'separator' },
    {
      label: '显示主窗口',
      click: () => {
        mainWindow?.show();
        mainWindow?.focus();
      }
    },
    {
      label: '框选识别区域',
      click: () => {
        createPickerWindow();
      }
    },
    {
      label: ocrState.isRunning ? '⏹ 停止识别' : '▶ 开始识别',
      click: () => {
        if (ocrState.isRunning) {
          ocrState.isRunning = false;
          stopOCRLoop();
        } else if (ocrState.area) {
          ocrState.isRunning = true;
          startOCRLoop();
        }
        broadcastOCRState();
        // 更新菜单
        createTray();
      }
    },
    { type: 'separator' },
    {
      label: '开发者工具',
      click: () => {
        mainWindow?.webContents.openDevTools();
      }
    },
    { type: 'separator' },
    {
      label: '退出',
      click: () => {
        stopOCRLoop();
        mainWindow?.destroy();
        app.quit();
      }
    }
  ]);
  
  tray.setToolTip('⚡ 设备适配器');
  tray.setContextMenu(contextMenu);
  
  tray.on('double-click', () => {
    mainWindow?.show();
    mainWindow?.focus();
  });
}

/**
 * 截取屏幕指定区域
 */
async function captureScreenArea(area: { x: number; y: number; width: number; height: number }): Promise<string | null> {
  try {
    const primaryDisplay = screen.getPrimaryDisplay();
    const { width, height } = primaryDisplay.size;
    const scaleFactor = primaryDisplay.scaleFactor || 1;
    
    const sources = await desktopCapturer.getSources({
      types: ['screen'],
      thumbnailSize: {
        width: Math.floor(width * scaleFactor),
        height: Math.floor(height * scaleFactor)
      }
    });
    
    if (sources.length === 0) {
      console.error('No screen sources found');
      return null;
    }
    
    const source = sources[0];
    const thumbnail = source.thumbnail;
    
    // 裁剪指定区域
    const croppedImage = thumbnail.crop({
      x: Math.floor(area.x * scaleFactor),
      y: Math.floor(area.y * scaleFactor),
      width: Math.floor(area.width * scaleFactor),
      height: Math.floor(area.height * scaleFactor)
    });
    
    return croppedImage.toDataURL();
  } catch (error) {
    console.error('Screen capture error:', error);
    return null;
  }
}

// IPC 处理程序
ipcMain.handle('capture-screen-area', async (event, area) => {
  return await captureScreenArea(area);
});

ipcMain.handle('get-screen-size', () => {
  const primaryDisplay = screen.getPrimaryDisplay();
  return primaryDisplay.size;
});

ipcMain.handle('get-all-displays', () => {
  return screen.getAllDisplays().map(display => ({
    id: display.id,
    bounds: display.bounds,
    size: display.size,
    scaleFactor: display.scaleFactor
  }));
});

/**
 * 创建坐标拾取窗口（全屏透明覆盖层）
 */
function createPickerWindow(): void {
  if (pickerWindow) {
    pickerWindow.focus();
    return;
  }

  const primaryDisplay = screen.getPrimaryDisplay();
  const { width, height } = primaryDisplay.size;

  pickerWindow = new BrowserWindow({
    width: width,
    height: height,
    x: 0,
    y: 0,
    frame: false,
    transparent: true,  // 启用透明
    backgroundColor: '#00000000',  // 完全透明背景
    alwaysOnTop: true,
    resizable: false,
    skipTaskbar: true,
    fullscreen: true,
    hasShadow: false,
    webPreferences: {
      nodeIntegration: true,
      contextIsolation: false
    }
  });

  pickerWindow.loadFile(path.join(__dirname, '../../public/ocr/coordinate-picker.html'));

  pickerWindow.once('ready-to-show', () => {
    pickerWindow?.show();
    pickerWindow?.focus();
    console.log('Picker window ready');
  });

  pickerWindow.on('closed', () => {
    pickerWindow = null;
  });
}

// 记录打开 picker 的源窗口
let pickerSourceWindow: BrowserWindow | null = null;

// 坐标拾取相关IPC
ipcMain.handle('open-coordinate-picker', (event) => {
  // 记录调用者窗口
  pickerSourceWindow = BrowserWindow.fromWebContents(event.sender);
  console.log('open-coordinate-picker called from window:', pickerSourceWindow?.id);
  createPickerWindow();
});

ipcMain.handle('get-picker-window-bounds', () => {
  if (pickerWindow && !pickerWindow.isDestroyed()) {
    const bounds = pickerWindow.getBounds();
    console.log('get-picker-window-bounds:', bounds);
    return bounds;
  }
  return null;
});

ipcMain.handle('close-picker-window', (event, area) => {
  console.log('close-picker-window called with area:', area);
  
  // 保存到 OCR 状态
  if (area) {
    ocrState.area = area;
  }
  
  // 发送坐标数据到所有窗口
  if (area) {
    // 发送到主窗口
    if (mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send('coordinate-data', area);
      mainWindow.webContents.send('ocr-state-update', ocrState);
      console.log('Sent coordinate-data to main window:', area);
    }
  }

  if (pickerWindow && !pickerWindow.isDestroyed()) {
    pickerWindow.close();
    pickerWindow = null;
  }
  pickerSourceWindow = null;
});

// OCR 控制 IPC
ipcMain.handle('ocr-set-area', (event, area) => {
  ocrState.area = area;
  broadcastOCRState();
  return { success: true };
});

ipcMain.handle('ocr-set-armor-area', (event, area) => {
  ocrState.armorArea = area;
  broadcastOCRState();
  return { success: true };
});

ipcMain.handle('ocr-set-config', (event, config) => {
  if (config.interval !== undefined) ocrState.interval = config.interval;
  if (config.mode !== undefined) ocrState.mode = config.mode;
  if (config.armorEnabled !== undefined) ocrState.armorEnabled = config.armorEnabled;
  if (config.healthColor !== undefined) ocrState.healthColor = config.healthColor;
  if (config.armorColor !== undefined) ocrState.armorColor = config.armorColor;
  if (config.tolerance !== undefined) ocrState.tolerance = config.tolerance;
  if (config.sampleRows !== undefined) ocrState.sampleRows = config.sampleRows;
  broadcastOCRState();
  return { success: true };
});

ipcMain.handle('ocr-start', async () => {
  if (!ocrState.area) {
    return { success: false, error: '未设置血量识别区域' };
  }
  if (ocrState.armorEnabled && !ocrState.armorArea) {
    return { success: false, error: '已启用护甲识别但未设置护甲区域' };
  }
  ocrState.isRunning = true;
  startOCRLoop();
  broadcastOCRState();
  return { success: true };
});

ipcMain.handle('ocr-stop', () => {
  ocrState.isRunning = false;
  stopOCRLoop();
  broadcastOCRState();
  return { success: true };
});

ipcMain.handle('ocr-get-state', () => {
  return ocrState;
});

// 广播 OCR 状态到所有窗口
function broadcastOCRState() {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('ocr-state-update', ocrState);
  }
}

// OCR 识别循环
function startOCRLoop() {
  if (ocrTimer) {
    clearInterval(ocrTimer);
  }
  
  ocrTimer = setInterval(async () => {
    if (!ocrState.isRunning || !ocrState.area) return;
    
    try {
      // 截取血量区域
      const healthImageData = await captureScreenArea(ocrState.area);
      
      // 截取护甲区域（如果启用）
      let armorImageData: string | null = null;
      if (ocrState.armorEnabled && ocrState.armorArea) {
        armorImageData = await captureScreenArea(ocrState.armorArea);
      }
      
      if (healthImageData && mainWindow && !mainWindow.isDestroyed()) {
        // 发送截图数据到渲染进程进行识别
        mainWindow.webContents.send('ocr-frame', {
          healthImageData,
          armorImageData,
          area: ocrState.area,
          armorArea: ocrState.armorArea,
          mode: ocrState.mode,
          armorEnabled: ocrState.armorEnabled,
          healthColor: ocrState.healthColor,
          armorColor: ocrState.armorColor,
          tolerance: ocrState.tolerance,
          sampleRows: ocrState.sampleRows
        });
      }
    } catch (err) {
      console.error('OCR capture error:', err);
    }
  }, ocrState.interval);
}

function stopOCRLoop() {
  if (ocrTimer) {
    clearInterval(ocrTimer);
    ocrTimer = null;
  }
}

ipcMain.handle('get-screen-info', () => {
  const primaryDisplay = screen.getPrimaryDisplay();
  return {
    width: primaryDisplay.size.width,
    height: primaryDisplay.size.height,
    scaleFactor: primaryDisplay.scaleFactor
  };
});

// 应用事件处理
app.whenReady().then(() => {
  startServer();
  createTray();
  createWindow();
});

app.on('window-all-closed', () => {
  // macOS 保持应用运行
  if (process.platform !== 'darwin') {
    // 不退出，只隐藏到托盘
  }
});

app.on('activate', () => {
  if (mainWindow === null) {
    createWindow();
  } else {
    mainWindow.show();
  }
});

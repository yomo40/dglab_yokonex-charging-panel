# 肌肉电刺激技术设备适配面板

⚡ 充电宝面板 - 支持 DG-LAB(郊狼) 和 役次元 设备的控制与管理。
![Version](https://img.shields.io/badge/version-0.95.0-blue)
![License](https://img.shields.io/badge/license-GPLv3-blue)

## 版本 0.95.0

### 🆕 新功能
- ✅ **内置郊狼WebSocket服务器**: 无需外部中转服务，可直接在系统设置中启动
- ✅ **OCR血量识别增强**: 支持三种识别模式（血条识别、数字OCR、自动检测）
- ✅ **护甲识别**: 新增护甲条识别功能，支持独立区域框选
- ✅ **透明窗口区域选择器**: Electron 集成的屏幕区域拾取工具
- ✅ **系统托盘支持**: 最小化到托盘，后台运行

## 功能特性

- ✅ **统一接口**: 为不同厂商设备提供统一的控制接口
- ✅ **DG-LAB 支持**: 通过 WebSocket 协议连接郊狼设备
- ✅ **役次元 支持**: 通过腾讯 IM 协议连接役次元设备
- ✅ **内置WebSocket服务器**: 郊狼设备可直接连接，无需外部中转
- ✅ **OCR血量识别**: 支持血条颜色识别、数字OCR和自动检测
- ✅ **护甲识别**: 独立的护甲条识别和事件触发
- ✅ **REST API**: 提供完整的 HTTP API 用于设备控制
- ✅ **WebSocket API**: 实时双向通信支持
- ✅ **波形生成器**: 内置多种预设波形和自定义波形生成
- ✅ **事件调度**: 支持定时任务和周期性任务
- ✅ **配置管理**: 持久化配置存储
- ✅ **Electron 桌面应用**: 独立运行的桌面客户端

## 快速开始

### 方式一：直接运行发行版

1. 下载 `充电宝2合1工具-v0.95.zip`
2. 运行 `充电宝2合1工具.exe`

### 方式二：从源码运行

#### 安装依赖

```bash
cd app
npm install
```

#### 开发模式运行

```bash
npm run dev
```

#### 生产构建

```bash
npm run build
npm start
```

#### 启动 Electron 应用

```bash
npm run build
npm run electron
```

## 项目结构

```
app/
├── src/
│   ├── adapters/           # 设备适配器
│   │   ├── IDeviceAdapter.ts   # 统一接口定义
│   │   ├── dglab/              # DG-LAB 适配器
│   │   │   ├── DGLabAdapter.ts
│   │   │   ├── protocol.ts     # 协议编解码
│   │   │   └── waveform.ts     # 波形生成
│   │   └── yokonex/            # YOKONEX 适配器
│   │       ├── YokonexAdapter.ts
│   │       └── eventMapper.ts  # 事件映射
│   ├── core/               # 核心服务
│   │   ├── DeviceManager.ts    # 设备管理
│   │   ├── EventScheduler.ts   # 事件调度
│   │   ├── ConfigStore.ts      # 配置存储
│   │   ├── CoyoteWebSocketServer.ts  # 内置郊狼WebSocket服务器
│   │   └── OCRService.ts       # OCR血量识别服务
│   ├── api/                # API 层
│   │   ├── server.ts           # HTTP/WS 服务器
│   │   └── routes/             # 路由定义
│   │       ├── devices.ts      # 设备路由
│   │       ├── control.ts      # 控制路由
│   │       ├── events.ts       # 事件路由
│   │       ├── ocr.ts          # OCR路由
│   │       └── coyote.ts       # 郊狼服务器路由
│   ├── utils/              # 工具类
│   │   └── logger.ts           # 日志工具
│   └── index.ts            # 应用入口
├── config/
│   └── default.json        # 默认配置
├── package.json
└── tsconfig.json
```

## API 文档

### 郊狼WebSocket服务器管理

#### 获取服务器状态
```http
GET /api/coyote/status
```

#### 启动服务器
```http
POST /api/coyote/start
Content-Type: application/json

{
  "port": 9999,
  "host": "0.0.0.0"
}
```

#### 停止服务器
```http
POST /api/coyote/stop
```

### OCR血量识别

#### 获取OCR状态
```http
GET /api/ocr/status
```

#### 配置OCR
```http
POST /api/ocr/configure
Content-Type: application/json

{
  "mode": "healthbar",
  "interval": 2000,
  "healthbar": {
    "color": "auto",
    "tolerance": 30
  }
}
```

#### 识别图像
```http
POST /api/ocr/recognize
Content-Type: application/json

{
  "image": "data:image/png;base64,..."
}
```

#### 手动上报血量
```http
POST /api/ocr/report-blood
Content-Type: application/json

{
  "value": 75
}
```

### 设备管理

#### 获取所有设备
```http
GET /api/devices
```

#### 添加设备
```http
POST /api/devices
Content-Type: application/json

{
  "type": "dglab",
  "name": "My DG-LAB Device",
  "config": {
    "websocketUrl": "ws://localhost:9999",
    "autoReconnect": true
  }
}
```

#### 连接设备
```http
POST /api/devices/:id/connect
```

#### 断开设备
```http
POST /api/devices/:id/disconnect
```

### 设备控制

#### 设置强度
```http
POST /api/control/strength
Content-Type: application/json

{
  "deviceId": "device_1",
  "channel": 1,
  "value": 50,
  "mode": 2
}
```

参数说明:
- `channel`: 1 = A通道, 2 = B通道
- `mode`: 0 = 减少, 1 = 增加, 2 = 设定值
- `value`: 强度值 (0-200)

#### 发送波形
```http
POST /api/control/waveform
Content-Type: application/json

{
  "deviceId": "device_1",
  "channel": 1,
  "waveform": {
    "frequency": [50, 60, 70, 80],
    "strength": [20, 30, 40, 50],
    "duration": 1
  }
}
```

#### 使用预设波形
```http
POST /api/control/waveform/preset
Content-Type: application/json

{
  "deviceId": "device_1",
  "channel": 1,
  "preset": "pulse",
  "duration": 2
}
```

可用预设: `gentle`, `pulse`, `wave`, `intense`, `random`

#### 发送事件 (YOKONEX)
```http
POST /api/control/event
Content-Type: application/json

{
  "deviceId": "device_1",
  "eventId": "hurt",
  "payload": {}
}
```

#### 紧急停止
```http
POST /api/control/stop
```

### WebSocket API

连接到 `ws://localhost:3000` 后发送 JSON 消息:

#### 订阅事件
```json
{
  "type": "subscribe",
  "events": ["device.status", "device.strength"]
}
```

#### 发送命令
```json
{
  "type": "command",
  "command": "setStrength",
  "params": {
    "deviceId": "device_1",
    "channel": 1,
    "value": 30
  }
}
```

## 配置说明

编辑 `config/default.json`:

```json
{
  "server": {
    "port": 3000,
    "host": "0.0.0.0"
  },
  "dglab": {
    "websocketUrl": "ws://localhost:9999",
    "heartbeatInterval": 10000,
    "reconnectInterval": 5000
  },
  "yokonex": {
    "apiBase": "https://suo.jiushu1234.com/api.php",
    "imTimeout": 15000
  },
  "waveform": {
    "defaultFrequency": 50,
    "defaultStrength": 20
  },
  "logging": {
    "level": "info"
  }
}
```

## 使用示例

### DG-LAB 设备连接

```javascript
// 1. 添加设备
const response = await fetch('http://localhost:3000/api/devices', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    type: 'dglab',
    name: 'My Coyote',
    config: {
      websocketUrl: 'ws://your-ws-server:9999',
      autoReconnect: true
    }
  })
});
const { data: device } = await response.json();

// 2. 连接设备
await fetch(`http://localhost:3000/api/devices/${device.id}/connect`, {
  method: 'POST'
});

// 3. 设置强度
await fetch('http://localhost:3000/api/control/strength', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    deviceId: device.id,
    channel: 1,
    value: 30,
    mode: 2
  })
});
```

### YOKONEX 设备连接

```javascript
// 1. 添加设备
const response = await fetch('http://localhost:3000/api/devices', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    type: 'yokonex',
    name: 'My YCY Device',
    config: {
      uid: '12345',
      token: 'your-token-here'
    }
  })
});
const { data: device } = await response.json();

// 2. 连接设备
await fetch(`http://localhost:3000/api/devices/${device.id}/connect`, {
  method: 'POST'
});

// 3. 发送事件
await fetch('http://localhost:3000/api/control/event', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    deviceId: device.id,
    eventId: 'hurt'
  })
});
```

## 许可证
- 本项目采用 GNU GPL v3.0（详见 [LICENSE](LICENSE)）。
- 允许商用，但需公开完整源代码，并在相同协议下分发。
- 所有修改和再分发版本必须附带源码与版权声明。

## 开发者

- [yomo40](https://github.com/yomo40)

## 致谢

- [DG-LAB](https://github.com/DG-LAB-OPENSOURCE) - 郊狼设备协议
- [YCY-YOKONEX-OpenSource](https://github.com/YCY-YOKONEX/YCY-YOKONEX-OpenSource) - 役次元设备协议
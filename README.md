# 肌肉电刺激技术设备适配面板

⚡ 充电宝面板 - 支持 DG-LAB(郊狼) 和 役次元 设备的控制与管理。
![Version](https://img.shields.io/badge/version-0.95.0-blue)
![License](https://img.shields.io/badge/license-GPLv3-blue)

一个基于 C# + Avalonia 的跨平台桌面应用，用于连接郊狼(DG-LAB)和役次元(Yokonex)设备，实现游戏血量识别与设备控制联动。

## 功能特性

### 设备适配
- **郊狼 (DG-LAB)**: 通过 WebSocket 协议连接郊狼APP，支持扫码绑定
- **役次元 (Yokonex)**: 通过 HTTP API 连接役次元设备

### OCR 血量识别
- 屏幕区域截图识别
- 血条颜色分析
- 护甲等级检测
- 实时血量变化监测

### 多人房间系统
- P2P 直连模式
- 房间创建与加入
- 用户权限管理

### 对战模式
- 伤害同步：你受伤 → 对手电击
- 血量竞速：血量低者受惩罚
- 互相控制：各自控制对方设备

## 技术栈

- **框架**: .NET 8.0 + Avalonia 11.2.2
- **UI**: Fluent Design 深色主题
- **图像处理**: SixLabors.ImageSharp
- **OCR**: Microsoft.ML.OnnxRuntime (预留)
- **日志**: Serilog
- **MVVM**: CommunityToolkit.Mvvm


## 项目架构  用户关系模型

## 场景1: 远程控制

```
用户1 ──── 控制指令 ────→ 用户2
```

## 场景2: 游戏对战惩罚

```
用户3 (PC+设备) ←───── 房间对战 ──────→ 用户4 (PC+设备)
     ↓                                    ↓
  游戏失败                             游戏失败
     ↓                                    ↓
 接受电击惩罚                         接受电击惩罚
```

## 场景3: 多人房间

```
┌──────┐  ┌──────┐  ┌──────┐  ┌──────┐
│用户A │  │用户B │  │用户C │  │用户D │
└──┬───┘  └──┬───┘  └──┬───┘  └──┬───┘
   └─────────┴─────────┴─────────┘
              房间
    (互相可见状态，可互相控制)
```



# GameAdapterPanel

**单一应用，多端适配**

## UI 层

- **Avalonia** (Windows/macOS/Linux)
- **MAUI** (Android/iOS)
- **Web** (预留)

## Core 核心层

### 设备模块

- 郊狼
- 役次元
- 扩展接口

### OCR模块

- 内置引擎
- 自定义模型
- 血条/数字识别

### 游戏适配模块

- 插件系统
- 规则引擎
- 脚本执行

### 网络模块

- P2P直连
- 房间系统
- 云端中转

### 数据模块

- SQLite
- 配置管理
- 日志系统

### 事件模块

- 本地事件
- 远程事件
- 事件同步


# 房间系统

## 创建房间

```
用户A ──创建房间──→ 生成房间码: ABC123
                  (可选: 设置密码/人数限制/权限)
```

## 加入房间

```
用户B ──输入房间码──→ 加入房间 ABC123
```

## 房间内通信

### 房间 ABC123

- **用户A**: PC+郊狼
- **用户B**: PC+役次元
- **用户C**: 手机(无设备)

### 房间状态同步

- **用户列表**: 谁在线/谁有设备
- **设备状态**: 连接/强度/模式
- **游戏状态**: 血量/护甲/死亡
- **控制权限**: 谁可以控制谁

## 连接方式

> **需要内置支持模式1**

### 模式1: P2P

- 房主开端口
- 直接连接
- 延迟最低

### 模式2: 中转

- 云服务器中转
- 无需开端口
- 穿透NAT

### 模式3: 混合

- 优先P2P
- 失败则中转
- 最佳体验


# 权限系统

## 用户角色

用户角色非固定，可切换：

### 控制者

- 可发送指令
- 可观看状态

### 被控者

- 接收指令
- 可拒绝控制

### 观察者

- 只能观看
- 不能操作

## 权限授予

```
用户A ──请求控制权限────→ 用户B
                           ↓
                    [同意] / [拒绝]
                           ↓
                  授权成功 / 授权失败
```

## 对战模式

### 游戏对战房间

**规则**: 游戏中死亡 → 触发对方设备电击

```
┌──────┐                      ┌──────┐
│用户A │  ←──── 互相绑定 ────→ │用户B │
│ 血量 │                      │ 血量 │
└──┬───┘                      └──┬───┘
   │                            │
   ↓ 死亡事件                   ↓ 死亡事件
触发用户B设备              触发用户A设备
```


## 项目结构

```
dglab_yokonex-charging-panel/
├── dglab_yokonex-charging-panel.sln  # 解决方案文件
├── src/
│   ├── Core/                          # 核心库
│   │   ├── Devices/                   # 设备适配器
│   │   │   ├── IDevice.cs             # 设备接口
│   │   │   ├── DGLab/DGLabDevice.cs   # 郊狼适配器
│   │   │   └── Yokonex/YokonexDevice.cs # 役次元适配器
│   │   ├── OCR/                       # OCR 引擎
│   │   │   ├── IOCREngine.cs          # OCR 接口
│   │   │   ├── OCREngine.cs           # OCR 实现
│   │   │   └── HealthBarAnalyzer.cs   # 血条分析器
│   │   └── Network/                   # 网络模块
│   │       ├── Room.cs                # 房间系统
│   │       └── P2PConnection.cs       # P2P 连接
│   └── Desktop/                       # 桌面应用
│       ├── Program.cs                 # 入口点
│       ├── App.axaml                  # 应用定义
│       ├── Views/                     # 视图
│       │   └── MainWindow.axaml       # 主窗口
│       └── ViewModels/                # 视图模型
│           ├── MainViewModel.cs
│           ├── OCRViewModel.cs
│           ├── RoomViewModel.cs
│           └── BattleViewModel.cs
```

## 构建与运行

### 开发环境要求
- .NET 8.0 SDK
- Visual Studio 2022 或 VS Code

### 构建
```bash
cd dglab_yokonex-charging-panel
dotnet build
```

### 运行
```bash
dotnet run --project src/Desktop
```

### 发布
```bash
dotnet publish src/Desktop -c Release -r win-x64 --self-contained
```

## 使用说明

### 连接郊狼
1. 点击"连接郊狼"按钮
2. 应用会连接到郊狼 WebSocket 服务器
3. 打开郊狼APP，扫描显示的二维码
4. 绑定成功后即可控制设备

### 连接役次元
1. 在役次元APP中获取 UID 和 Token
2. 点击"连接役次元"按钮
3. 输入 UID 和 Token
4. 连接成功后即可控制设备

### OCR 识别
1. 切换到 OCR 页面
2. 设置识别区域（血量条位置）
3. 选择识别模式
4. 点击"开始识别"

### 多人对战
1. 创建房间或加入房间
2. 设置对战绑定
3. 开始对战
4. 游戏中受伤时会触发对手设备

## 协议参考

- [DG-LAB WebSocket 协议](../ExternalApiAdapter/DG-LAB-OPENSOURCE/socket/README.md)
- [役次元 IM 协议](../ExternalApiAdapter/ycy_dev/Instant%20Messaging/)

## 许可证

GPLv3
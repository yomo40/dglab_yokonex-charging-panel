/**
 * DG-LAB 蓝牙协议处理器
 * 基于郊狼情趣脉冲主机 V3 蓝牙协议
 * 
 * 协议文档: ExternalApiAdapter/DG-LAB-OPENSOURCE/coyote/v3/README_V3.md
 * 
 * 蓝牙特性:
 * - 服务 UUID: 0x180C
 * - 写入特性: 0x150A (发送指令)
 * - 通知特性: 0x150B (接收回应)
 * - 电量服务: 0x180A -> 0x1500
 * 
 * @version 1.0.0
 */

import { Channel, StrengthMode } from '../IDeviceAdapter';
import { Logger } from '../../utils/logger';

const logger = new Logger('DGLabBluetooth');

// 蓝牙 UUID 常量
export const BLE_SERVICE_UUID = '0000180c-0000-1000-8000-00805f9b34fb';
export const BLE_WRITE_CHARACTERISTIC = '0000150a-0000-1000-8000-00805f9b34fb';
export const BLE_NOTIFY_CHARACTERISTIC = '0000150b-0000-1000-8000-00805f9b34fb';
export const BLE_BATTERY_SERVICE = '0000180a-0000-1000-8000-00805f9b34fb';
export const BLE_BATTERY_CHARACTERISTIC = '00001500-0000-1000-8000-00805f9b34fb';

// 蓝牙设备名称前缀
export const DEVICE_NAME_PREFIX_V3 = '47L121000';  // 脉冲主机 3.0
export const DEVICE_NAME_PREFIX_SENSOR = '47L120100';  // 无线传感器

/**
 * 强度解读方式
 */
export enum StrengthParsingMode {
  NoChange = 0b00,    // 不改变
  Increase = 0b01,    // 相对增加
  Decrease = 0b10,    // 相对减少
  Absolute = 0b11,    // 绝对值设定
}

/**
 * 波形数据结构 (单通道)
 */
export interface ChannelWaveform {
  frequency: number[];  // 4个频率值 (10-240)
  strength: number[];   // 4个强度值 (0-100)
}

/**
 * B0 指令数据结构
 */
export interface B0CommandData {
  sequenceNo: number;           // 序列号 (0-15)
  strengthModeA: StrengthParsingMode;  // A通道强度解读方式
  strengthModeB: StrengthParsingMode;  // B通道强度解读方式
  strengthValueA: number;       // A通道强度设定值 (0-200)
  strengthValueB: number;       // B通道强度设定值 (0-200)
  waveformA: ChannelWaveform;   // A通道波形数据
  waveformB: ChannelWaveform;   // B通道波形数据
}

/**
 * BF 指令数据结构 (软上限和平衡参数)
 */
export interface BFCommandData {
  limitA: number;              // A通道强度软上限 (0-200)
  limitB: number;              // B通道强度软上限 (0-200)
  freqBalanceA: number;        // A通道频率平衡参数 (0-255)
  freqBalanceB: number;        // B通道频率平衡参数 (0-255)
  strengthBalanceA: number;    // A通道强度平衡参数 (0-255)
  strengthBalanceB: number;    // B通道强度平衡参数 (0-255)
}

/**
 * B1 回应消息结构
 */
export interface B1Response {
  sequenceNo: number;          // 序列号
  strengthA: number;           // A通道当前实际强度
  strengthB: number;           // B通道当前实际强度
}

/**
 * DG-LAB 蓝牙协议编解码器
 */
export class DGLabBluetoothProtocol {
  private sequenceNo: number = 0;

  /**
   * 获取下一个序列号 (0-15 循环)
   */
  getNextSequenceNo(): number {
    this.sequenceNo = (this.sequenceNo + 1) % 16;
    return this.sequenceNo;
  }

  /**
   * 频率值转换：用户输入 (10-1000) -> 蓝牙协议值 (10-240)
   */
  static convertFrequency(inputValue: number): number {
    if (inputValue >= 10 && inputValue <= 100) {
      return inputValue;
    } else if (inputValue >= 101 && inputValue <= 600) {
      return Math.floor((inputValue - 100) / 5) + 100;
    } else if (inputValue >= 601 && inputValue <= 1000) {
      return Math.floor((inputValue - 600) / 10) + 200;
    } else {
      return 10; // 默认值
    }
  }

  /**
   * 构建 B0 指令 (20字节)
   * 每100ms发送一次，包含强度变化和波形数据
   */
  buildB0Command(data: B0CommandData): Uint8Array {
    const buffer = new Uint8Array(20);
    
    // 指令 HEAD
    buffer[0] = 0xB0;
    
    // 序列号 (高4位) + 强度值解读方式 (低4位)
    const strengthMode = ((data.strengthModeA & 0x03) << 2) | (data.strengthModeB & 0x03);
    buffer[1] = ((data.sequenceNo & 0x0F) << 4) | (strengthMode & 0x0F);
    
    // 强度设定值
    buffer[2] = Math.max(0, Math.min(200, data.strengthValueA));
    buffer[3] = Math.max(0, Math.min(200, data.strengthValueB));
    
    // A通道波形频率 (4字节)
    for (let i = 0; i < 4; i++) {
      buffer[4 + i] = Math.max(10, Math.min(240, data.waveformA.frequency[i] || 10));
    }
    
    // A通道波形强度 (4字节)
    for (let i = 0; i < 4; i++) {
      buffer[8 + i] = Math.max(0, Math.min(100, data.waveformA.strength[i] || 0));
    }
    
    // B通道波形频率 (4字节)
    for (let i = 0; i < 4; i++) {
      buffer[12 + i] = Math.max(10, Math.min(240, data.waveformB.frequency[i] || 10));
    }
    
    // B通道波形强度 (4字节)
    for (let i = 0; i < 4; i++) {
      buffer[16 + i] = Math.max(0, Math.min(100, data.waveformB.strength[i] || 0));
    }
    
    return buffer;
  }

  /**
   * 构建 BF 指令 (7字节)
   * 设置软上限和平衡参数 (断电保存)
   * 
   * ⚠️ 重要：每次重连设备后必须重新写入 BF 指令设置软上限
   */
  buildBFCommand(data: BFCommandData): Uint8Array {
    const buffer = new Uint8Array(7);
    
    // 指令 HEAD
    buffer[0] = 0xBF;
    
    // 通道强度软上限
    buffer[1] = Math.max(0, Math.min(200, data.limitA));
    buffer[2] = Math.max(0, Math.min(200, data.limitB));
    
    // 波形频率平衡参数
    buffer[3] = Math.max(0, Math.min(255, data.freqBalanceA));
    buffer[4] = Math.max(0, Math.min(255, data.freqBalanceB));
    
    // 波形强度平衡参数
    buffer[5] = Math.max(0, Math.min(255, data.strengthBalanceA));
    buffer[6] = Math.max(0, Math.min(255, data.strengthBalanceB));
    
    return buffer;
  }

  /**
   * 构建简单的强度设置 B0 指令
   * 只修改强度，不发送波形
   */
  buildStrengthCommand(
    channel: Channel,
    value: number,
    mode: StrengthMode,
    needResponse: boolean = true
  ): Uint8Array {
    // 转换强度模式
    let parsingMode: StrengthParsingMode;
    switch (mode) {
      case StrengthMode.Decrease:
        parsingMode = StrengthParsingMode.Decrease;
        break;
      case StrengthMode.Increase:
        parsingMode = StrengthParsingMode.Increase;
        break;
      case StrengthMode.Set:
      default:
        parsingMode = StrengthParsingMode.Absolute;
        break;
    }

    const data: B0CommandData = {
      sequenceNo: needResponse ? this.getNextSequenceNo() : 0,
      strengthModeA: channel === Channel.A ? parsingMode : StrengthParsingMode.NoChange,
      strengthModeB: channel === Channel.B ? parsingMode : StrengthParsingMode.NoChange,
      strengthValueA: channel === Channel.A ? value : 0,
      strengthValueB: channel === Channel.B ? value : 0,
      waveformA: { frequency: [0, 0, 0, 0], strength: [0, 0, 0, 101] }, // 101 表示不输出
      waveformB: { frequency: [0, 0, 0, 0], strength: [0, 0, 0, 101] },
    };

    return this.buildB0Command(data);
  }

  /**
   * 构建波形输出 B0 指令
   * 只发送波形，不修改强度
   */
  buildWaveformCommand(
    channel: Channel,
    waveform: ChannelWaveform
  ): Uint8Array {
    // 对于不使用的通道，设置无效数据 (强度值 > 100)
    const invalidWaveform: ChannelWaveform = {
      frequency: [0, 0, 0, 0],
      strength: [0, 0, 0, 101],
    };

    const data: B0CommandData = {
      sequenceNo: 0,
      strengthModeA: StrengthParsingMode.NoChange,
      strengthModeB: StrengthParsingMode.NoChange,
      strengthValueA: 0,
      strengthValueB: 0,
      waveformA: channel === Channel.A ? waveform : invalidWaveform,
      waveformB: channel === Channel.B ? waveform : invalidWaveform,
    };

    return this.buildB0Command(data);
  }

  /**
   * 构建双通道波形输出 B0 指令
   */
  buildDualWaveformCommand(
    waveformA: ChannelWaveform,
    waveformB: ChannelWaveform
  ): Uint8Array {
    const data: B0CommandData = {
      sequenceNo: 0,
      strengthModeA: StrengthParsingMode.NoChange,
      strengthModeB: StrengthParsingMode.NoChange,
      strengthValueA: 0,
      strengthValueB: 0,
      waveformA,
      waveformB,
    };

    return this.buildB0Command(data);
  }

  /**
   * 解析 B1 回应消息
   */
  parseB1Response(data: Uint8Array): B1Response | null {
    if (data.length < 4 || data[0] !== 0xB1) {
      return null;
    }

    return {
      sequenceNo: data[1],
      strengthA: data[2],
      strengthB: data[3],
    };
  }

  /**
   * 将数据转换为 HEX 字符串 (调试用)
   */
  static toHexString(data: Uint8Array): string {
    return '0x' + Array.from(data)
      .map(b => b.toString(16).padStart(2, '0').toUpperCase())
      .join('');
  }
}

/**
 * 蓝牙连接接口
 * 由具体平台实现（Node.js noble / Web Bluetooth / Electron）
 */
export interface BluetoothConnection {
  /**
   * 扫描设备
   */
  scan(timeout?: number): Promise<BluetoothDevice[]>;

  /**
   * 连接设备
   */
  connect(deviceId: string): Promise<void>;

  /**
   * 断开连接
   */
  disconnect(): Promise<void>;

  /**
   * 写入数据
   */
  write(data: Uint8Array): Promise<void>;

  /**
   * 订阅通知
   */
  subscribe(callback: (data: Uint8Array) => void): void;

  /**
   * 读取电量
   */
  readBattery(): Promise<number>;

  /**
   * 检查连接状态
   */
  isConnected(): boolean;
}

/**
 * 蓝牙设备信息
 */
export interface BluetoothDevice {
  id: string;
  name: string;
  rssi?: number;
}

/**
 * 蓝牙连接状态
 */
export enum BluetoothConnectionState {
  Disconnected = 'disconnected',
  Scanning = 'scanning',
  Connecting = 'connecting',
  Connected = 'connected',
  Error = 'error',
}

/**
 * 蓝牙适配器抽象基类
 * 提供平台无关的蓝牙操作封装
 */
export abstract class DGLabBluetoothAdapterBase {
  protected protocol: DGLabBluetoothProtocol;
  protected connection: BluetoothConnection | null = null;
  protected state: BluetoothConnectionState = BluetoothConnectionState.Disconnected;
  protected strengthA: number = 0;
  protected strengthB: number = 0;
  protected limitA: number = 200;
  protected limitB: number = 200;
  protected batteryLevel: number = 0;
  protected waveformTimer: NodeJS.Timeout | null = null;

  // 事件回调
  protected onStateChange?: (state: BluetoothConnectionState) => void;
  protected onStrengthChange?: (strengthA: number, strengthB: number) => void;
  protected onBatteryChange?: (level: number) => void;
  protected onError?: (error: Error) => void;

  constructor() {
    this.protocol = new DGLabBluetoothProtocol();
  }

  /**
   * 设置状态变化回调
   */
  setOnStateChange(callback: (state: BluetoothConnectionState) => void): void {
    this.onStateChange = callback;
  }

  /**
   * 设置强度变化回调
   */
  setOnStrengthChange(callback: (strengthA: number, strengthB: number) => void): void {
    this.onStrengthChange = callback;
  }

  /**
   * 设置电量变化回调
   */
  setOnBatteryChange(callback: (level: number) => void): void {
    this.onBatteryChange = callback;
  }

  /**
   * 设置错误回调
   */
  setOnError(callback: (error: Error) => void): void {
    this.onError = callback;
  }

  /**
   * 获取当前状态
   */
  getState(): BluetoothConnectionState {
    return this.state;
  }

  /**
   * 获取当前强度
   */
  getStrength(): { a: number; b: number } {
    return { a: this.strengthA, b: this.strengthB };
  }

  /**
   * 获取电量
   */
  getBattery(): number {
    return this.batteryLevel;
  }

  /**
   * 更新状态
   */
  protected updateState(newState: BluetoothConnectionState): void {
    if (this.state !== newState) {
      this.state = newState;
      this.onStateChange?.(newState);
    }
  }

  /**
   * 处理收到的蓝牙通知
   */
  protected handleNotification(data: Uint8Array): void {
    // 解析 B1 回应
    const b1Response = this.protocol.parseB1Response(data);
    if (b1Response) {
      this.strengthA = b1Response.strengthA;
      this.strengthB = b1Response.strengthB;
      this.onStrengthChange?.(this.strengthA, this.strengthB);
      logger.debug(`Strength update: A=${this.strengthA}, B=${this.strengthB}`);
    }
  }

  /**
   * 设置通道强度
   */
  async setStrength(channel: Channel, value: number, mode: StrengthMode = StrengthMode.Set): Promise<void> {
    if (!this.connection?.isConnected()) {
      throw new Error('Not connected');
    }

    const command = this.protocol.buildStrengthCommand(channel, value, mode);
    await this.connection.write(command);
    logger.debug(`Set strength: channel=${channel}, value=${value}, mode=${mode}`);
  }

  /**
   * 设置软上限
   * ⚠️ 每次重连后必须调用
   */
  async setLimits(limitA: number, limitB: number): Promise<void> {
    if (!this.connection?.isConnected()) {
      throw new Error('Not connected');
    }

    const command = this.protocol.buildBFCommand({
      limitA,
      limitB,
      freqBalanceA: 128,  // 默认中间值
      freqBalanceB: 128,
      strengthBalanceA: 128,
      strengthBalanceB: 128,
    });

    await this.connection.write(command);
    this.limitA = limitA;
    this.limitB = limitB;
    logger.info(`Set limits: A=${limitA}, B=${limitB}`);
  }

  /**
   * 发送波形数据
   * @param channel 通道
   * @param waveforms 波形数据数组，每个元素包含 100ms 的数据
   * @param interval 发送间隔（毫秒），默认 100ms
   */
  async sendWaveform(
    channel: Channel,
    waveforms: ChannelWaveform[],
    interval: number = 100
  ): Promise<void> {
    if (!this.connection?.isConnected()) {
      throw new Error('Not connected');
    }

    // 停止之前的波形发送
    this.stopWaveform();

    let index = 0;
    
    const sendNext = async () => {
      if (index >= waveforms.length || !this.connection?.isConnected()) {
        this.stopWaveform();
        return;
      }

      const command = this.protocol.buildWaveformCommand(channel, waveforms[index]);
      await this.connection!.write(command);
      index++;

      this.waveformTimer = setTimeout(sendNext, interval);
    };

    await sendNext();
  }

  /**
   * 停止波形发送
   */
  stopWaveform(): void {
    if (this.waveformTimer) {
      clearTimeout(this.waveformTimer);
      this.waveformTimer = null;
    }
  }

  /**
   * 断开连接
   */
  async disconnect(): Promise<void> {
    this.stopWaveform();
    
    if (this.connection) {
      await this.connection.disconnect();
      this.connection = null;
    }

    this.updateState(BluetoothConnectionState.Disconnected);
  }

  /**
   * 抽象方法：扫描设备
   */
  abstract scan(timeout?: number): Promise<BluetoothDevice[]>;

  /**
   * 抽象方法：连接设备
   */
  abstract connect(deviceId: string): Promise<void>;
}

// 导出默认协议实例
export const defaultProtocol = new DGLabBluetoothProtocol();

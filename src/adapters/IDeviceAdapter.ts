/**
 * 统一设备适配器接口
 * 所有厂商的适配器都需要实现此接口
 */

// 通道枚举
export enum Channel {
  A = 1,
  B = 2,
}

// 强度变化模式
export enum StrengthMode {
  Decrease = 0, // 减少
  Increase = 1, // 增加
  Set = 2, // 设定为指定值
}

// 设备状态
export enum DeviceStatus {
  Disconnected = 'disconnected',
  Connecting = 'connecting',
  Connected = 'connected',
  Error = 'error',
}

// 连接配置
export interface ConnectionConfig {
  // DG-LAB 配置
  websocketUrl?: string;
  targetId?: string;

  // YOKONEX 配置
  uid?: string;
  token?: string;

  // 通用配置
  autoReconnect?: boolean;
  reconnectInterval?: number;
}

// 波形数据
export interface WaveformData {
  frequency: number[]; // 频率数组 (10-240 或映射后 10-1000)
  strength: number[]; // 强度数组 (0-100)
  duration?: number; // 持续时间(秒)
}

// 强度信息
export interface StrengthInfo {
  channelA: number;
  channelB: number;
  limitA: number;
  limitB: number;
}

// 设备状态信息
export interface DeviceState {
  status: DeviceStatus;
  strength: StrengthInfo;
  batteryLevel?: number;
  lastUpdate: Date;
}

// 反馈回调类型
export type FeedbackCallback = (index: number) => void;

// 状态变化回调类型
export type StatusChangeCallback = (status: DeviceStatus) => void;

// 强度变化回调类型
export type StrengthChangeCallback = (strength: StrengthInfo) => void;

// 错误回调类型
export type ErrorCallback = (error: Error) => void;

/**
 * 设备适配器接口
 */
export interface IDeviceAdapter {
  /** 适配器名称 */
  readonly name: string;

  /** 适配器类型标识 */
  readonly type: 'dglab' | 'yokonex';

  /**
   * 连接设备
   * @param config 连接配置
   */
  connect(config: ConnectionConfig): Promise<void>;

  /**
   * 断开连接
   */
  disconnect(): Promise<void>;

  /**
   * 设置通道强度
   * @param channel 通道
   * @param value 强度值 (0-200)
   * @param mode 变化模式
   */
  setStrength(channel: Channel, value: number, mode?: StrengthMode): Promise<void>;

  /**
   * 发送波形数据
   * @param channel 通道
   * @param data 波形数据
   */
  sendWaveform(channel: Channel, data: WaveformData): Promise<void>;

  /**
   * 清空波形队列
   * @param channel 通道
   */
  clearWaveformQueue(channel: Channel): Promise<void>;

  /**
   * 发送事件指令 (主要用于 YOKONEX)
   * @param eventId 事件ID
   * @param payload 附加数据
   */
  sendEvent(eventId: string, payload?: Record<string, unknown>): Promise<void>;

  /**
   * 获取当前设备状态
   */
  getState(): DeviceState;

  /**
   * 注册反馈回调
   */
  onFeedback(callback: FeedbackCallback): void;

  /**
   * 注册状态变化回调
   */
  onStatusChange(callback: StatusChangeCallback): void;

  /**
   * 注册强度变化回调
   */
  onStrengthChange(callback: StrengthChangeCallback): void;

  /**
   * 注册错误回调
   */
  onError(callback: ErrorCallback): void;
}

/**
 * 适配器基类 - 提供通用功能实现
 */
export abstract class BaseAdapter implements IDeviceAdapter {
  abstract readonly name: string;
  abstract readonly type: 'dglab' | 'yokonex';

  protected feedbackCallbacks: FeedbackCallback[] = [];
  protected statusChangeCallbacks: StatusChangeCallback[] = [];
  protected strengthChangeCallbacks: StrengthChangeCallback[] = [];
  protected errorCallbacks: ErrorCallback[] = [];

  protected state: DeviceState = {
    status: DeviceStatus.Disconnected,
    strength: { channelA: 0, channelB: 0, limitA: 200, limitB: 200 },
    lastUpdate: new Date(),
  };

  abstract connect(config: ConnectionConfig): Promise<void>;
  abstract disconnect(): Promise<void>;
  abstract setStrength(channel: Channel, value: number, mode?: StrengthMode): Promise<void>;
  abstract sendWaveform(channel: Channel, data: WaveformData): Promise<void>;
  abstract clearWaveformQueue(channel: Channel): Promise<void>;
  abstract sendEvent(eventId: string, payload?: Record<string, unknown>): Promise<void>;

  getState(): DeviceState {
    return { ...this.state };
  }

  onFeedback(callback: FeedbackCallback): void {
    this.feedbackCallbacks.push(callback);
  }

  onStatusChange(callback: StatusChangeCallback): void {
    this.statusChangeCallbacks.push(callback);
  }

  onStrengthChange(callback: StrengthChangeCallback): void {
    this.strengthChangeCallbacks.push(callback);
  }

  onError(callback: ErrorCallback): void {
    this.errorCallbacks.push(callback);
  }

  protected emitFeedback(index: number): void {
    this.feedbackCallbacks.forEach((cb) => cb(index));
  }

  protected emitStatusChange(status: DeviceStatus): void {
    this.state.status = status;
    this.state.lastUpdate = new Date();
    this.statusChangeCallbacks.forEach((cb) => cb(status));
  }

  protected emitStrengthChange(strength: StrengthInfo): void {
    this.state.strength = strength;
    this.state.lastUpdate = new Date();
    this.strengthChangeCallbacks.forEach((cb) => cb(strength));
  }

  protected emitError(error: Error): void {
    this.errorCallbacks.forEach((cb) => cb(error));
  }
}

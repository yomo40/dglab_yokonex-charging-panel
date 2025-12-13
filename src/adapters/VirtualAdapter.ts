/**
 * 虚拟设备适配器
 * 用于测试系统功能，模拟设备连接和控制响应
 */

import {
  BaseAdapter,
  ConnectionConfig,
  DeviceStatus,
  Channel,
  StrengthMode,
  WaveformData,
  DeviceState,
  FeedbackCallback,
  StatusChangeCallback,
  StrengthChangeCallback,
  ErrorCallback,
} from './IDeviceAdapter';
import { logger } from '../utils/logger';

/**
 * 虚拟设备适配器
 */
export class VirtualAdapter extends BaseAdapter {
  readonly name: string;
  readonly type: 'dglab' | 'yokonex';
  
  constructor(type: 'dglab' | 'yokonex' = 'dglab', name: string = '虚拟设备') {
    super();
    this.type = type;
    this.name = name || `虚拟${type === 'dglab' ? '郊狼' : '役次元'}`;
    logger.info('[VirtualAdapter]', `VirtualAdapter created: ${this.name} (${type})`);
  }

  /**
   * 连接虚拟设备（模拟连接）
   */
  async connect(config: ConnectionConfig): Promise<void> {
    logger.info('[VirtualAdapter]', `Virtual device connecting: ${this.name}`);
    
    // 模拟连接延迟
    await this.sleep(100);
    
    this.state.status = DeviceStatus.Connected;
    this.state.lastUpdate = new Date();
    
    // 通知状态变化
    this.notifyStatusChange(DeviceStatus.Connected);
    
    logger.info('[VirtualAdapter]', `Virtual device connected: ${this.name}`);
  }

  /**
   * 断开虚拟设备
   */
  async disconnect(): Promise<void> {
    logger.info('[VirtualAdapter]', `Virtual device disconnecting: ${this.name}`);
    
    this.state.status = DeviceStatus.Disconnected;
    this.state.strength.channelA = 0;
    this.state.strength.channelB = 0;
    this.state.lastUpdate = new Date();
    
    // 通知状态变化
    this.notifyStatusChange(DeviceStatus.Disconnected);
    
    logger.info('[VirtualAdapter]', `Virtual device disconnected: ${this.name}`);
  }

  /**
   * 设置强度（模拟）
   */
  async setStrength(channel: Channel, value: number, mode: StrengthMode = StrengthMode.Set): Promise<void> {
    if (this.state.status !== DeviceStatus.Connected) {
      throw new Error('Virtual device not connected');
    }
    
    const maxStrength = this.type === 'yokonex' ? 276 : 200;
    
    // 计算新强度值
    let newValue: number;
    const currentValue = channel === Channel.A 
      ? this.state.strength.channelA 
      : this.state.strength.channelB;
    
    switch (mode) {
      case StrengthMode.Set:
        newValue = value;
        break;
      case StrengthMode.Increase:
        newValue = currentValue + value;
        break;
      case StrengthMode.Decrease:
        newValue = currentValue - value;
        break;
      default:
        newValue = value;
    }
    
    // 限制范围
    const limit = channel === Channel.A 
      ? this.state.strength.limitA 
      : this.state.strength.limitB;
    newValue = Math.max(0, Math.min(Math.min(maxStrength, limit), newValue));
    
    // 更新状态
    if (channel === Channel.A) {
      this.state.strength.channelA = newValue;
    } else {
      this.state.strength.channelB = newValue;
    }
    this.state.lastUpdate = new Date();
    
    logger.info('[VirtualAdapter]', `setStrength: channel=${channel === Channel.A ? 'A' : 'B'}, value=${newValue}, mode=${StrengthMode[mode]}`);
    
    // 通知强度变化
    this.notifyStrengthChange(this.state.strength);
  }

  /**
   * 发送波形（模拟）
   */
  async sendWaveform(channel: Channel, waveform: WaveformData): Promise<void> {
    if (this.state.status !== DeviceStatus.Connected) {
      throw new Error('Virtual device not connected');
    }
    
    logger.info('[VirtualAdapter]', `sendWaveform: channel=${channel === Channel.A ? 'A' : 'B'}, points=${waveform.frequency.length}`);
    // 虚拟设备只记录日志，不做实际处理
  }

  /**
   * 清空波形队列（模拟）
   */
  async clearWaveformQueue(channel: Channel): Promise<void> {
    if (this.state.status !== DeviceStatus.Connected) {
      throw new Error('Virtual device not connected');
    }
    
    logger.info('[VirtualAdapter]', `clearWaveformQueue: channel=${channel === Channel.A ? 'A' : 'B'}`);
  }

  /**
   * 发送事件（模拟）
   */
  async sendEvent(eventId: string, payload?: Record<string, unknown>): Promise<void> {
    if (this.state.status !== DeviceStatus.Connected) {
      throw new Error('Virtual device not connected');
    }
    
    logger.info('[VirtualAdapter]', `Received event: ${eventId}`, payload);
    
    // 模拟反馈回调
    setTimeout(() => {
      this.notifyFeedback(1);
    }, 50);
  }

  // 辅助方法
  private sleep(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  private notifyStatusChange(status: DeviceStatus): void {
    for (const callback of this.statusChangeCallbacks) {
      try {
        callback(status);
      } catch (e) {
        logger.error('[VirtualAdapter]', 'Status callback error:', e);
      }
    }
  }

  private notifyStrengthChange(strength: typeof this.state.strength): void {
    for (const callback of this.strengthChangeCallbacks) {
      try {
        callback({ ...strength });
      } catch (e) {
        logger.error('[VirtualAdapter]', 'Strength callback error:', e);
      }
    }
  }

  private notifyFeedback(index: number): void {
    for (const callback of this.feedbackCallbacks) {
      try {
        callback(index);
      } catch (e) {
        logger.error('[VirtualAdapter]', 'Feedback callback error:', e);
      }
    }
  }
}

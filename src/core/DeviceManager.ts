/**
 * 设备管理器
 * 管理多个设备适配器实例
 */

import {
  IDeviceAdapter,
  ConnectionConfig,
  DeviceStatus,
  Channel,
  StrengthMode,
  WaveformData,
  DeviceState,
} from '../adapters';
import { DGLabAdapter } from '../adapters/dglab';
import { YokonexAdapter } from '../adapters/yokonex';
import { VirtualAdapter } from '../adapters/VirtualAdapter';
import { Logger } from '../utils/logger';
import { shockLogger } from '../utils/shockLogger';

const logger = new Logger('DeviceManager');

/**
 * 设备信息
 */
export interface DeviceInfo {
  id: string;
  name: string;
  type: 'dglab' | 'yokonex';
  status: DeviceStatus;
  config: ConnectionConfig;
  adapter: IDeviceAdapter;
  isVirtual?: boolean;
}

/**
 * 设备管理器类
 */
export class DeviceManager {
  private devices: Map<string, DeviceInfo> = new Map();
  private deviceCounter = 0;

  /**
   * 创建设备适配器
   */
  createAdapter(type: 'dglab' | 'yokonex', isVirtual: boolean = false, name?: string): IDeviceAdapter {
    // 如果是虚拟设备，创建虚拟适配器
    if (isVirtual) {
      return new VirtualAdapter(type, name || `虚拟${type === 'dglab' ? '郊狼' : '役次元'}`);
    }
    
    switch (type) {
      case 'dglab':
        return new DGLabAdapter();
      case 'yokonex':
        return new YokonexAdapter();
      default:
        throw new Error(`Unknown adapter type: ${type}`);
    }
  }

  /**
   * 添加设备
   */
  async addDevice(
    type: 'dglab' | 'yokonex',
    config: ConnectionConfig,
    name?: string
  ): Promise<string> {
    // 检查是否是虚拟设备
    const isVirtual = (config as any)?.isVirtual === true;
    
    const deviceName = name || `${type === 'dglab' ? '郊狼' : '役次元'} 设备 ${this.deviceCounter + 1}`;
    const adapter = this.createAdapter(type, isVirtual, deviceName);
    const id = `device_${++this.deviceCounter}`;

    const deviceInfo: DeviceInfo = {
      id,
      name: deviceName,
      type,
      status: DeviceStatus.Disconnected,
      config,
      adapter,
      isVirtual,
    };

    // 注册状态变化回调
    adapter.onStatusChange((status) => {
      deviceInfo.status = status;
      logger.info(`Device ${id} status changed: ${status}`);
    });

    adapter.onError((error) => {
      logger.error(`Device ${id} error:`, error.message);
    });

    this.devices.set(id, deviceInfo);
    logger.info(`Device added: ${id} (${deviceName})`);

    return id;
  }

  /**
   * 连接设备
   */
  async connectDevice(deviceId: string): Promise<void> {
    const device = this.getDevice(deviceId);
    await device.adapter.connect(device.config);
    logger.info(`Device connected: ${deviceId}`);
  }

  /**
   * 断开设备连接
   */
  async disconnectDevice(deviceId: string): Promise<void> {
    const device = this.getDevice(deviceId);
    await device.adapter.disconnect();
    logger.info(`Device disconnected: ${deviceId}`);
  }

  /**
   * 移除设备
   */
  async removeDevice(deviceId: string): Promise<void> {
    const device = this.devices.get(deviceId);
    if (device) {
      if (device.status === DeviceStatus.Connected) {
        await device.adapter.disconnect();
      }
      this.devices.delete(deviceId);
      logger.info(`Device removed: ${deviceId}`);
    }
  }

  /**
   * 获取设备
   */
  getDevice(deviceId: string): DeviceInfo {
    const device = this.devices.get(deviceId);
    if (!device) {
      throw new Error(`Device not found: ${deviceId}`);
    }
    return device;
  }

  /**
   * 获取所有设备
   */
  getAllDevices(): DeviceInfo[] {
    return Array.from(this.devices.values());
  }

  /**
   * 获取已连接的设备
   */
  getConnectedDevices(): DeviceInfo[] {
    return this.getAllDevices().filter((d) => d.status === DeviceStatus.Connected);
  }

  /**
   * 获取已连接设备的 ID 列表
   */
  getConnectedDeviceIds(): string[] {
    return this.getConnectedDevices().map((d) => d.id);
  }

  /**
   * 获取设备状态
   */
  getDeviceState(deviceId: string): DeviceState {
    const device = this.getDevice(deviceId);
    return device.adapter.getState();
  }

  /**
   * 设置强度
   */
  async setStrength(
    deviceId: string,
    channel: Channel,
    value: number,
    mode?: StrengthMode,
    source?: string
  ): Promise<void> {
    const device = this.getDevice(deviceId);
    await device.adapter.setStrength(channel, value, mode);
    
    // 记录电击日志
    shockLogger.logStrength({
      deviceId,
      deviceName: device.name,
      deviceType: device.type,
      channel: channel === Channel.A ? 'A' : 'B',
      value,
      mode: mode === StrengthMode.Increase ? 'increase' : mode === StrengthMode.Decrease ? 'decrease' : 'set',
      source: source || 'api',
    });
  }

  /**
   * 发送波形
   */
  async sendWaveform(deviceId: string, channel: Channel, data: WaveformData, source?: string): Promise<void> {
    const device = this.getDevice(deviceId);
    await device.adapter.sendWaveform(channel, data);
    
    // 记录电击日志
    shockLogger.logWaveform({
      deviceId,
      deviceName: device.name,
      deviceType: device.type,
      channel: channel === Channel.A ? 'A' : 'B',
      waveform: (data as any).name || 'custom',
      source: source || 'api',
      details: { duration: data.duration },
    });
  }

  /**
   * 清空波形队列
   */
  async clearWaveformQueue(deviceId: string, channel: Channel, source?: string): Promise<void> {
    const device = this.getDevice(deviceId);
    await device.adapter.clearWaveformQueue(channel);
    
    // 记录电击日志
    shockLogger.logQueue({
      deviceId,
      deviceName: device.name,
      channel: channel === Channel.A ? 'A' : 'B',
      action: 'clear',
      source: source || 'api',
    });
  }

  /**
   * 发送事件
   */
  async sendEvent(
    deviceId: string,
    eventId: string,
    payload?: Record<string, unknown>,
    source?: string
  ): Promise<void> {
    const device = this.getDevice(deviceId);
    await device.adapter.sendEvent(eventId, payload);
    
    // 记录电击日志
    shockLogger.logEvent({
      deviceId,
      deviceName: device.name,
      deviceType: device.type,
      eventId,
      eventName: eventId,
      source: source || 'api',
      details: payload as Record<string, any>,
    });
  }

  /**
   * 向所有已连接设备发送强度命令
   */
  async broadcastStrength(channel: Channel, value: number, mode?: StrengthMode, source?: string): Promise<void> {
    const connected = this.getConnectedDevices();
    
    // 记录广播电击日志
    shockLogger.logStrength({
      deviceName: '广播到所有设备',
      channel: channel === Channel.A ? 'A' : 'B',
      value,
      mode: mode === StrengthMode.Increase ? 'increase' : mode === StrengthMode.Decrease ? 'decrease' : 'set',
      source: source || 'api',
    });
    
    await Promise.all(connected.map((d) => d.adapter.setStrength(channel, value, mode)));
  }

  /**
   * 向所有已连接设备发送波形
   */
  async broadcastWaveform(channel: Channel, data: WaveformData, source?: string): Promise<void> {
    const connected = this.getConnectedDevices();
    
    // 记录广播波形日志
    shockLogger.logWaveform({
      deviceName: '广播到所有设备',
      channel: channel === Channel.A ? 'A' : 'B',
      waveform: (data as any).name || 'custom',
      source: source || 'api',
      details: { duration: data.duration },
    });
    
    await Promise.all(connected.map((d) => d.adapter.sendWaveform(channel, data)));
  }

  /**
   * 向所有已连接设备发送事件
   */
  async broadcastEvent(eventId: string, payload?: Record<string, unknown>, source?: string): Promise<void> {
    const connected = this.getConnectedDevices();
    
    // 记录广播事件日志
    shockLogger.logEvent({
      deviceName: '广播到所有设备',
      eventId,
      eventName: eventId,
      source: source || 'api',
      details: payload as Record<string, any>,
    });
    
    await Promise.all(connected.map((d) => d.adapter.sendEvent(eventId, payload)));
  }

  /**
   * 断开所有设备
   */
  async disconnectAll(): Promise<void> {
    const devices = this.getAllDevices();
    await Promise.all(devices.map((d) => this.disconnectDevice(d.id)));
  }

  /**
   * 清理所有设备
   */
  async cleanup(): Promise<void> {
    await this.disconnectAll();
    this.devices.clear();
    logger.info('All devices cleaned up');
  }
}

// 导出单例
export const deviceManager = new DeviceManager();

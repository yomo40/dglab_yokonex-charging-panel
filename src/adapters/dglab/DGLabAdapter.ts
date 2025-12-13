/**
 * DG-LAB WebSocket 适配器
 * 通过 WebSocket 中转服务连接 DG-LAB APP
 */

import WebSocket from 'ws';
import {
  BaseAdapter,
  Channel,
  ConnectionConfig,
  DeviceStatus,
  StrengthMode,
  WaveformData,
} from '../IDeviceAdapter';
import { DGLabProtocol, DGLabMessage } from './protocol';
import { WaveformGenerator } from './waveform';
import { Logger } from '../../utils/logger';

const logger = new Logger('DGLabAdapter');

export class DGLabAdapter extends BaseAdapter {
  readonly name = 'DG-LAB Adapter';
  readonly type = 'dglab' as const;

  private ws: WebSocket | null = null;
  private protocol: DGLabProtocol;
  private config: ConnectionConfig = {};
  private heartbeatTimer: NodeJS.Timeout | null = null;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private reconnectAttempts = 0;
  private maxReconnectAttempts = 10;
  private isBound = false;

  constructor() {
    super();
    this.protocol = new DGLabProtocol();
  }

  /**
   * 连接到 WebSocket 服务
   * 连接流程：
   * 1. 建立 WebSocket 连接
   * 2. 收到服务器分配的 clientId
   * 3. 生成二维码供 APP 扫描
   * 4. APP 扫码后发送绑定请求，服务器返回绑定成功
   * 5. 绑定成功后才能进行设备控制
   * 
   * @param config 连接配置，包含 websocketUrl
   * @param waitForBind 是否等待 APP 绑定成功（默认 false，只等待获取 clientId）
   */
  async connect(config: ConnectionConfig, waitForBind: boolean = false): Promise<void> {
    this.config = config;

    if (!config.websocketUrl) {
      throw new Error('WebSocket URL is required');
    }

    const wsUrl = config.websocketUrl;

    return new Promise((resolve, reject) => {
      this.emitStatusChange(DeviceStatus.Connecting);
      logger.info(`Connecting to ${wsUrl}`);

      try {
        this.ws = new WebSocket(wsUrl);

        this.ws.on('open', () => {
          logger.info('WebSocket connection established');
          this.reconnectAttempts = 0;
          this.startHeartbeat();
        });

        this.ws.on('message', (data: WebSocket.Data) => {
          this.handleMessage(data.toString());
        });

        this.ws.on('close', () => {
          logger.warn('WebSocket connection closed');
          this.cleanup();
          this.emitStatusChange(DeviceStatus.Disconnected);

          if (config.autoReconnect && this.reconnectAttempts < this.maxReconnectAttempts) {
            this.scheduleReconnect();
          }
        });

        this.ws.on('error', (error: Error) => {
          logger.error('WebSocket error:', error.message);
          this.emitError(error);
          reject(error);
        });

        // 连接超时
        const timeout = setTimeout(() => {
          reject(new Error('Connection timeout'));
        }, 10000);

        // 等待连接完成
        const checkConnection = setInterval(() => {
          // 获取到 clientId 说明已连接到服务器
          if (this.protocol.getClientId()) {
            clearInterval(checkConnection);
            clearTimeout(timeout);
            
            if (waitForBind) {
              // 等待 APP 绑定成功
              logger.info('Waiting for APP to scan QR code and bind...');
              this.emitStatusChange(DeviceStatus.Connecting);
              
              // 设置绑定等待超时（60秒）
              const bindTimeout = setTimeout(() => {
                if (!this.isBound) {
                  logger.warn('Bind timeout, but connection is ready for binding');
                  // 不 reject，允许后续绑定
                  resolve();
                }
              }, 60000);
              
              // 监听绑定成功
              const checkBind = setInterval(() => {
                if (this.isBound) {
                  clearInterval(checkBind);
                  clearTimeout(bindTimeout);
                  this.emitStatusChange(DeviceStatus.Connected);
                  logger.info('Successfully bound to APP');
                  resolve();
                }
              }, 100);
            } else {
              // 不等待绑定，获取到 clientId 即认为连接成功
              // 后续需要 APP 扫码绑定后才能发送控制指令
              this.emitStatusChange(DeviceStatus.Connected);
              logger.info('Connected to WebSocket server, waiting for APP binding');
              resolve();
            }
          }
        }, 100);
      } catch (error) {
        reject(error);
      }
    });
  }

  /**
   * 断开连接
   */
  async disconnect(): Promise<void> {
    this.cleanup();
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    this.isBound = false;
    this.emitStatusChange(DeviceStatus.Disconnected);
    logger.info('Disconnected');
  }

  /**
   * 设置通道强度
   */
  async setStrength(
    channel: Channel,
    value: number,
    mode: StrengthMode = StrengthMode.Set
  ): Promise<void> {
    this.ensureConnected();

    // 限制值范围
    const safeValue = Math.max(0, Math.min(200, value));
    const message = this.protocol.buildStrengthMessage(channel, safeValue, mode);

    this.send(message);
    logger.debug(`Set strength: channel=${channel}, value=${safeValue}, mode=${mode}`);
  }

  /**
   * 发送波形数据
   */
  async sendWaveform(channel: Channel, data: WaveformData): Promise<void> {
    this.ensureConnected();

    const hexArray = WaveformGenerator.generateHexArray(data);

    // 分批发送，每批最多100条
    const batchSize = 100;
    for (let i = 0; i < hexArray.length; i += batchSize) {
      const batch = hexArray.slice(i, i + batchSize);
      const message = this.protocol.buildWaveformMessage(channel, batch);
      this.send(message);

      // 如果还有更多数据，稍作延迟
      if (i + batchSize < hexArray.length) {
        await this.delay(50);
      }
    }

    logger.debug(`Sent waveform: channel=${channel}, length=${hexArray.length}`);
  }

  /**
   * 清空波形队列
   */
  async clearWaveformQueue(channel: Channel): Promise<void> {
    this.ensureConnected();

    const message = this.protocol.buildClearQueueMessage(channel);
    this.send(message);
    logger.debug(`Cleared waveform queue: channel=${channel}`);
  }

  /**
   * 发送事件 (DG-LAB 不支持事件模式，此方法为兼容接口)
   */
  async sendEvent(eventId: string, payload?: Record<string, unknown>): Promise<void> {
    logger.warn('DG-LAB does not support event mode, use setStrength or sendWaveform instead');

    // 尝试将事件转换为波形操作
    if (payload?.channel && payload?.strength) {
      await this.setStrength(
        payload.channel as Channel,
        payload.strength as number,
        StrengthMode.Set
      );
    }
  }

  /**
   * 获取二维码内容
   */
  getQRCodeContent(): string {
    if (!this.config.websocketUrl) {
      throw new Error('WebSocket URL not configured');
    }
    return this.protocol.generateQRCodeContent(this.config.websocketUrl);
  }

  /**
   * 获取客户端ID
   */
  getClientId(): string {
    return this.protocol.getClientId();
  }

  /**
   * 检查是否已绑定
   */
  isBoundToApp(): boolean {
    return this.isBound;
  }

  /**
   * 处理收到的消息
   */
  private handleMessage(data: string): void {
    const msg = this.protocol.parseMessage(data);
    if (!msg) {
      logger.warn('Failed to parse message:', data);
      return;
    }

    logger.debug('Received message:', msg);

    switch (msg.type) {
      case 'bind':
        this.handleBindMessage(msg);
        break;
      case 'msg':
        this.handleDataMessage(msg);
        break;
      case 'heartbeat':
        // 心跳响应，无需处理
        break;
      case 'break':
        logger.warn('Connection break:', msg.message);
        this.isBound = false;
        break;
      case 'error':
        logger.error('Server error:', msg.message);
        this.emitError(new Error(msg.message));
        break;
    }
  }

  /**
   * 处理绑定消息
   */
  private handleBindMessage(msg: DGLabMessage): void {
    const result = this.protocol.parseBindMessage(msg);

    if (result.success) {
      if (msg.message === 'targetId') {
        // 收到服务器分配的 clientId
        logger.info('Received clientId:', this.protocol.getClientId());
      } else if (msg.message === '200') {
        // 绑定成功
        this.isBound = true;
        this.protocol.setTargetId(msg.targetId);
        logger.info('Bound to APP:', msg.targetId);
        this.emitStatusChange(DeviceStatus.Connected);
      }
    } else {
      logger.error('Bind failed:', result.errorMessage);
      this.emitError(new Error(result.errorMessage || 'Bind failed'));
    }
  }

  /**
   * 处理数据消息
   */
  private handleDataMessage(msg: DGLabMessage): void {
    // 解析强度信息
    const strengthInfo = this.protocol.parseStrengthInfo(msg.message);
    if (strengthInfo) {
      this.emitStrengthChange(strengthInfo);
      return;
    }

    // 解析反馈信息
    const feedback = this.protocol.parseFeedback(msg.message);
    if (feedback !== null) {
      this.emitFeedback(feedback);
      return;
    }

    logger.debug('Unknown data message:', msg.message);
  }

  /**
   * 发送消息
   */
  private send(message: string): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(message);
    } else {
      throw new Error('WebSocket is not connected');
    }
  }

  /**
   * 确保已连接
   */
  private ensureConnected(): void {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('Not connected to WebSocket server');
    }
    if (!this.isBound) {
      throw new Error('Not bound to APP');
    }
  }

  /**
   * 开始心跳
   */
  private startHeartbeat(): void {
    const interval = this.config.reconnectInterval || 10000;
    this.heartbeatTimer = setInterval(() => {
      if (this.ws && this.ws.readyState === WebSocket.OPEN) {
        const message = this.protocol.buildHeartbeatMessage();
        this.ws.send(message);
      }
    }, interval);
  }

  /**
   * 计划重连
   */
  private scheduleReconnect(): void {
    const interval = this.config.reconnectInterval || 5000;
    this.reconnectAttempts++;

    logger.info(`Scheduling reconnect attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts}`);

    this.reconnectTimer = setTimeout(async () => {
      try {
        await this.connect(this.config);
      } catch (error) {
        logger.error('Reconnect failed:', (error as Error).message);
      }
    }, interval);
  }

  /**
   * 清理资源
   */
  private cleanup(): void {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  /**
   * 延迟
   */
  private delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

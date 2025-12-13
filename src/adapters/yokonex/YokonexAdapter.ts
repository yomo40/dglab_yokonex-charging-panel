/**
 * YOKONEX (役次元) 腾讯 IM 适配器
 * 通过腾讯云即时通讯连接役次元 APP
 */

import {
  BaseAdapter,
  Channel,
  ConnectionConfig,
  DeviceStatus,
  StrengthMode,
  WaveformData,
} from '../IDeviceAdapter';
import { EventMapper, DeviceAction, ActionType } from './eventMapper';
import { Logger } from '../../utils/logger';

const logger = new Logger('YokonexAdapter');

// 腾讯 IM SDK 类型定义
interface TencentCloudChat {
  create(options: { SDKAppID: string }): ChatInstance;
  TYPES: {
    CONV_C2C: string;
  };
  EVENT: {
    SDK_READY: string;
    MESSAGE_RECEIVED: string;
    KICKED_OUT: string;
    ERROR: string;
  };
}

interface ChatInstance {
  login(options: { userID: string; userSig: string }): Promise<any>;
  logout(): Promise<void>;
  destroy(): Promise<void>;
  on(event: string, callback: (event: any) => void): void;
  off(event: string, callback: (event: any) => void): void;
  createTextMessage(options: any): any;
  sendMessage(message: any): Promise<void>;
  setLogLevel(level: number): void;
  getLoginUser(): string;
}

/**
 * 鉴权信息
 */
interface AuthInfo {
  appId: string;
  userSig: string;
}

/**
 * 游戏指令消息
 */
interface GameCommand {
  code: string;
  id?: string;
  token?: string;
  payload?: Record<string, unknown>;
}

export class YokonexAdapter extends BaseAdapter {
  readonly name = 'YOKONEX Adapter';
  readonly type = 'yokonex' as const;

  private chat: ChatInstance | null = null;
  private TIM: TencentCloudChat | null = null;
  private config: ConnectionConfig = {};
  private eventMapper: EventMapper;
  private isReady = false;
  private uid: string = '';
  private token: string = '';
  private apiBase = 'https://suo.jiushu1234.com/api.php';

  constructor() {
    super();
    this.eventMapper = new EventMapper();
  }

  /**
   * 连接到腾讯 IM
   */
  async connect(config: ConnectionConfig): Promise<void> {
    this.config = config;

    if (!config.uid || !config.token) {
      throw new Error('uid and token are required');
    }

    this.uid = config.uid;
    this.token = config.token;

    this.emitStatusChange(DeviceStatus.Connecting);
    logger.info(`Connecting with uid: ${this.uid}`);

    try {
      // 动态加载腾讯 IM SDK
      await this.loadIMSDK();

      // 获取鉴权信息
      const authInfo = await this.requestGameSign();

      // 初始化 IM
      await this.initIM(authInfo);

      this.emitStatusChange(DeviceStatus.Connected);
      logger.info('Connected to Tencent IM');
    } catch (error) {
      this.emitStatusChange(DeviceStatus.Error);
      this.emitError(error as Error);
      throw error;
    }
  }

  /**
   * 断开连接
   */
  async disconnect(): Promise<void> {
    if (this.chat) {
      try {
        await this.chat.logout();
        await this.chat.destroy();
      } catch (error) {
        logger.warn('Error during disconnect:', (error as Error).message);
      } finally {
        this.chat = null;
        this.isReady = false;
      }
    }

    this.emitStatusChange(DeviceStatus.Disconnected);
    logger.info('Disconnected');
  }

  /**
   * 设置通道强度
   * YOKONEX 使用事件模式，此方法转换为事件
   */
  async setStrength(
    channel: Channel,
    value: number,
    mode: StrengthMode = StrengthMode.Set
  ): Promise<void> {
    // 转换为自定义指令
    await this.sendEvent('_estim_set_strength', {
      channel,
      value,
      mode,
    });
  }

  /**
   * 发送波形数据
   * YOKONEX 使用事件模式，此方法转换为事件
   */
  async sendWaveform(channel: Channel, data: WaveformData): Promise<void> {
    await this.sendEvent('_estim_custom_waveform', {
      channel,
      frequency: data.frequency,
      strength: data.strength,
      duration: data.duration,
    });
  }

  /**
   * 清空波形队列
   */
  async clearWaveformQueue(channel: Channel): Promise<void> {
    await this.sendEvent('_estim_clear_queue', { channel });
  }

  /**
   * 发送游戏事件指令
   */
  async sendEvent(eventId: string, payload?: Record<string, unknown>): Promise<void> {
    this.ensureConnected();

    const command: GameCommand = {
      code: 'game_cmd',
      id: eventId,
      token: this.token,
    };

    if (payload) {
      command.payload = payload;
    }

    await this.sendIMMessage(command);
    logger.debug(`Sent event: ${eventId}`);
  }

  /**
   * 触发已注册的事件
   */
  async triggerEvent(eventId: string): Promise<DeviceAction[] | null> {
    const actions = this.eventMapper.triggerEvent(eventId);

    if (actions) {
      // 发送到设备
      await this.sendEvent(eventId);
    }

    return actions;
  }

  /**
   * 获取事件映射器
   */
  getEventMapper(): EventMapper {
    return this.eventMapper;
  }

  /**
   * 加载腾讯 IM SDK
   */
  private async loadIMSDK(): Promise<void> {
    try {
      // 尝试加载 SDK
      const TencentCloudChat = await import('@tencentcloud/chat');
      this.TIM = (TencentCloudChat.default || TencentCloudChat) as any;
      logger.info('Tencent IM SDK loaded');
    } catch (error) {
      logger.error('Failed to load Tencent IM SDK:', (error as Error).message);
      throw new Error('Tencent IM SDK not installed. Run: npm install @tencentcloud/chat');
    }
  }

  /**
   * 请求游戏签名
   */
  private async requestGameSign(): Promise<AuthInfo> {
    const url = `${this.apiBase}/user/game_sign`;

    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          uid: `game_${this.uid}`,
          token: this.token,
        }),
      });

      if (!response.ok) {
        throw new Error(`HTTP error: ${response.status}`);
      }

      const result: any = await response.json();

      if (result.code !== 1 || !result.data) {
        throw new Error(`API error: ${result.msg || 'Unknown error'}`);
      }

      return {
        appId: result.data.appid,
        userSig: result.data.sign,
      };
    } catch (error) {
      logger.error('Failed to get game sign:', (error as Error).message);
      throw error;
    }
  }

  /**
   * 初始化 IM
   */
  private async initIM(authInfo: AuthInfo): Promise<void> {
    if (!this.TIM) {
      throw new Error('IM SDK not loaded');
    }

    // 创建 IM 实例
    this.chat = this.TIM.create({ SDKAppID: authInfo.appId });
    this.chat.setLogLevel(4); // 关闭日志

    // 注册事件监听
    this.setupEventListeners();

    // 登录
    const userID = `game_${this.uid}`;
    logger.info(`Logging in as ${userID}`);

    const result = await this.chat.login({
      userID,
      userSig: authInfo.userSig,
    });

    if (result?.data?.repeatLogin) {
      logger.warn('Repeat login detected');
    }

    // 等待 SDK 就绪
    await this.waitForReady();
  }

  /**
   * 设置事件监听
   */
  private setupEventListeners(): void {
    if (!this.chat || !this.TIM) return;

    this.chat.on(this.TIM.EVENT.SDK_READY, () => {
      this.isReady = true;
      logger.info('IM SDK ready');
    });

    this.chat.on(this.TIM.EVENT.MESSAGE_RECEIVED, (event: any) => {
      this.handleReceivedMessages(event.data);
    });

    this.chat.on(this.TIM.EVENT.KICKED_OUT, async () => {
      logger.warn('Kicked out from IM');
      this.isReady = false;
      this.emitStatusChange(DeviceStatus.Disconnected);

      // 尝试重连
      if (this.config.autoReconnect) {
        setTimeout(() => this.reconnect(), 3000);
      }
    });

    this.chat.on(this.TIM.EVENT.ERROR, (event: any) => {
      logger.error('IM error:', event?.message || event);
      this.emitError(new Error(event?.message || 'IM error'));
    });
  }

  /**
   * 处理收到的消息
   */
  private handleReceivedMessages(messages: any[]): void {
    for (const msg of messages) {
      try {
        const content = JSON.parse(msg.payload.text);
        logger.debug('Received message:', content);

        // 处理设备状态更新等
        if (content.code === 'device_status') {
          // 更新设备状态
        }
      } catch {
        logger.debug('Received raw message:', msg.payload.text);
      }
    }
  }

  /**
   * 等待 SDK 就绪
   */
  private waitForReady(timeout: number = 15000): Promise<void> {
    return new Promise((resolve, reject) => {
      if (this.isReady) {
        resolve();
        return;
      }

      const timer = setTimeout(() => {
        reject(new Error('Wait for SDK_READY timeout'));
      }, timeout);

      const checkReady = setInterval(() => {
        if (this.isReady) {
          clearInterval(checkReady);
          clearTimeout(timer);
          resolve();
        }
      }, 100);
    });
  }

  /**
   * 发送 IM 消息
   */
  private async sendIMMessage(command: GameCommand): Promise<void> {
    if (!this.chat || !this.TIM) {
      throw new Error('IM not initialized');
    }

    const message = this.chat.createTextMessage({
      to: this.uid, // 发送给用户（不带 game_ 前缀）
      conversationType: this.TIM.TYPES.CONV_C2C,
      payload: {
        text: JSON.stringify(command),
      },
    });

    await this.chat.sendMessage(message);
  }

  /**
   * 重连
   */
  private async reconnect(): Promise<void> {
    logger.info('Attempting to reconnect...');
    try {
      await this.connect(this.config);
    } catch (error) {
      logger.error('Reconnect failed:', (error as Error).message);
    }
  }

  /**
   * 确保已连接
   */
  private ensureConnected(): void {
    if (!this.chat || !this.isReady) {
      throw new Error('Not connected to IM');
    }
  }
}

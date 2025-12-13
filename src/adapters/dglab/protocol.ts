/**
 * DG-LAB 协议编解码器
 * 处理 WebSocket 消息与蓝牙协议的转换
 */

import { Channel, StrengthMode, StrengthInfo, WaveformData } from '../IDeviceAdapter';

// 消息类型
export type DGLabMessageType = 'heartbeat' | 'bind' | 'msg' | 'break' | 'error';

// DG-LAB 消息结构
export interface DGLabMessage {
  type: DGLabMessageType;
  clientId: string;
  targetId: string;
  message: string;
}

// 绑定状态
export interface BindStatus {
  success: boolean;
  errorCode?: string;
  errorMessage?: string;
}

/**
 * 错误码映射
 */
export const ErrorCodes: Record<string, string> = {
  '200': '成功',
  '209': '对方客户端已断开',
  '210': '二维码中没有有效的clientID',
  '211': 'socket连接上了，但服务器迟迟不下发app端的id来绑定',
  '400': '此id已被其他客户端绑定关系',
  '401': '要绑定的目标客户端不存在',
  '402': '收信方和寄信方不是绑定关系',
  '403': '发送的内容不是标准json对象',
  '404': '未找到收信人（离线）',
  '405': '下发的message长度大于1950',
  '500': '服务器内部异常',
};

/**
 * 协议编解码类
 */
export class DGLabProtocol {
  private clientId: string = '';
  private targetId: string = '';

  /**
   * 设置客户端ID
   */
  setClientId(id: string): void {
    this.clientId = id;
  }

  /**
   * 设置目标ID (APP端)
   */
  setTargetId(id: string): void {
    this.targetId = id;
  }

  /**
   * 获取客户端ID
   */
  getClientId(): string {
    return this.clientId;
  }

  /**
   * 获取目标ID
   */
  getTargetId(): string {
    return this.targetId;
  }

  /**
   * 解析收到的消息
   */
  parseMessage(data: string): DGLabMessage | null {
    try {
      const msg = JSON.parse(data) as DGLabMessage;
      if (msg.type && msg.message !== undefined) {
        return msg;
      }
      return null;
    } catch {
      return null;
    }
  }

  /**
   * 解析绑定消息
   */
  parseBindMessage(msg: DGLabMessage): BindStatus {
    if (msg.type !== 'bind') {
      return { success: false, errorMessage: '非绑定消息' };
    }

    // 服务器返回clientId
    if (msg.message === 'targetId' && msg.clientId) {
      this.clientId = msg.clientId;
      return { success: true };
    }

    // 绑定结果
    if (msg.message === '200') {
      return { success: true };
    }

    return {
      success: false,
      errorCode: msg.message,
      errorMessage: ErrorCodes[msg.message] || '未知错误',
    };
  }

  /**
   * 解析强度信息
   * 格式: strength-A通道强度+B通道强度+A强度上限+B强度上限
   */
  parseStrengthInfo(message: string): StrengthInfo | null {
    const match = message.match(/^strength-(\d+)\+(\d+)\+(\d+)\+(\d+)$/);
    if (!match) return null;

    return {
      channelA: parseInt(match[1], 10),
      channelB: parseInt(match[2], 10),
      limitA: parseInt(match[3], 10),
      limitB: parseInt(match[4], 10),
    };
  }

  /**
   * 解析反馈信息
   * 格式: feedback-index
   */
  parseFeedback(message: string): number | null {
    const match = message.match(/^feedback-(\d+)$/);
    if (!match) return null;
    return parseInt(match[1], 10);
  }

  /**
   * 构建消息基础结构
   */
  private buildMessage(type: DGLabMessageType, message: string): DGLabMessage {
    return {
      type,
      clientId: this.clientId,
      targetId: this.targetId,
      message,
    };
  }

  /**
   * 构建强度控制消息
   * 格式: strength-通道+强度变化模式+数值
   */
  buildStrengthMessage(channel: Channel, value: number, mode: StrengthMode): string {
    const msg = this.buildMessage('msg', `strength-${channel}+${mode}+${value}`);
    return JSON.stringify(msg);
  }

  /**
   * 构建波形数据消息
   * 格式: pulse-通道:[波形数据数组]
   */
  buildWaveformMessage(channel: Channel, hexData: string[]): string {
    const channelStr = channel === Channel.A ? 'A' : 'B';
    const dataStr = JSON.stringify(hexData);
    const msg = this.buildMessage('msg', `pulse-${channelStr}:${dataStr}`);
    return JSON.stringify(msg);
  }

  /**
   * 构建清空队列消息
   * 格式: clear-通道
   */
  buildClearQueueMessage(channel: Channel): string {
    const msg = this.buildMessage('msg', `clear-${channel}`);
    return JSON.stringify(msg);
  }

  /**
   * 构建心跳消息
   */
  buildHeartbeatMessage(): string {
    const msg = this.buildMessage('heartbeat', '200');
    return JSON.stringify(msg);
  }

  /**
   * 生成二维码内容
   * 格式: https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#ws地址/clientId
   * 
   * 官方文档要求：
   * - 二维码必须包含 APP 官网下载地址
   * - 必须包含 DGLAB-SOCKET 标签
   * - 服务地址与 clientId 之间不得有其他内容（只能用 / 连接）
   * - 有且仅有两个 # 分割内容
   */
  generateQRCodeContent(wsUrl: string): string {
    // 保持原始协议格式（ws:// 或 wss://），不强制转换
    // 本地服务器通常使用 ws://，公网服务器使用 wss://
    return `https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#${wsUrl}/${this.clientId}`;
  }
}

/**
 * 前端协议转换 (用于自建后端时)
 * 将前端格式转换为APP格式
 */
export class FrontendProtocolConverter {
  /**
   * 转换强度操作
   * 前端格式: { type: 1-4, strength, channel, ... }
   * APP格式: strength-通道+模式+数值
   */
  static convertStrengthCommand(
    frontendType: number,
    channel: number,
    strength: number
  ): { channel: Channel; mode: StrengthMode; value: number } {
    const ch = channel === 1 ? Channel.A : Channel.B;

    switch (frontendType) {
      case 1: // 减少
        return { channel: ch, mode: StrengthMode.Decrease, value: strength };
      case 2: // 增加
        return { channel: ch, mode: StrengthMode.Increase, value: strength };
      case 3: // 归零
        return { channel: ch, mode: StrengthMode.Set, value: 0 };
      case 4: // 指定值
        return { channel: ch, mode: StrengthMode.Set, value: strength };
      default:
        throw new Error(`Unknown frontend type: ${frontendType}`);
    }
  }
}

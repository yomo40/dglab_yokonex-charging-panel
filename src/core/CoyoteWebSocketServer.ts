/**
 * 内置郊狼 WebSocket 服务器
 * 完全遵循 DG-LAB 官方 WebSocket 协议
 * 
 * 协议文档: ExternalApiAdapter/DG-LAB-OPENSOURCE/socket/README.md
 * @version 0.95
 */

import WebSocket, { WebSocketServer } from 'ws';
import { createServer, Server as HttpServer } from 'http';
import { v4 as uuidv4 } from 'uuid';
import { Logger } from '../utils/logger';
import * as os from 'os';

const logger = new Logger('CoyoteWSServer');

/**
 * DG-LAB 消息结构
 */
interface DGLabMessage {
  type: string | number;
  clientId: string;
  targetId: string;
  message: string;
  // 前端协议额外字段
  channel?: string | number;
  strength?: number;
  time?: number;
}

/**
 * 郊狼 WebSocket 服务器配置
 */
export interface CoyoteServerConfig {
  port: number;
  host?: string;
  heartbeatInterval?: number;
}

/**
 * 客户端信息
 */
interface ClientInfo {
  id: string;
  ws: WebSocket;
  boundTo: string | null;  // 绑定的对方ID
}

/**
 * 郊狼 WebSocket 服务器
 * 
 * 实现 DG-LAB 官方 WebSocket 协议，支持：
 * - 客户端连接管理
 * - APP与第三方程序绑定
 * - 消息转发
 * - 心跳检测
 */
export class CoyoteWebSocketServer {
  private wss: WebSocketServer | null = null;
  private httpServer: HttpServer | null = null;
  
  // 储存已连接的客户端及其标识
  private clients: Map<string, ClientInfo> = new Map();
  
  // 存储绑定关系: clientId(第三方) -> targetId(APP)
  private relations: Map<string, string> = new Map();
  
  private config: CoyoteServerConfig;
  private isRunning = false;
  private heartbeatTimer: NodeJS.Timeout | null = null;

  constructor(config: CoyoteServerConfig) {
    this.config = {
      host: '0.0.0.0',
      heartbeatInterval: 60000,  // 每分钟发送心跳
      ...config
    };
  }

  /**
   * 启动服务器
   */
  async start(): Promise<string> {
    if (this.isRunning) {
      logger.warn('Server already running');
      return this.getServerUrl();
    }

    return new Promise((resolve, reject) => {
      try {
        this.httpServer = createServer();
        this.wss = new WebSocketServer({ server: this.httpServer });

        this.wss.on('connection', (ws: WebSocket) => {
          this.handleConnection(ws);
        });

        this.wss.on('error', (error) => {
          logger.error('WebSocket server error:', error.message);
        });

        this.httpServer.listen(this.config.port, this.config.host, () => {
          this.isRunning = true;
          this.startHeartbeat();
          const url = this.getServerUrl();
          logger.info(`郊狼 WebSocket 服务器已启动: ${url}`);
          resolve(url);
        });

        this.httpServer.on('error', (error: NodeJS.ErrnoException) => {
          if (error.code === 'EADDRINUSE') {
            logger.error(`端口 ${this.config.port} 已被占用`);
            reject(new Error(`端口 ${this.config.port} 已被占用`));
          } else {
            reject(error);
          }
        });
      } catch (error) {
        reject(error);
      }
    });
  }

  /**
   * 停止服务器
   */
  async stop(): Promise<void> {
    if (!this.isRunning) return;

    // 停止心跳
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }

    // 关闭所有客户端连接
    for (const client of this.clients.values()) {
      client.ws.close(1000, 'Server shutting down');
    }
    this.clients.clear();
    this.relations.clear();

    // 关闭服务器
    return new Promise((resolve) => {
      if (this.wss) {
        this.wss.close(() => {
          if (this.httpServer) {
            this.httpServer.close(() => {
              this.isRunning = false;
              logger.info('郊狼 WebSocket 服务器已停止');
              resolve();
            });
          } else {
            this.isRunning = false;
            resolve();
          }
        });
      } else {
        this.isRunning = false;
        resolve();
      }
    });
  }

  /**
   * 获取服务器URL (内部使用)
   */
  getServerUrl(): string {
    return `ws://${this.config.host === '0.0.0.0' ? 'localhost' : this.config.host}:${this.config.port}`;
  }

  /**
   * 获取可公开访问的服务器URL（使用实际局域网IP）
   */
  getPublicServerUrl(): string {
    const host = this.config.host === '0.0.0.0' ? this.getLocalIP() : this.config.host;
    return `ws://${host}:${this.config.port}`;
  }

  /**
   * 获取服务器状态
   */
  getStatus() {
    const clientList: Array<{
      id: string;
      type: string;
      boundTo: string | null;
      connected: boolean;
    }> = [];

    for (const [id, client] of this.clients) {
      // 判断客户端类型：如果在relations的key中，则是第三方程序；如果在values中，则是APP
      let type = 'unknown';
      if (this.relations.has(id)) {
        type = 'third';  // 第三方程序
      } else if ([...this.relations.values()].includes(id)) {
        type = 'app';    // DG-LAB APP
      }

      clientList.push({
        id,
        type,
        boundTo: client.boundTo,
        connected: client.ws.readyState === WebSocket.OPEN
      });
    }

    return {
      running: this.isRunning,
      url: this.getServerUrl(),
      publicUrl: this.getPublicServerUrl(),
      clientCount: this.clients.size,
      bindingCount: this.relations.size,
      clients: clientList
    };
  }

  /**
   * 处理新连接
   */
  private handleConnection(ws: WebSocket): void {
    // 生成唯一的标识符 (UUID v4格式)
    const clientId = uuidv4();
    
    logger.info(`新的 WebSocket 连接已建立，标识符为: ${clientId}`);

    // 存储客户端
    const clientInfo: ClientInfo = {
      id: clientId,
      ws,
      boundTo: null
    };
    this.clients.set(clientId, clientInfo);

    // 发送标识符给客户端（格式固定，双方都必须获取才可以进行后续通信）
    // 协议: {"type":"bind","clientId":"xxx","targetId":"","message":"targetId"}
    this.sendMessage(ws, {
      type: 'bind',
      clientId: clientId,
      targetId: '',
      message: 'targetId'
    });

    ws.on('message', (data: WebSocket.Data) => {
      this.handleMessage(clientId, ws, data.toString());
    });

    ws.on('close', () => {
      this.handleDisconnect(clientId);
    });

    ws.on('error', (error) => {
      logger.error(`客户端 ${clientId} 错误:`, error.message);
      this.handleError(clientId, error);
    });
  }

  /**
   * 处理消息
   */
  private handleMessage(senderClientId: string, senderWs: WebSocket, data: string): void {
    logger.debug(`收到消息 [${senderClientId}]: ${data}`);

    let msg: DGLabMessage;
    try {
      msg = JSON.parse(data);
    } catch (e) {
      // 非JSON数据处理
      this.sendMessage(senderWs, {
        type: 'msg',
        clientId: '',
        targetId: '',
        message: '403'
      });
      return;
    }

    // 验证消息来源合法性
    if (msg.clientId && msg.targetId) {
      const clientExists = this.clients.has(msg.clientId);
      const targetExists = this.clients.has(msg.targetId);
      
      // 发送者必须是clientId或targetId对应的客户端
      if (msg.clientId !== senderClientId && msg.targetId !== senderClientId) {
        // 如果发送者既不是clientId也不是targetId，拒绝
        if (clientExists || targetExists) {
          this.sendMessage(senderWs, {
            type: 'msg',
            clientId: '',
            targetId: '',
            message: '404'
          });
          return;
        }
      }
    }

    // 根据消息类型处理
    const msgType = msg.type;
    
    if (msgType === 'bind') {
      this.handleBindMessage(senderClientId, senderWs, msg);
    } else if (msgType === 'msg') {
      // 普通消息转发
      this.handleMsgMessage(senderClientId, senderWs, msg);
    } else if (msgType === 'heartbeat') {
      // 心跳响应
      this.sendMessage(senderWs, {
        type: 'heartbeat',
        clientId: senderClientId,
        targetId: '',
        message: '200'
      });
    } else if (msgType === 1 || msgType === 2 || msgType === 3 || msgType === 4) {
      // 前端协议：强度操作
      this.handleStrengthCommand(senderClientId, senderWs, msg);
    } else if (msgType === 'clientMsg') {
      // 前端协议：波形数据
      this.handleWaveformCommand(senderClientId, senderWs, msg);
    } else {
      // 其他消息类型，尝试转发
      this.handleDefaultMessage(senderClientId, senderWs, msg);
    }
  }

  /**
   * 处理绑定消息
   * APP扫描二维码后发送: {"type":"bind","clientId":"第三方ID","targetId":"APP的ID","message":"DGLAB"}
   */
  private handleBindMessage(senderClientId: string, senderWs: WebSocket, msg: DGLabMessage): void {
    const { clientId, targetId, message } = msg;

    // 如果message是"DGLAB"，这是APP发来的绑定请求
    if (message === 'DGLAB') {
      // clientId = 第三方程序ID (从二维码扫描获得)
      // targetId = APP自己的ID
      // senderClientId = 实际发送者（应该是APP，即targetId）
      
      logger.info(`收到APP绑定请求: 第三方=${clientId}, APP=${targetId}`);

      // 验证两个客户端都存在
      if (!this.clients.has(clientId) || !this.clients.has(targetId)) {
        const errorMsg = {
          type: 'bind',
          clientId,
          targetId,
          message: '401'  // 目标客户端不存在
        };
        this.sendMessage(senderWs, errorMsg);
        return;
      }

      // 检查是否已经绑定
      const alreadyBound = this.relations.has(clientId) || 
                          [...this.relations.values()].includes(clientId) ||
                          this.relations.has(targetId) || 
                          [...this.relations.values()].includes(targetId);
      
      if (alreadyBound) {
        const errorMsg = {
          type: 'bind',
          clientId,
          targetId,
          message: '400'  // 已被其他客户端绑定
        };
        this.sendMessage(senderWs, errorMsg);
        return;
      }

      // 建立绑定关系: 第三方程序ID -> APP ID
      this.relations.set(clientId, targetId);
      
      // 更新客户端信息
      const thirdClient = this.clients.get(clientId)!;
      const appClient = this.clients.get(targetId)!;
      thirdClient.boundTo = targetId;
      appClient.boundTo = clientId;

      // 通知双方绑定成功
      const successMsg = {
        type: 'bind',
        clientId,
        targetId,
        message: '200'
      };
      
      // 通知APP
      this.sendMessage(senderWs, successMsg);
      
      // 通知第三方程序
      this.sendMessage(thirdClient.ws, successMsg);

      logger.info(`绑定成功: 第三方程序(${clientId}) <-> APP(${targetId})`);
      return;
    }

    // 其他绑定消息（不应该出现在正常流程中）
    logger.warn(`收到未知绑定消息: ${message}`);
  }

  /**
   * 处理普通消息转发
   */
  private handleMsgMessage(senderClientId: string, senderWs: WebSocket, msg: DGLabMessage): void {
    const { clientId, targetId, message } = msg;

    // 验证绑定关系
    if (!this.isValidRelation(clientId, targetId)) {
      this.sendMessage(senderWs, {
        type: 'bind',
        clientId,
        targetId,
        message: '402'  // 收信方和寄信方不是绑定关系
      });
      return;
    }

    // 找到目标客户端并转发
    // 如果发送者是第三方程序，目标是APP；反之亦然
    let targetClientId: string;
    if (this.relations.get(clientId) === targetId) {
      // 第三方 -> APP
      targetClientId = targetId;
    } else {
      // APP -> 第三方
      targetClientId = clientId;
    }

    const targetClient = this.clients.get(targetClientId);
    if (!targetClient || targetClient.ws.readyState !== WebSocket.OPEN) {
      this.sendMessage(senderWs, {
        type: 'msg',
        clientId,
        targetId,
        message: '404'  // 未找到收信人
      });
      return;
    }

    // 转发消息
    this.sendMessage(targetClient.ws, {
      type: 'msg',
      clientId,
      targetId,
      message
    });
  }

  /**
   * 处理强度控制命令 (前端协议)
   * type: 1=减少, 2=增加, 3=归零, 4=指定值
   */
  private handleStrengthCommand(senderClientId: string, senderWs: WebSocket, msg: DGLabMessage): void {
    const { clientId, targetId, type, channel, strength } = msg;

    // 验证绑定关系
    if (!this.isValidRelation(clientId, targetId)) {
      this.sendMessage(senderWs, {
        type: 'bind',
        clientId,
        targetId,
        message: '402'
      });
      return;
    }

    const appClient = this.clients.get(targetId);
    if (!appClient || appClient.ws.readyState !== WebSocket.OPEN) {
      this.sendMessage(senderWs, {
        type: 'msg',
        clientId,
        targetId,
        message: '404'
      });
      return;
    }

    // 转换为APP协议格式
    // strength-通道+模式+数值
    // 模式: 0=减少, 1=增加, 2=指定值
    const sendChannel = channel || 1;
    let sendMode: number;
    let sendStrength: number;

    const typeNum = typeof type === 'number' ? type : parseInt(type as string);
    
    switch (typeNum) {
      case 1:  // 减少
        sendMode = 0;
        sendStrength = 1;  // 增减模式强度改成1
        break;
      case 2:  // 增加
        sendMode = 1;
        sendStrength = 1;  // 增减模式强度改成1
        break;
      case 3:  // 归零
        sendMode = 2;
        sendStrength = 0;
        break;
      case 4:  // 指定值
        sendMode = 2;
        sendStrength = strength || 0;
        break;
      default:
        return;
    }

    const appMessage = `strength-${sendChannel}+${sendMode}+${sendStrength}`;
    
    this.sendMessage(appClient.ws, {
      type: 'msg',
      clientId,
      targetId,
      message: appMessage
    });
  }

  /**
   * 处理波形数据命令 (前端协议)
   */
  private handleWaveformCommand(senderClientId: string, senderWs: WebSocket, msg: DGLabMessage): void {
    const { clientId, targetId, message, channel } = msg;

    // 验证绑定关系
    if (!this.isValidRelation(clientId, targetId)) {
      this.sendMessage(senderWs, {
        type: 'bind',
        clientId,
        targetId,
        message: '402'
      });
      return;
    }

    if (!channel) {
      this.sendMessage(senderWs, {
        type: 'error',
        clientId,
        targetId,
        message: '406-channel is empty'
      });
      return;
    }

    const appClient = this.clients.get(targetId);
    if (!appClient || appClient.ws.readyState !== WebSocket.OPEN) {
      this.sendMessage(senderWs, {
        type: 'msg',
        clientId,
        targetId,
        message: '404'
      });
      return;
    }

    // 转换为APP协议格式: pulse-通道:["波形数据"]
    const pulseMessage = `pulse-${channel}:${message}`;
    
    this.sendMessage(appClient.ws, {
      type: 'msg',
      clientId,
      targetId,
      message: pulseMessage
    });
  }

  /**
   * 处理默认消息（直接转发）
   */
  private handleDefaultMessage(senderClientId: string, senderWs: WebSocket, msg: DGLabMessage): void {
    const { clientId, targetId, type, message } = msg;

    // 验证绑定关系
    if (!this.isValidRelation(clientId, targetId)) {
      this.sendMessage(senderWs, {
        type: 'bind',
        clientId,
        targetId,
        message: '402'
      });
      return;
    }

    // 找到对方并转发
    const targetClient = this.clients.get(clientId);
    if (targetClient && targetClient.ws.readyState === WebSocket.OPEN) {
      this.sendMessage(targetClient.ws, {
        type,
        clientId,
        targetId,
        message
      });
    } else {
      this.sendMessage(senderWs, {
        type: 'msg',
        clientId,
        targetId,
        message: '404'
      });
    }
  }

  /**
   * 验证绑定关系是否有效
   */
  private isValidRelation(clientId: string, targetId: string): boolean {
    // relations: 第三方ID -> APP ID
    return this.relations.get(clientId) === targetId || 
           this.relations.get(targetId) === clientId;
  }

  /**
   * 处理断开连接
   */
  private handleDisconnect(clientId: string): void {
    logger.info(`WebSocket 连接已关闭: ${clientId}`);

    // 查找并通知绑定的对方
    for (const [thirdId, appId] of this.relations) {
      if (thirdId === clientId) {
        // 第三方程序断开，通知APP
        const appClient = this.clients.get(appId);
        if (appClient && appClient.ws.readyState === WebSocket.OPEN) {
          this.sendMessage(appClient.ws, {
            type: 'break',
            clientId: thirdId,
            targetId: appId,
            message: '209'  // 对方客户端已断开
          });
          appClient.ws.close();
        }
        this.relations.delete(thirdId);
        this.clients.delete(appId);
        logger.info(`对方掉线，关闭 ${appId}`);
        break;
      } else if (appId === clientId) {
        // APP断开，通知第三方程序
        const thirdClient = this.clients.get(thirdId);
        if (thirdClient && thirdClient.ws.readyState === WebSocket.OPEN) {
          this.sendMessage(thirdClient.ws, {
            type: 'break',
            clientId: thirdId,
            targetId: appId,
            message: '209'  // 对方客户端已断开
          });
          thirdClient.ws.close();
        }
        this.relations.delete(thirdId);
        this.clients.delete(thirdId);
        logger.info(`对方掉线，关闭 ${thirdId}`);
        break;
      }
    }

    // 移除客户端
    this.clients.delete(clientId);
    logger.info(`已清除 ${clientId}, 当前连接数: ${this.clients.size}`);
  }

  /**
   * 处理错误
   */
  private handleError(clientId: string, error: Error): void {
    // 遍历关系，通知对方
    for (const [thirdId, appId] of this.relations) {
      if (thirdId === clientId) {
        const appClient = this.clients.get(appId);
        if (appClient) {
          this.sendMessage(appClient.ws, {
            type: 'error',
            clientId: thirdId,
            targetId: appId,
            message: '500'
          });
        }
      } else if (appId === clientId) {
        const thirdClient = this.clients.get(thirdId);
        if (thirdClient) {
          this.sendMessage(thirdClient.ws, {
            type: 'error',
            clientId: thirdId,
            targetId: appId,
            message: error.message
          });
        }
      }
    }
  }

  /**
   * 启动心跳定时器
   */
  private startHeartbeat(): void {
    this.heartbeatTimer = setInterval(() => {
      if (this.clients.size > 0) {
        logger.debug(`发送心跳消息，当前连接数: ${this.clients.size}`);
        
        for (const [clientId, client] of this.clients) {
          if (client.ws.readyState === WebSocket.OPEN) {
            this.sendMessage(client.ws, {
              type: 'heartbeat',
              clientId: clientId,
              targetId: client.boundTo || '',
              message: '200'
            });
          }
        }
      }
    }, this.config.heartbeatInterval);
  }

  /**
   * 发送消息
   */
  private sendMessage(ws: WebSocket, msg: Record<string, unknown>): void {
    if (ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(msg));
    }
  }

  /**
   * 生成APP扫码绑定的二维码内容
   * 格式: https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#ws://host:port/clientId
   */
  generateBindUrl(clientId: string): string {
    const serverUrl = this.getPublicServerUrl();
    // 注意：服务器地址和clientId之间用/连接，不能有其他内容
    return `https://www.dungeon-lab.com/app-download.php#DGLAB-SOCKET#${serverUrl}/${clientId}`;
  }

  /**
   * 获取本机局域网IP地址
   */
  private getLocalIP(): string {
    const interfaces = os.networkInterfaces();
    
    for (const name of Object.keys(interfaces)) {
      for (const iface of interfaces[name] || []) {
        // 跳过内部地址和非IPv4地址
        if (iface.family === 'IPv4' && !iface.internal) {
          return iface.address;
        }
      }
    }
    return 'localhost';
  }

  /**
   * 向绑定的APP发送强度控制命令
   * @param thirdClientId 第三方程序的clientId
   * @param channel 通道 (1=A, 2=B)
   * @param mode 模式 (0=减少, 1=增加, 2=指定值)
   * @param value 数值 (0-200)
   */
  sendStrengthToApp(thirdClientId: string, channel: number, mode: number, value: number): boolean {
    const appId = this.relations.get(thirdClientId);
    if (!appId) {
      logger.warn(`第三方程序 ${thirdClientId} 未绑定APP`);
      return false;
    }

    const appClient = this.clients.get(appId);
    if (!appClient || appClient.ws.readyState !== WebSocket.OPEN) {
      logger.warn(`APP ${appId} 未连接`);
      return false;
    }

    const message = `strength-${channel}+${mode}+${value}`;
    this.sendMessage(appClient.ws, {
      type: 'msg',
      clientId: thirdClientId,
      targetId: appId,
      message
    });

    return true;
  }

  /**
   * 向绑定的APP发送波形数据
   * @param thirdClientId 第三方程序的clientId
   * @param channel 通道 (A/B)
   * @param waveformData 波形数据数组 (8字节HEX格式)
   */
  sendWaveformToApp(thirdClientId: string, channel: 'A' | 'B', waveformData: string[]): boolean {
    const appId = this.relations.get(thirdClientId);
    if (!appId) {
      logger.warn(`第三方程序 ${thirdClientId} 未绑定APP`);
      return false;
    }

    const appClient = this.clients.get(appId);
    if (!appClient || appClient.ws.readyState !== WebSocket.OPEN) {
      logger.warn(`APP ${appId} 未连接`);
      return false;
    }

    const message = `pulse-${channel}:${JSON.stringify(waveformData)}`;
    this.sendMessage(appClient.ws, {
      type: 'msg',
      clientId: thirdClientId,
      targetId: appId,
      message
    });

    return true;
  }

  /**
   * 清空APP的波形队列
   * @param thirdClientId 第三方程序的clientId
   * @param channel 通道 (1=A, 2=B)
   */
  clearWaveformQueue(thirdClientId: string, channel: 1 | 2): boolean {
    const appId = this.relations.get(thirdClientId);
    if (!appId) return false;

    const appClient = this.clients.get(appId);
    if (!appClient || appClient.ws.readyState !== WebSocket.OPEN) return false;

    const message = `clear-${channel}`;
    this.sendMessage(appClient.ws, {
      type: 'msg',
      clientId: thirdClientId,
      targetId: appId,
      message
    });

    return true;
  }
}

// 单例实例
let serverInstance: CoyoteWebSocketServer | null = null;

/**
 * 获取或创建郊狼服务器实例
 */
export function getCoyoteServer(config?: CoyoteServerConfig): CoyoteWebSocketServer {
  if (!serverInstance && config) {
    serverInstance = new CoyoteWebSocketServer(config);
  }
  if (!serverInstance) {
    throw new Error('郊狼服务器未初始化');
  }
  return serverInstance;
}

/**
 * 关闭郊狼服务器
 */
export async function closeCoyoteServer(): Promise<void> {
  if (serverInstance) {
    await serverInstance.stop();
    serverInstance = null;
  }
}

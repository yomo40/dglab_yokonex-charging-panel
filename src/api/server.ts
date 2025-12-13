/**
 * API 服务器
 * 提供 HTTP 和 WebSocket API
 */

import express, { Application, Request, Response, NextFunction } from 'express';
import { createServer, Server as HttpServer } from 'http';
import { WebSocketServer, WebSocket } from 'ws';
import path from 'path';
import { DeviceManager } from '../core/DeviceManager';
import { EventScheduler } from '../core/EventScheduler';
import { ConfigStore } from '../core/ConfigStore';
import { ScriptManager } from '../core/ScriptManager';
import { getDatabase } from '../core/Database';
import { createDevicesRouter } from './routes/devices';
import { createControlRouter } from './routes/control';
import { createScriptsRouter } from './routes/scripts';
import eventsRouter, { setDeviceManager as setEventsDeviceManager, setBroadcast as setEventsBroadcast } from './routes/events';
import settingsRouter from './routes/settings';
import ocrRouter, { setDeviceManager as setOcrDeviceManager, setBroadcast as setOcrBroadcast } from './routes/ocr';
import coyoteRouter from './routes/coyote';
import { createShockLogRouter } from './routes/shockLogs';
import { Logger } from '../utils/logger';

const logger = new Logger('ApiServer');

/**
 * WebSocket 客户端信息
 */
interface WSClient {
  id: string;
  ws: WebSocket;
  subscriptions: Set<string>;
}

/**
 * API 服务器类
 */
export class ApiServer {
  private app: Application;
  private httpServer: HttpServer;
  private wss: WebSocketServer;
  private deviceManager: DeviceManager;
  private scheduler: EventScheduler;
  private configStore: ConfigStore;
  private wsClients: Map<string, WSClient> = new Map();
  private clientCounter = 0;

  constructor(
    deviceManager: DeviceManager,
    scheduler: EventScheduler,
    configStore: ConfigStore
  ) {
    this.deviceManager = deviceManager;
    this.scheduler = scheduler;
    this.configStore = configStore;

    this.app = express();
    this.httpServer = createServer(this.app);
    this.wss = new WebSocketServer({ server: this.httpServer });

    this.setupMiddleware();
    this.setupRoutes();
    this.setupWebSocket();
  }

  /**
   * 检查端口是否可用
   */
  private checkPortAvailable(port: number, host: string): Promise<boolean> {
    return new Promise((resolve) => {
      const testServer = createServer();
      testServer.once('error', (err: NodeJS.ErrnoException) => {
        if (err.code === 'EADDRINUSE') {
          resolve(false);
        } else {
          resolve(false);
        }
      });
      testServer.once('listening', () => {
        testServer.close(() => resolve(true));
      });
      testServer.listen(port, host);
    });
  }

  /**
   * 查找可用端口
   */
  private async findAvailablePort(startPort: number, host: string, maxTries: number = 10): Promise<number> {
    for (let i = 0; i < maxTries; i++) {
      const port = startPort + i;
      const available = await this.checkPortAvailable(port, host);
      if (available) {
        return port;
      }
      logger.warn(`Port ${port} is in use, trying next...`);
    }
    throw new Error(`Could not find an available port after ${maxTries} attempts starting from ${startPort}`);
  }

  /**
   * 获取实际使用的端口
   */
  getPort(): number | null {
    const address = this.httpServer.address();
    if (address && typeof address === 'object') {
      return address.port;
    }
    return null;
  }

  /**
   * 启动服务器
   */
  start(): Promise<number> {
    return new Promise(async (resolve, reject) => {
      try {
        const config = this.configStore.getServerConfig();
        const availablePort = await this.findAvailablePort(config.port, config.host);
        
        if (availablePort !== config.port) {
          logger.warn(`Default port ${config.port} is in use, using port ${availablePort} instead`);
        }

        this.httpServer.listen(availablePort, config.host, () => {
          logger.info(`API server started on http://${config.host}:${availablePort}`);
          resolve(availablePort);
        });

        this.httpServer.once('error', (err: NodeJS.ErrnoException) => {
          logger.error(`Failed to start server: ${err.message}`);
          reject(err);
        });
      } catch (err) {
        reject(err);
      }
    });
  }

  /**
   * 停止服务器
   */
  async stop(): Promise<void> {
    // 关闭所有 WebSocket 连接
    for (const client of this.wsClients.values()) {
      client.ws.close();
    }
    this.wsClients.clear();

    // 关闭 HTTP 服务器
    return new Promise((resolve, reject) => {
      this.httpServer.close((err) => {
        if (err) reject(err);
        else {
          logger.info('API server stopped');
          resolve();
        }
      });
    });
  }

  /**
   * 设置中间件
   */
  private setupMiddleware(): void {
    // JSON 解析
    this.app.use(express.json());

    // 静态文件服务 - 修正路径（编译后在 dist/src/api/）
    const publicPath = path.join(__dirname, '../../../public');
    logger.debug(`Static files path: ${publicPath}`);
    this.app.use(express.static(publicPath));

    // 请求日志
    this.app.use((req: Request, res: Response, next: NextFunction) => {
      logger.debug(`${req.method} ${req.path}`);
      next();
    });

    // CORS
    this.app.use((req: Request, res: Response, next: NextFunction) => {
      res.header('Access-Control-Allow-Origin', '*');
      res.header('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
      res.header('Access-Control-Allow-Headers', 'Content-Type, Authorization');
      if (req.method === 'OPTIONS') {
        return res.sendStatus(200);
      }
      next();
    });
  }

  /**
   * 设置路由
   */
  private setupRoutes(): void {
    // 健康检查
    this.app.get('/health', (req: Request, res: Response) => {
      res.json({ status: 'ok', timestamp: new Date().toISOString() });
    });

    // API 信息
    this.app.get('/api', (req: Request, res: Response) => {
      res.json({
        name: 'Device Adapter API',
        version: '0.95.0',
        endpoints: {
          devices: '/api/devices',
          control: '/api/control',
          config: '/api/config',
          events: '/api/events',
          ocr: '/api/ocr',
          coyote: '/api/coyote',
          shockLogs: '/api/shock-logs',
        },
      });
    });

    // 设备路由
    this.app.use('/api/devices', createDevicesRouter(this.deviceManager));

    // 控制路由
    this.app.use('/api/control', createControlRouter(this.deviceManager, this.scheduler));

    // 脚本路由
    const scriptManager = new ScriptManager('./data');
    scriptManager.setDeviceManager(this.deviceManager);
    this.app.use('/api/scripts', createScriptsRouter(scriptManager));

    // 事件路由
    setEventsDeviceManager(this.deviceManager);
    setEventsBroadcast((eventType, data) => this.broadcast(eventType, data));
    this.app.use('/api/events', eventsRouter);

    // 设置路由
    this.app.use('/api/settings', settingsRouter);

    // OCR 血量识别路由
    setOcrDeviceManager(this.deviceManager);
    setOcrBroadcast((eventType, data) => this.broadcast(eventType, data));
    this.app.use('/api/ocr', ocrRouter);

    // 郊狼 WebSocket 服务器管理路由
    this.app.use('/api/coyote', coyoteRouter);

    // 电击日志路由
    this.app.use('/api/shock-logs', createShockLogRouter());

    // 配置路由
    this.app.get('/api/config', (req: Request, res: Response) => {
      // 返回非敏感配置
      const config = this.configStore.getConfig();
      res.json({
        success: true,
        data: {
          server: config.server,
          waveform: config.waveform,
          logging: config.logging,
        },
      });
    });

    // API 404 处理
    this.app.all('/api/*', (req: Request, res: Response) => {
      res.status(404).json({ success: false, error: 'API endpoint not found' });
    });

    // SPA fallback - 非 API 请求返回 index.html
    const publicPath = path.join(__dirname, '../../../public');
    this.app.get('*', (req: Request, res: Response) => {
      res.sendFile(path.join(publicPath, 'index.html'));
    });

    // 错误处理
    this.app.use((err: Error, req: Request, res: Response, next: NextFunction) => {
      logger.error('Unhandled error:', err.message);
      res.status(500).json({ success: false, error: 'Internal server error' });
    });
  }

  /**
   * 设置 WebSocket
   */
  private setupWebSocket(): void {
    this.wss.on('connection', (ws: WebSocket) => {
      const clientId = `ws_${++this.clientCounter}`;
      const client: WSClient = {
        id: clientId,
        ws,
        subscriptions: new Set(),
      };

      this.wsClients.set(clientId, client);
      logger.info(`WebSocket client connected: ${clientId}`);

      // 发送欢迎消息
      this.sendToClient(client, {
        type: 'connected',
        clientId,
        timestamp: new Date().toISOString(),
      });

      // 消息处理
      ws.on('message', (data: Buffer) => {
        this.handleWebSocketMessage(client, data.toString());
      });

      // 断开处理
      ws.on('close', () => {
        this.wsClients.delete(clientId);
        logger.info(`WebSocket client disconnected: ${clientId}`);
      });

      // 错误处理
      ws.on('error', (error: Error) => {
        logger.error(`WebSocket error (${clientId}):`, error.message);
      });
    });

    // 设置设备事件广播
    this.setupDeviceEventBroadcast();
  }

  /**
   * 处理 WebSocket 消息
   */
  private handleWebSocketMessage(client: WSClient, data: string): void {
    try {
      const message = JSON.parse(data);

      switch (message.type) {
        case 'subscribe':
          // 订阅事件
          if (message.events && Array.isArray(message.events)) {
            message.events.forEach((event: string) => client.subscriptions.add(event));
            this.sendToClient(client, {
              type: 'subscribed',
              events: Array.from(client.subscriptions),
            });
          }
          break;

        case 'unsubscribe':
          // 取消订阅
          if (message.events && Array.isArray(message.events)) {
            message.events.forEach((event: string) => client.subscriptions.delete(event));
            this.sendToClient(client, {
              type: 'unsubscribed',
              events: Array.from(client.subscriptions),
            });
          }
          break;

        case 'ping':
          // 心跳
          this.sendToClient(client, { type: 'pong', timestamp: new Date().toISOString() });
          break;

        case 'command':
          // 直接命令
          this.handleWebSocketCommand(client, message);
          break;

        default:
          this.sendToClient(client, {
            type: 'error',
            error: `Unknown message type: ${message.type}`,
          });
      }
    } catch (error) {
      this.sendToClient(client, {
        type: 'error',
        error: 'Invalid JSON message',
      });
    }
  }

  /**
   * 处理 WebSocket 命令
   */
  private async handleWebSocketCommand(client: WSClient, message: any): Promise<void> {
    try {
      const { command, params } = message;

      switch (command) {
        case 'setStrength':
          await this.deviceManager.setStrength(
            params.deviceId,
            params.channel,
            params.value,
            params.mode
          );
          this.sendToClient(client, { type: 'commandResult', command, success: true });
          break;

        case 'sendWaveform':
          await this.deviceManager.sendWaveform(params.deviceId, params.channel, params.waveform);
          this.sendToClient(client, { type: 'commandResult', command, success: true });
          break;

        case 'sendEvent':
          await this.deviceManager.sendEvent(params.deviceId, params.eventId, params.payload);
          this.sendToClient(client, { type: 'commandResult', command, success: true });
          break;

        default:
          this.sendToClient(client, {
            type: 'commandResult',
            command,
            success: false,
            error: `Unknown command: ${command}`,
          });
      }
    } catch (error) {
      this.sendToClient(client, {
        type: 'commandResult',
        command: message.command,
        success: false,
        error: (error as Error).message,
      });
    }
  }

  /**
   * 设置设备事件广播
   */
  private setupDeviceEventBroadcast(): void {
    // 监听所有设备的状态变化和强度变化
    // 这需要在 DeviceManager 中添加设备时注册
  }

  /**
   * 向客户端发送消息
   */
  private sendToClient(client: WSClient, message: any): void {
    if (client.ws.readyState === WebSocket.OPEN) {
      client.ws.send(JSON.stringify(message));
    }
  }

  /**
   * 广播消息到所有订阅的客户端
   */
  broadcast(eventType: string, data: any): void {
    const message = {
      type: 'event',
      event: eventType,
      data,
      timestamp: new Date().toISOString(),
    };

    for (const client of this.wsClients.values()) {
      if (client.subscriptions.has(eventType) || client.subscriptions.has('*')) {
        this.sendToClient(client, message);
      }
    }
  }

  /**
   * 获取 Express 应用实例
   */
  getApp(): Application {
    return this.app;
  }

  /**
   * 获取 HTTP 服务器实例
   */
  getHttpServer(): HttpServer {
    return this.httpServer;
  }
}

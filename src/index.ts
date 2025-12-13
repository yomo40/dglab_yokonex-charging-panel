/**
 * 设备适配器应用入口
 * 统一适配 DG-LAB 和 役次元 设备
 */

import { DeviceManager } from './core/DeviceManager';
import { EventScheduler } from './core/EventScheduler';
import { ConfigStore } from './core/ConfigStore';
import { getDatabase, closeDatabase } from './core/Database';
import { ApiServer } from './api/server';
import { Logger, setLogLevel } from './utils/logger';

const logger = new Logger('Main');

/**
 * 应用类
 */
class Application {
  private deviceManager: DeviceManager;
  private scheduler: EventScheduler;
  private configStore: ConfigStore;
  private apiServer: ApiServer;
  private isRunning = false;

  constructor() {
    // 初始化配置
    this.configStore = new ConfigStore();
    this.configStore.load();

    // 设置日志级别
    const loggingConfig = this.configStore.getLoggingConfig();
    setLogLevel(loggingConfig.level);

    // 初始化 SQLite 数据库
    const db = getDatabase('./data');
    logger.info(`Database initialized with ${db.getAllEvents().length} events`);

    // 初始化核心组件
    this.deviceManager = new DeviceManager();
    this.scheduler = new EventScheduler(this.deviceManager);

    // 初始化 API 服务器
    this.apiServer = new ApiServer(this.deviceManager, this.scheduler, this.configStore);
  }

  /**
   * 启动应用
   */
  async start(): Promise<void> {
    if (this.isRunning) {
      logger.warn('Application is already running');
      return;
    }

    logger.info('Starting Device Adapter Application...');

    try {
      // 启动调度器
      this.scheduler.start();

      // 启动 API 服务器（返回实际使用的端口）
      const actualPort = await this.apiServer.start();

      // 加载保存的设备配置并自动连接
      await this.loadSavedDevices();

      this.isRunning = true;
      logger.info('Application started successfully');

      // 打印使用信息（传入实际端口）
      this.printUsageInfo(actualPort);
    } catch (error) {
      logger.error('Failed to start application:', (error as Error).message);
      await this.stop();
      throw error;
    }
  }

  /**
   * 停止应用
   */
  async stop(): Promise<void> {
    logger.info('Stopping application...');

    try {
      // 停止调度器
      this.scheduler.stop();

      // 断开所有设备
      await this.deviceManager.cleanup();

      // 停止 API 服务器
      await this.apiServer.stop();

      // 保存配置
      this.configStore.save();

      // 关闭数据库
      closeDatabase();

      this.isRunning = false;
      logger.info('Application stopped');
    } catch (error) {
      logger.error('Error during shutdown:', (error as Error).message);
    }
  }

  /**
   * 加载保存的设备
   */
  private async loadSavedDevices(): Promise<void> {
    const savedDevices = this.configStore.getSavedDevices();

    for (const device of savedDevices) {
      try {
        const deviceId = await this.deviceManager.addDevice(
          device.type,
          device.config,
          device.name
        );

        if (device.autoConnect) {
          await this.deviceManager.connectDevice(deviceId);
        }

        logger.info(`Loaded saved device: ${device.name}`);
      } catch (error) {
        logger.error(`Failed to load device ${device.name}:`, (error as Error).message);
      }
    }
  }

  /**
   * 打印使用信息
   */
  private printUsageInfo(port: number): void {
    const serverConfig = this.configStore.getServerConfig();
    
    console.log('\n' + '='.repeat(60));
    console.log('  设备适配器应用已启动');
    console.log('='.repeat(60));
    console.log(`\n  API 服务地址: http://${serverConfig.host}:${port}`);
    if (port !== serverConfig.port) {
      console.log(`  (默认端口 ${serverConfig.port} 被占用，已切换到端口 ${port})`);
    }
    console.log('\n  API 端点:');
    console.log('    GET  /api              - API 信息');
    console.log('    GET  /api/devices      - 获取所有设备');
    console.log('    POST /api/devices      - 添加设备');
    console.log('    POST /api/devices/:id/connect    - 连接设备');
    console.log('    POST /api/devices/:id/disconnect - 断开设备');
    console.log('    POST /api/control/strength       - 设置强度');
    console.log('    POST /api/control/waveform       - 发送波形');
    console.log('    POST /api/control/event          - 发送事件');
    console.log('    POST /api/control/stop           - 停止所有设备');
    console.log('\n  WebSocket: ws://' + serverConfig.host + ':' + port);
    console.log('\n  支持的设备类型:');
    console.log('    - dglab   : DG-LAB 郊狼 (WebSocket 协议)');
    console.log('    - yokonex : 役次元 (腾讯 IM 协议)');
    console.log('\n' + '='.repeat(60) + '\n');
  }

  /**
   * 获取设备管理器
   */
  getDeviceManager(): DeviceManager {
    return this.deviceManager;
  }

  /**
   * 获取调度器
   */
  getScheduler(): EventScheduler {
    return this.scheduler;
  }

  /**
   * 获取配置存储
   */
  getConfigStore(): ConfigStore {
    return this.configStore;
  }

  /**
   * 获取 API 服务器
   */
  getApiServer(): ApiServer {
    return this.apiServer;
  }

  /**
   * 获取服务器实际使用的端口
   */
  getPort(): number | null {
    return this.apiServer.getPort();
  }
}

// 创建应用实例
const app = new Application();

// 导出获取端口的函数（供 Electron 使用）
export function getPort(): number | null {
  return app.getPort();
}

// 优雅退出处理
process.on('SIGINT', async () => {
  logger.info('Received SIGINT signal');
  await app.stop();
  process.exit(0);
});

process.on('SIGTERM', async () => {
  logger.info('Received SIGTERM signal');
  await app.stop();
  process.exit(0);
});

process.on('uncaughtException', (error) => {
  logger.error('Uncaught exception:', error.message);
  console.error(error);
});

process.on('unhandledRejection', (reason) => {
  logger.error('Unhandled rejection:', reason);
});

// 启动应用
app.start().catch((error) => {
  logger.error('Fatal error:', error.message);
  process.exit(1);
});

// 导出应用实例供外部使用
export { app, Application };

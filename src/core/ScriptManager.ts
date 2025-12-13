/**
 * 游戏脚本管理器
 * 提供脚本的持久化存储、加载、执行功能
 * 使用 SQLite 数据库进行持久化
 */

import { Logger } from '../utils/logger';
import { DeviceManager } from './DeviceManager';
import { Channel, StrengthMode } from '../adapters/IDeviceAdapter';
import { getDatabase, ScriptRecord } from './Database';

const logger = new Logger('ScriptManager');

/**
 * 脚本元数据
 */
export interface ScriptMetadata {
  id: string;
  name: string;
  game: string;
  description: string;
  version: string;
  author: string;
  createdAt: string;
  updatedAt: string;
  enabled: boolean;
}

/**
 * 完整脚本数据
 */
export interface GameScript extends ScriptMetadata {
  code: string;
}

/**
 * 脚本执行上下文
 */
export interface ScriptContext {
  // 设备控制 API
  device: {
    setStrength: (deviceId: string, channel: Channel, value: number, mode?: StrengthMode) => Promise<void>;
    getStrength: (deviceId: string, channel: Channel) => number;
    sendEvent: (deviceId: string, eventId: string, payload?: Record<string, unknown>) => Promise<void>;
    clearQueue: (deviceId: string, channel: Channel) => Promise<void>;
    stopAll: () => Promise<void>;
    getDevices: () => string[];
    getConnectedDevices: () => string[];
  };
  
  // 脚本工具 API  
  script: {
    log: (message: string) => void;
    warn: (message: string) => void;
    error: (message: string) => void;
    sleep: (ms: number) => Promise<void>;
    random: (min: number, max: number) => number;
    clamp: (value: number, min: number, max: number) => number;
  };
  
  // 事件系统 API
  events: {
    on: (eventName: string, callback: (data: any) => void) => void;
    off: (eventName: string, callback: (data: any) => void) => void;
    emit: (eventName: string, data: any) => void;
    // 触发系统事件
    trigger: (eventId: string, options?: { deviceId?: string; multiplier?: number }) => Promise<void>;
    // 获取所有可用事件
    getAll: () => { id: string; eventId: string; name: string; category: string; enabled: boolean }[];
    // 获取事件配置
    get: (eventId: string) => { id: string; eventId: string; name: string; action: string; value: number; channel: string } | null;
  };
  
  // 存储 API (脚本专用数据存储)
  storage: {
    get: (key: string) => any;
    set: (key: string, value: any) => void;
    delete: (key: string) => void;
    clear: () => void;
  };
}

/**
 * 脚本运行时实例
 */
interface ScriptRuntime {
  script: GameScript;
  context: ScriptContext;
  eventHandlers: Map<string, Set<(data: any) => void>>;
  storage: Map<string, any>;
  isRunning: boolean;
}

/**
 * 游戏脚本管理器
 */
export class ScriptManager {
  private scripts: Map<string, GameScript> = new Map();
  private runtimes: Map<string, ScriptRuntime> = new Map();
  private deviceManager: DeviceManager | null = null;
  
  constructor(dataDir: string = './data') {
    // 从数据库加载脚本
    this.loadScripts();
  }
  
  /**
   * 设置设备管理器引用
   */
  setDeviceManager(manager: DeviceManager): void {
    this.deviceManager = manager;
  }
  
  /**
   * 从数据库加载所有脚本到内存缓存
   */
  private loadScripts(): void {
    try {
      const db = getDatabase();
      const records = db.getAllScripts();
      
      for (const record of records) {
        const script: GameScript = {
          id: record.id,
          name: record.name,
          game: record.game,
          description: record.description,
          version: record.version,
          author: record.author,
          code: record.code,
          createdAt: record.createdAt,
          updatedAt: record.updatedAt,
          enabled: record.enabled,
        };
        this.scripts.set(script.id, script);
      }
      
      logger.info(`Loaded ${this.scripts.size} scripts`);
    } catch (error) {
      logger.error('Failed to load scripts:', (error as Error).message);
    }
  }
  
  /**
   * 添加新脚本
   */
  addScript(data: {
    name: string;
    game: string;
    description: string;
    code: string;
    author?: string;
    version?: string;
  }): GameScript {
    const db = getDatabase();
    
    // 保存到数据库
    const record = db.addScript({
      name: data.name,
      game: data.game,
      description: data.description,
      code: data.code,
      version: data.version || '1.0.0',
      author: data.author || 'Anonymous',
      enabled: true,
    });
    
    // 转换为 GameScript
    const script: GameScript = {
      id: record.id,
      name: record.name,
      game: record.game,
      description: record.description,
      version: record.version,
      author: record.author,
      code: record.code,
      createdAt: record.createdAt,
      updatedAt: record.updatedAt,
      enabled: record.enabled,
    };
    
    // 更新内存缓存
    this.scripts.set(script.id, script);
    
    logger.info(`Added script: ${script.name} (${script.id})`);
    return script;
  }
  
  /**
   * 更新脚本
   */
  updateScript(id: string, updates: Partial<Omit<GameScript, 'id' | 'createdAt'>>): GameScript | null {
    const script = this.scripts.get(id);
    if (!script) {
      logger.warn(`Script not found: ${id}`);
      return null;
    }
    
    const db = getDatabase();
    
    // 更新数据库
    const success = db.updateScript(id, updates);
    if (!success) {
      logger.error(`Failed to update script in database: ${id}`);
      return null;
    }
    
    // 重新从数据库获取更新后的记录
    const record = db.getScript(id);
    if (!record) {
      return null;
    }
    
    // 更新内存缓存
    const updated: GameScript = {
      id: record.id,
      name: record.name,
      game: record.game,
      description: record.description,
      version: record.version,
      author: record.author,
      code: record.code,
      createdAt: record.createdAt,
      updatedAt: record.updatedAt,
      enabled: record.enabled,
    };
    
    this.scripts.set(id, updated);
    
    // 如果脚本正在运行，重新加载
    if (this.runtimes.has(id)) {
      this.stopScript(id);
      if (updated.enabled) {
        this.startScript(id);
      }
    }
    
    logger.info(`Updated script: ${updated.name} (${id})`);
    return updated;
  }
  
  /**
   * 删除脚本
   */
  deleteScript(id: string): boolean {
    const script = this.scripts.get(id);
    if (!script) {
      return false;
    }
    
    // 停止运行
    this.stopScript(id);
    
    // 从数据库删除
    const db = getDatabase();
    const success = db.deleteScript(id);
    
    if (success) {
      // 从内存缓存删除
      this.scripts.delete(id);
      logger.info(`Deleted script: ${script.name} (${id})`);
    }
    
    return success;
  }
  
  /**
   * 获取脚本
   */
  getScript(id: string): GameScript | undefined {
    return this.scripts.get(id);
  }
  
  /**
   * 获取所有脚本
   */
  getAllScripts(): GameScript[] {
    return Array.from(this.scripts.values());
  }
  
  /**
   * 获取指定游戏的脚本
   */
  getScriptsByGame(game: string): GameScript[] {
    return Array.from(this.scripts.values()).filter(s => 
      s.game.toLowerCase() === game.toLowerCase()
    );
  }
  
  /**
   * 创建脚本执行上下文
   */
  private createContext(scriptId: string): ScriptContext {
    const runtime = this.runtimes.get(scriptId);
    
    return {
      device: {
        setStrength: async (deviceId, channel, value, mode = StrengthMode.Set) => {
          if (!this.deviceManager) throw new Error('DeviceManager not available');
          await this.deviceManager.setStrength(deviceId, channel, value, mode);
        },
        getStrength: (deviceId, channel) => {
          // 返回缓存的强度值
          return 0; // TODO: 实现强度缓存
        },
        sendEvent: async (deviceId, eventId, payload) => {
          if (!this.deviceManager) throw new Error('DeviceManager not available');
          await this.deviceManager.sendEvent(deviceId, eventId, payload);
        },
        clearQueue: async (deviceId, channel) => {
          if (!this.deviceManager) throw new Error('DeviceManager not available');
          await this.deviceManager.clearWaveformQueue(deviceId, channel);
        },
        stopAll: async () => {
          if (!this.deviceManager) throw new Error('DeviceManager not available');
          await this.deviceManager.disconnectAll();
        },
        getDevices: () => {
          if (!this.deviceManager) return [];
          return this.deviceManager.getAllDevices().map(d => d.id);
        },
        getConnectedDevices: () => {
          if (!this.deviceManager) return [];
          return this.deviceManager.getConnectedDevices().map(d => d.id);
        },
      },
      
      script: {
        log: (message) => logger.info(`[Script:${scriptId}] ${message}`),
        warn: (message) => logger.warn(`[Script:${scriptId}] ${message}`),
        error: (message) => logger.error(`[Script:${scriptId}] ${message}`),
        sleep: (ms) => new Promise(resolve => setTimeout(resolve, ms)),
        random: (min, max) => Math.floor(Math.random() * (max - min + 1)) + min,
        clamp: (value, min, max) => Math.min(Math.max(value, min), max),
      },
      
      events: {
        on: (eventName, callback) => {
          if (!runtime) return;
          if (!runtime.eventHandlers.has(eventName)) {
            runtime.eventHandlers.set(eventName, new Set());
          }
          runtime.eventHandlers.get(eventName)!.add(callback);
        },
        off: (eventName, callback) => {
          if (!runtime) return;
          runtime.eventHandlers.get(eventName)?.delete(callback);
        },
        emit: (eventName, data) => {
          // 广播到所有运行中的脚本
          for (const [, rt] of this.runtimes) {
            const handlers = rt.eventHandlers.get(eventName);
            if (handlers) {
              for (const handler of handlers) {
                try {
                  handler(data);
                } catch (error) {
                  logger.error(`Event handler error: ${(error as Error).message}`);
                }
              }
            }
          }
        },
        // 触发系统事件（从数据库获取配置并执行）
        trigger: async (eventId: string, options?: { deviceId?: string; multiplier?: number }) => {
          const db = getDatabase();
          const event = db.getEvent(eventId);
          if (!event || !event.enabled) {
            logger.warn(`Event not found or disabled: ${eventId}`);
            return;
          }
          
          // 确定目标设备
          const targetDevices: string[] = [];
          if (options?.deviceId) {
            targetDevices.push(options.deviceId);
          } else if (this.deviceManager) {
            // 默认发送到所有已连接设备
            targetDevices.push(...this.deviceManager.getConnectedDeviceIds());
          }
          
          if (targetDevices.length === 0) {
            logger.warn(`No target devices for event: ${eventId}`);
            return;
          }
          
          // 计算强度值
          let value = event.value || 0;
          if (options?.multiplier) {
            value = Math.round(value * options.multiplier);
          }
          
          // 执行动作
          for (const deviceId of targetDevices) {
            if (!this.deviceManager) continue;
            
            const channels: Channel[] = [];
            if (event.channel === 'A' || event.channel === 'AB') channels.push(Channel.A);
            if (event.channel === 'B' || event.channel === 'AB') channels.push(Channel.B);
            
            for (const channel of channels) {
              try {
                switch (event.action) {
                  case 'set':
                    await this.deviceManager.setStrength(deviceId, channel, value, StrengthMode.Set);
                    break;
                  case 'increase':
                    await this.deviceManager.setStrength(deviceId, channel, value, StrengthMode.Increase);
                    break;
                  case 'decrease':
                    await this.deviceManager.setStrength(deviceId, channel, value, StrengthMode.Decrease);
                    break;
                  case 'pulse':
                    await this.deviceManager.setStrength(deviceId, channel, value, StrengthMode.Set);
                    if (event.duration) {
                      const duration = event.duration;
                      setTimeout(async () => {
                        await this.deviceManager?.setStrength(deviceId, channel, 0, StrengthMode.Set);
                      }, duration);
                    }
                    break;
                }
              } catch (error) {
                logger.error(`Failed to execute event action: ${(error as Error).message}`);
              }
            }
          }
          
          logger.info(`Triggered event: ${event.name} (${eventId})`);
        },
        // 获取所有可用事件
        getAll: () => {
          const db = getDatabase();
          return db.getAllEvents().filter(e => e.enabled);
        },
        // 获取事件配置
        get: (eventId: string) => {
          const db = getDatabase();
          return db.getEvent(eventId) || null;
        },
      },
      
      storage: {
        get: (key) => runtime?.storage.get(key),
        set: (key, value) => runtime?.storage.set(key, value),
        delete: (key) => runtime?.storage.delete(key),
        clear: () => runtime?.storage.clear(),
      },
    };
  }
  
  /**
   * 启动脚本
   */
  startScript(id: string): boolean {
    const script = this.scripts.get(id);
    if (!script) {
      logger.warn(`Script not found: ${id}`);
      return false;
    }
    
    if (this.runtimes.has(id)) {
      logger.warn(`Script already running: ${id}`);
      return false;
    }
    
    try {
      // 创建运行时
      const runtime: ScriptRuntime = {
        script,
        context: null as any, // 稍后设置
        eventHandlers: new Map(),
        storage: new Map(),
        isRunning: true,
      };
      
      this.runtimes.set(id, runtime);
      runtime.context = this.createContext(id);
      
      // 执行脚本
      const scriptFunction = new Function(
        'device', 'script', 'events', 'storage', 'Channel', 'StrengthMode',
        script.code
      );
      
      scriptFunction(
        runtime.context.device,
        runtime.context.script,
        runtime.context.events,
        runtime.context.storage,
        Channel,
        StrengthMode
      );
      
      logger.info(`Started script: ${script.name} (${id})`);
      return true;
    } catch (error) {
      logger.error(`Failed to start script ${id}: ${(error as Error).message}`);
      this.runtimes.delete(id);
      return false;
    }
  }
  
  /**
   * 停止脚本
   */
  stopScript(id: string): boolean {
    const runtime = this.runtimes.get(id);
    if (!runtime) {
      return false;
    }
    
    runtime.isRunning = false;
    runtime.eventHandlers.clear();
    this.runtimes.delete(id);
    
    logger.info(`Stopped script: ${runtime.script.name} (${id})`);
    return true;
  }
  
  /**
   * 触发脚本事件
   */
  triggerEvent(eventName: string, data: any): void {
    for (const [, runtime] of this.runtimes) {
      const handlers = runtime.eventHandlers.get(eventName);
      if (handlers) {
        for (const handler of handlers) {
          try {
            handler(data);
          } catch (error) {
            logger.error(`Event handler error: ${(error as Error).message}`);
          }
        }
      }
    }
  }
  
  /**
   * 获取运行中的脚本
   */
  getRunningScripts(): string[] {
    return Array.from(this.runtimes.keys());
  }
  
  /**
   * 检查脚本是否运行中
   */
  isScriptRunning(id: string): boolean {
    return this.runtimes.has(id);
  }
  
  /**
   * 停止所有脚本
   */
  stopAllScripts(): void {
    for (const id of this.runtimes.keys()) {
      this.stopScript(id);
    }
  }
}

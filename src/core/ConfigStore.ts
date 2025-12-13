/**
 * 配置存储
 * 管理应用配置的持久化和读取
 */

import fs from 'fs';
import path from 'path';
import { Logger } from '../utils/logger';

const logger = new Logger('ConfigStore');

/**
 * 服务器配置
 */
export interface ServerConfig {
  port: number;
  host: string;
}

/**
 * DG-LAB 配置
 */
export interface DGLabConfig {
  websocketUrl: string;
  heartbeatInterval: number;
  reconnectInterval: number;
  maxReconnectAttempts: number;
}

/**
 * YOKONEX 配置
 */
export interface YokonexConfig {
  apiBase: string;
  imTimeout: number;
}

/**
 * 波形配置
 */
export interface WaveformConfig {
  defaultFrequency: number;
  defaultStrength: number;
  maxQueueLength: number;
}

/**
 * 日志配置
 */
export interface LoggingConfig {
  level: string;
  file: string;
}

/**
 * 设备保存配置
 */
export interface SavedDeviceConfig {
  id: string;
  name: string;
  type: 'dglab' | 'yokonex';
  autoConnect: boolean;
  config: Record<string, any>;
}

/**
 * 应用配置
 */
export interface AppConfig {
  server: ServerConfig;
  dglab: DGLabConfig;
  yokonex: YokonexConfig;
  waveform: WaveformConfig;
  logging: LoggingConfig;
  savedDevices?: SavedDeviceConfig[];
}

/**
 * 默认配置
 */
const DEFAULT_CONFIG: AppConfig = {
  server: {
    port: 3000,
    host: '0.0.0.0',
  },
  dglab: {
    websocketUrl: 'ws://localhost:9999',
    heartbeatInterval: 10000,
    reconnectInterval: 5000,
    maxReconnectAttempts: 10,
  },
  yokonex: {
    apiBase: 'https://suo.jiushu1234.com/api.php',
    imTimeout: 15000,
  },
  waveform: {
    defaultFrequency: 50,
    defaultStrength: 20,
    maxQueueLength: 500,
  },
  logging: {
    level: 'info',
    file: 'logs/app.log',
  },
  savedDevices: [],
};

/**
 * 配置存储类
 */
export class ConfigStore {
  private config: AppConfig;
  private configPath: string;
  private userConfigPath: string;

  constructor(configDir?: string) {
    const baseDir = configDir || path.resolve(__dirname, '../../config');
    this.configPath = path.join(baseDir, 'default.json');
    this.userConfigPath = path.join(baseDir, 'user.json');
    this.config = { ...DEFAULT_CONFIG };
  }

  /**
   * 加载配置
   */
  load(): AppConfig {
    // 加载默认配置
    this.loadFile(this.configPath);

    // 加载用户配置（覆盖默认配置）
    this.loadFile(this.userConfigPath);

    logger.info('Configuration loaded');
    return this.config;
  }

  /**
   * 加载配置文件
   */
  private loadFile(filePath: string): void {
    try {
      if (fs.existsSync(filePath)) {
        const content = fs.readFileSync(filePath, 'utf-8');
        const fileConfig = JSON.parse(content);
        this.config = this.mergeConfig(this.config, fileConfig);
        logger.debug(`Loaded config from: ${filePath}`);
      }
    } catch (error) {
      logger.warn(`Failed to load config from ${filePath}:`, (error as Error).message);
    }
  }

  /**
   * 保存用户配置
   */
  save(): void {
    try {
      const dir = path.dirname(this.userConfigPath);
      if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
      }

      fs.writeFileSync(this.userConfigPath, JSON.stringify(this.config, null, 2), 'utf-8');
      logger.info('Configuration saved');
    } catch (error) {
      logger.error('Failed to save config:', (error as Error).message);
      throw error;
    }
  }

  /**
   * 获取完整配置
   */
  getConfig(): AppConfig {
    return { ...this.config };
  }

  /**
   * 获取服务器配置
   */
  getServerConfig(): ServerConfig {
    return { ...this.config.server };
  }

  /**
   * 获取 DG-LAB 配置
   */
  getDGLabConfig(): DGLabConfig {
    return { ...this.config.dglab };
  }

  /**
   * 获取 YOKONEX 配置
   */
  getYokonexConfig(): YokonexConfig {
    return { ...this.config.yokonex };
  }

  /**
   * 获取波形配置
   */
  getWaveformConfig(): WaveformConfig {
    return { ...this.config.waveform };
  }

  /**
   * 获取日志配置
   */
  getLoggingConfig(): LoggingConfig {
    return { ...this.config.logging };
  }

  /**
   * 获取保存的设备配置
   */
  getSavedDevices(): SavedDeviceConfig[] {
    return [...(this.config.savedDevices || [])];
  }

  /**
   * 更新配置
   */
  update(partialConfig: Partial<AppConfig>): void {
    this.config = this.mergeConfig(this.config, partialConfig);
  }

  /**
   * 更新服务器配置
   */
  updateServerConfig(config: Partial<ServerConfig>): void {
    this.config.server = { ...this.config.server, ...config };
  }

  /**
   * 更新 DG-LAB 配置
   */
  updateDGLabConfig(config: Partial<DGLabConfig>): void {
    this.config.dglab = { ...this.config.dglab, ...config };
  }

  /**
   * 更新 YOKONEX 配置
   */
  updateYokonexConfig(config: Partial<YokonexConfig>): void {
    this.config.yokonex = { ...this.config.yokonex, ...config };
  }

  /**
   * 保存设备配置
   */
  saveDevice(device: SavedDeviceConfig): void {
    if (!this.config.savedDevices) {
      this.config.savedDevices = [];
    }

    const index = this.config.savedDevices.findIndex((d) => d.id === device.id);
    if (index >= 0) {
      this.config.savedDevices[index] = device;
    } else {
      this.config.savedDevices.push(device);
    }
  }

  /**
   * 移除保存的设备
   */
  removeDevice(deviceId: string): boolean {
    if (!this.config.savedDevices) return false;

    const index = this.config.savedDevices.findIndex((d) => d.id === deviceId);
    if (index >= 0) {
      this.config.savedDevices.splice(index, 1);
      return true;
    }
    return false;
  }

  /**
   * 深度合并配置
   */
  private mergeConfig<T>(target: T, source: Partial<T>): T {
    const result = { ...target };

    for (const key in source) {
      if (source.hasOwnProperty(key)) {
        const sourceValue = source[key];
        const targetValue = (result as any)[key];

        if (
          sourceValue !== null &&
          typeof sourceValue === 'object' &&
          !Array.isArray(sourceValue) &&
          targetValue !== null &&
          typeof targetValue === 'object' &&
          !Array.isArray(targetValue)
        ) {
          (result as any)[key] = this.mergeConfig(targetValue, sourceValue);
        } else {
          (result as any)[key] = sourceValue;
        }
      }
    }

    return result;
  }

  /**
   * 重置为默认配置
   */
  reset(): void {
    this.config = { ...DEFAULT_CONFIG };
    logger.info('Configuration reset to defaults');
  }
}

// 导出单例
export const configStore = new ConfigStore();

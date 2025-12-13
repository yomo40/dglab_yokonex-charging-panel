/**
 * 电击日志模块
 * 专门记录所有电击相关操作到独立日志文件
 */

import winston from 'winston';
import path from 'path';
import fs from 'fs';

const logDir = path.resolve(__dirname, '../../logs');

// 确保日志目录存在
if (!fs.existsSync(logDir)) {
  fs.mkdirSync(logDir, { recursive: true });
}

/**
 * 电击日志类型
 */
export enum ShockLogType {
  Strength = 'strength',        // 强度设置
  Waveform = 'waveform',        // 波形发送
  Event = 'event',              // 事件触发
  EventRule = 'event_rule',     // 事件规则触发
  Queue = 'queue',              // 队列操作
  Limit = 'limit',              // 强度上限设置
}

/**
 * 电击日志条目
 */
export interface ShockLogEntry {
  timestamp: string;
  type: ShockLogType;
  deviceId?: string;
  deviceName?: string;
  deviceType?: string;
  channel?: number | string;
  value?: number;
  mode?: string;
  waveform?: string;
  eventId?: string;
  eventName?: string;
  ruleName?: string;
  source?: string;           // 触发来源 (api, script, event_rule, ws)
  details?: Record<string, any>;
}

/**
 * 格式化日志条目为可读文本
 */
function formatLogEntry(timestamp: string, data: any): string {
  const { type, deviceName, channel, value, mode, eventName, ruleName, source, details } = data;
  let line = `[${timestamp}]`;
  
  switch (type) {
    case 'strength':
      line += ` [强度] ${deviceName || '广播'} CH${channel} = ${value} (${mode || 'set'})`;
      break;
    case 'waveform':
      line += ` [波形] ${deviceName || '广播'} CH${channel} - ${data.waveform || '自定义'}`;
      break;
    case 'event':
      line += ` [事件] ${eventName || data.eventId}`;
      break;
    case 'event_rule':
      line += ` [规则] ${ruleName} → ${eventName || data.eventId}`;
      if (value !== undefined) line += ` (强度: ${value})`;
      break;
    case 'queue':
      line += ` [队列] ${deviceName} CH${channel} ${details?.action || ''}`;
      break;
    case 'limit':
      line += ` [上限] ${deviceName} = ${value}`;
      break;
    default:
      line += ` [${type}] ${JSON.stringify(data)}`;
  }
  
  if (source) {
    line += ` [来源: ${source}]`;
  }
  
  return line;
}

// 创建专门的电击日志记录器
const shockWinstonLogger = winston.createLogger({
  level: 'info',
  format: winston.format.combine(
    winston.format.timestamp({ format: 'YYYY-MM-DD HH:mm:ss.SSS' }),
    winston.format.printf(({ timestamp, message, ...meta }) => {
      const logData = typeof message === 'object' ? message : { message };
      // 使用可读的文本格式
      return formatLogEntry(timestamp as string, { ...logData, ...meta });
    })
  ),
  transports: [
    // 电击日志专用文件 (txt格式，可读文本)
    new winston.transports.File({
      filename: path.join(logDir, 'shock.txt'),
      maxsize: 10 * 1024 * 1024, // 10MB
      maxFiles: 5,
      tailable: true,
    }),
    // 每日归档 (txt格式)
    new winston.transports.File({
      filename: path.join(logDir, `shock-${new Date().toISOString().split('T')[0]}.txt`),
    }),
  ],
});

// 开发环境同时输出到控制台
if (process.env.NODE_ENV !== 'production') {
  shockWinstonLogger.add(
    new winston.transports.Console({
      format: winston.format.combine(
        winston.format.colorize(),
        winston.format.printf(({ timestamp, message, ...meta }) => {
          const data = typeof message === 'object' ? message : { message };
          const merged = { ...data, ...meta } as unknown as ShockLogEntry;
          const { type, deviceName, channel, value, mode, eventName, ruleName, source } = merged;
          let logStr = `${timestamp} [SHOCK]`;
          
          if (type === ShockLogType.Strength) {
            logStr += ` 强度设置: ${deviceName || '广播'} CH${channel} = ${value} (${mode || 'set'})`;
          } else if (type === ShockLogType.Waveform) {
            logStr += ` 波形发送: ${deviceName || '广播'} CH${channel}`;
          } else if (type === ShockLogType.EventRule) {
            logStr += ` 规则触发: ${ruleName} -> ${eventName}`;
          } else if (type === ShockLogType.Event) {
            logStr += ` 事件: ${eventName || 'unknown'}`;
          } else if (type === ShockLogType.Limit) {
            logStr += ` 上限设置: ${deviceName} = ${value}`;
          } else if (type === ShockLogType.Queue) {
            logStr += ` 队列操作: ${deviceName} CH${channel}`;
          }
          
          if (source) {
            logStr += ` [来源: ${source}]`;
          }
          
          return logStr;
        })
      ),
    })
  );
}

/**
 * 电击日志记录器
 */
class ShockLogger {
  private enabled: boolean = true;
  private recentLogs: ShockLogEntry[] = [];
  private maxRecentLogs: number = 100;

  /**
   * 启用/禁用日志
   */
  setEnabled(enabled: boolean): void {
    this.enabled = enabled;
  }

  /**
   * 记录日志
   */
  private log(entry: ShockLogEntry): void {
    if (!this.enabled) return;

    entry.timestamp = new Date().toISOString();
    
    // 保存到内存（用于前端展示）
    this.recentLogs.unshift(entry);
    if (this.recentLogs.length > this.maxRecentLogs) {
      this.recentLogs = this.recentLogs.slice(0, this.maxRecentLogs);
    }

    // 写入文件
    shockWinstonLogger.info(entry);
  }

  /**
   * 记录强度设置
   */
  logStrength(params: {
    deviceId?: string;
    deviceName?: string;
    deviceType?: string;
    channel: number | string;
    value: number;
    mode?: string;
    source?: string;
  }): void {
    this.log({
      timestamp: '',
      type: ShockLogType.Strength,
      ...params,
    });
  }

  /**
   * 记录波形发送
   */
  logWaveform(params: {
    deviceId?: string;
    deviceName?: string;
    deviceType?: string;
    channel: number | string;
    waveform?: string;
    source?: string;
    details?: Record<string, any>;
  }): void {
    this.log({
      timestamp: '',
      type: ShockLogType.Waveform,
      ...params,
    });
  }

  /**
   * 记录事件触发
   */
  logEvent(params: {
    deviceId?: string;
    deviceName?: string;
    deviceType?: string;
    eventId: string;
    eventName?: string;
    source?: string;
    details?: Record<string, any>;
  }): void {
    this.log({
      timestamp: '',
      type: ShockLogType.Event,
      ...params,
    });
  }

  /**
   * 记录事件规则触发
   */
  logEventRule(params: {
    ruleName: string;
    eventId?: string;
    eventName?: string;
    channel?: number | string;
    value?: number;
    source?: string;
    details?: Record<string, any>;
  }): void {
    this.log({
      timestamp: '',
      type: ShockLogType.EventRule,
      ...params,
    });
  }

  /**
   * 记录强度上限设置
   */
  logLimit(params: {
    deviceId?: string;
    deviceName?: string;
    channel?: number | string;
    value: number;
    source?: string;
  }): void {
    this.log({
      timestamp: '',
      type: ShockLogType.Limit,
      ...params,
    });
  }

  /**
   * 记录队列操作（清空等）
   */
  logQueue(params: {
    deviceId?: string;
    deviceName?: string;
    channel: number | string;
    action: 'clear' | 'add' | 'remove';
    source?: string;
  }): void {
    this.log({
      timestamp: '',
      type: ShockLogType.Queue,
      details: { action: params.action },
      ...params,
    });
  }

  /**
   * 获取最近的日志
   */
  getRecentLogs(count?: number): ShockLogEntry[] {
    const limit = count || this.maxRecentLogs;
    return this.recentLogs.slice(0, limit);
  }

  /**
   * 清空内存中的日志
   */
  clearRecentLogs(): void {
    this.recentLogs = [];
  }

  /**
   * 获取统计信息
   */
  getStats(): {
    totalLogs: number;
    byType: Record<string, number>;
    byDevice: Record<string, number>;
  } {
    const byType: Record<string, number> = {};
    const byDevice: Record<string, number> = {};

    for (const log of this.recentLogs) {
      byType[log.type] = (byType[log.type] || 0) + 1;
      if (log.deviceName) {
        byDevice[log.deviceName] = (byDevice[log.deviceName] || 0) + 1;
      }
    }

    return {
      totalLogs: this.recentLogs.length,
      byType,
      byDevice,
    };
  }
}

// 导出单例
export const shockLogger = new ShockLogger();

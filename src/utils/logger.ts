/**
 * 日志工具类
 */

import winston from 'winston';
import path from 'path';

const logDir = path.resolve(__dirname, '../../logs');

// 创建 winston logger 实例
const winstonLogger = winston.createLogger({
  level: process.env.LOG_LEVEL || 'info',
  format: winston.format.combine(
    winston.format.timestamp({ format: 'YYYY-MM-DD HH:mm:ss' }),
    winston.format.errors({ stack: true }),
    winston.format.printf(({ timestamp, level, message, module, ...meta }) => {
      const moduleStr = module ? `[${module}]` : '';
      const metaStr = Object.keys(meta).length ? JSON.stringify(meta) : '';
      return `${timestamp} ${level.toUpperCase()} ${moduleStr} ${message} ${metaStr}`.trim();
    })
  ),
  transports: [
    new winston.transports.Console({
      format: winston.format.combine(
        winston.format.colorize(),
        winston.format.printf(({ timestamp, level, message, module, ...meta }) => {
          const moduleStr = module ? `[${module}]` : '';
          const metaStr = Object.keys(meta).length ? JSON.stringify(meta) : '';
          return `${timestamp} ${level} ${moduleStr} ${message} ${metaStr}`.trim();
        })
      ),
    }),
    new winston.transports.File({
      filename: path.join(logDir, 'error.log'),
      level: 'error',
    }),
    new winston.transports.File({
      filename: path.join(logDir, 'combined.log'),
    }),
  ],
});

/**
 * 日志类 - 模块化日志记录
 */
export class Logger {
  private module: string;

  constructor(module: string) {
    this.module = module;
  }

  private log(level: string, message: string, ...args: any[]): void {
    const formattedMessage = args.length > 0 ? `${message} ${args.map(a => 
      typeof a === 'object' ? JSON.stringify(a) : a
    ).join(' ')}` : message;

    winstonLogger.log(level, formattedMessage, { module: this.module });
  }

  debug(message: string, ...args: any[]): void {
    this.log('debug', message, ...args);
  }

  info(message: string, ...args: any[]): void {
    this.log('info', message, ...args);
  }

  warn(message: string, ...args: any[]): void {
    this.log('warn', message, ...args);
  }

  error(message: string, ...args: any[]): void {
    this.log('error', message, ...args);
  }

  success(message: string, ...args: any[]): void {
    // success 映射到 info 级别，但带有标记
    this.log('info', `✓ ${message}`, ...args);
  }
}

/**
 * 全局日志实例
 */
export const logger = new Logger('App');

/**
 * 设置日志级别
 */
export function setLogLevel(level: string): void {
  winstonLogger.level = level;
}

/**
 * 获取当前日志级别
 */
export function getLogLevel(): string {
  return winstonLogger.level;
}

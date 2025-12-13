/**
 * 电击日志路由
 * 提供电击日志查询和管理 API
 */

import { Router, Request, Response } from 'express';
import { shockLogger, ShockLogType } from '../../utils/shockLogger';
import { Logger } from '../../utils/logger';
import path from 'path';
import fs from 'fs';

const logger = new Logger('ShockLogRoute');
const logDir = path.resolve(__dirname, '../../../logs');

export function createShockLogRouter(): Router {
  const router = Router();

  /**
   * 获取最近的电击日志
   * GET /shock-logs
   * Query: { count?: number, type?: ShockLogType }
   */
  router.get('/', (req: Request, res: Response) => {
    try {
      const count = parseInt(req.query.count as string) || 100;
      const type = req.query.type as ShockLogType;
      
      let logs = shockLogger.getRecentLogs(count);
      
      // 按类型过滤
      if (type) {
        logs = logs.filter(log => log.type === type);
      }
      
      res.json({ success: true, data: logs });
    } catch (error) {
      logger.error('Failed to get shock logs:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取电击日志统计
   * GET /shock-logs/stats
   */
  router.get('/stats', (req: Request, res: Response) => {
    try {
      const stats = shockLogger.getStats();
      res.json({ success: true, data: stats });
    } catch (error) {
      logger.error('Failed to get shock log stats:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 清空内存中的电击日志
   * POST /shock-logs/clear
   */
  router.post('/clear', (req: Request, res: Response) => {
    try {
      shockLogger.clearRecentLogs();
      res.json({ success: true, message: 'Memory logs cleared' });
    } catch (error) {
      logger.error('Failed to clear shock logs:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取日志文件列表
   * GET /shock-logs/files
   */
  router.get('/files', (req: Request, res: Response) => {
    try {
      if (!fs.existsSync(logDir)) {
        return res.json({ success: true, data: [] });
      }
      
      const files = fs.readdirSync(logDir)
        .filter(f => f.startsWith('shock'))
        .map(filename => {
          const filepath = path.join(logDir, filename);
          const stats = fs.statSync(filepath);
          return {
            filename,
            size: stats.size,
            modified: stats.mtime.toISOString(),
          };
        })
        .sort((a, b) => new Date(b.modified).getTime() - new Date(a.modified).getTime());
      
      res.json({ success: true, data: files });
    } catch (error) {
      logger.error('Failed to list log files:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 下载日志文件
   * GET /shock-logs/download/:filename
   */
  router.get('/download/:filename', (req: Request, res: Response) => {
    try {
      const { filename } = req.params;
      
      // 安全检查：只允许下载 shock 开头的日志文件
      if (!filename.startsWith('shock') || filename.includes('..')) {
        return res.status(400).json({ success: false, error: 'Invalid filename' });
      }
      
      const filepath = path.join(logDir, filename);
      
      if (!fs.existsSync(filepath)) {
        return res.status(404).json({ success: false, error: 'File not found' });
      }
      
      res.download(filepath, filename);
    } catch (error) {
      logger.error('Failed to download log file:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 读取日志文件内容（最后N行）
   * GET /shock-logs/read/:filename
   * Query: { lines?: number }
   */
  router.get('/read/:filename', (req: Request, res: Response) => {
    try {
      const { filename } = req.params;
      const lines = parseInt(req.query.lines as string) || 100;
      
      // 安全检查
      if (!filename.startsWith('shock') || filename.includes('..')) {
        return res.status(400).json({ success: false, error: 'Invalid filename' });
      }
      
      const filepath = path.join(logDir, filename);
      
      if (!fs.existsSync(filepath)) {
        return res.status(404).json({ success: false, error: 'File not found' });
      }
      
      const content = fs.readFileSync(filepath, 'utf-8');
      const allLines = content.split('\n').filter(line => line.trim());
      const lastLines = allLines.slice(-lines);
      
      // 尝试解析 JSON 行
      const parsedLogs = lastLines.map(line => {
        try {
          return JSON.parse(line);
        } catch {
          return { raw: line };
        }
      });
      
      res.json({ 
        success: true, 
        data: {
          totalLines: allLines.length,
          returnedLines: parsedLogs.length,
          logs: parsedLogs,
        }
      });
    } catch (error) {
      logger.error('Failed to read log file:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  return router;
}

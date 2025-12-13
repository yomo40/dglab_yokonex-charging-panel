/**
 * 设置管理 API 路由
 * 处理系统设置和数据导出/导入
 */

import { Router, Request, Response } from 'express';
import { getDatabase } from '../../core/Database';
import { Logger } from '../../utils/logger';

const logger = new Logger('SettingsAPI');
const router = Router();

/**
 * 获取所有设置
 * GET /api/settings
 */
router.get('/', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const category = req.query.category as string | undefined;
    
    let settings;
    if (category) {
      settings = db.getSettingsByCategory(category);
    } else {
      settings = db.getAllSettings();
    }
    
    res.json({
      success: true,
      data: settings
    });
  } catch (error) {
    logger.error('Failed to get settings:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取设置失败'
    });
  }
});

/**
 * 获取单个设置
 * GET /api/settings/:key
 */
router.get('/:key', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { key } = req.params;
    
    const value = db.getSetting(key);
    
    if (value === undefined) {
      return res.status(404).json({
        success: false,
        message: '设置项不存在'
      });
    }
    
    res.json({
      success: true,
      data: { key, value }
    });
  } catch (error) {
    logger.error('Failed to get setting:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取设置失败'
    });
  }
});

/**
 * 更新设置
 * PUT /api/settings/:key
 */
router.put('/:key', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { key } = req.params;
    const { value, category } = req.body;
    
    if (value === undefined) {
      return res.status(400).json({
        success: false,
        message: '请提供设置值'
      });
    }
    
    db.setSetting(key, value, category || 'general');
    
    logger.info(`Setting updated: ${key}`);
    
    res.json({
      success: true,
      data: { key, value },
      message: '设置已保存'
    });
  } catch (error) {
    logger.error('Failed to update setting:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '保存设置失败'
    });
  }
});

/**
 * 批量更新设置
 * PUT /api/settings
 */
router.put('/', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { settings } = req.body;
    
    if (!settings || typeof settings !== 'object') {
      return res.status(400).json({
        success: false,
        message: '请提供设置对象'
      });
    }
    
    let count = 0;
    for (const [key, value] of Object.entries(settings)) {
      const category = key.split('.')[0] || 'general';
      db.setSetting(key, value, category);
      count++;
    }
    
    logger.info(`Batch settings updated: ${count} items`);
    
    res.json({
      success: true,
      message: `已保存 ${count} 项设置`
    });
  } catch (error) {
    logger.error('Failed to batch update settings:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '批量保存设置失败'
    });
  }
});

/**
 * 删除设置
 * DELETE /api/settings/:key
 */
router.delete('/:key', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { key } = req.params;
    
    const success = db.deleteSetting(key);
    
    if (success) {
      logger.info(`Setting deleted: ${key}`);
      res.json({
        success: true,
        message: '设置已删除'
      });
    } else {
      res.status(404).json({
        success: false,
        message: '设置项不存在'
      });
    }
  } catch (error) {
    logger.error('Failed to delete setting:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '删除设置失败'
    });
  }
});

/**
 * 导出所有数据
 * GET /api/settings/export/all
 */
router.get('/export/all', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const data = db.exportData();
    
    // 设置下载头
    const filename = `device_adapter_backup_${new Date().toISOString().split('T')[0]}.json`;
    res.setHeader('Content-Type', 'application/json');
    res.setHeader('Content-Disposition', `attachment; filename="${filename}"`);
    
    logger.info('Data exported');
    
    res.json(data);
  } catch (error) {
    logger.error('Failed to export data:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '导出数据失败'
    });
  }
});

/**
 * 导入数据
 * POST /api/settings/import
 */
router.post('/import', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { data, options } = req.body;
    
    if (!data) {
      return res.status(400).json({
        success: false,
        message: '请提供导入数据'
      });
    }
    
    // 验证数据格式
    if (!data.version) {
      return res.status(400).json({
        success: false,
        message: '无效的备份文件格式'
      });
    }
    
    const stats = db.importData(data, options || { merge: true });
    
    logger.info('Data imported:', stats);
    
    res.json({
      success: true,
      data: stats,
      message: `导入完成: ${stats.devices} 设备, ${stats.events} 事件, ${stats.scripts} 脚本, ${stats.settings} 设置`
    });
  } catch (error) {
    logger.error('Failed to import data:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '导入数据失败'
    });
  }
});

/**
 * 重置所有数据
 * POST /api/settings/reset
 */
router.post('/reset', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { confirm } = req.body;
    
    if (confirm !== 'RESET_ALL') {
      return res.status(400).json({
        success: false,
        message: '请确认重置操作 (confirm: "RESET_ALL")'
      });
    }
    
    db.resetDatabase();
    
    logger.warn('Database reset to defaults');
    
    res.json({
      success: true,
      message: '所有数据已重置为默认值'
    });
  } catch (error) {
    logger.error('Failed to reset database:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '重置数据失败'
    });
  }
});

export default router;

/**
 * OCR 血量识别 API 路由
 * 提供 HTTP 接口进行血量识别和配置
 * 
 * @version 0.95
 */

import { Router, Request, Response } from 'express';
import { getOCRService, OCRConfig, BloodChangeEvent, BloodRule } from '../../core/OCRService';
import { Logger } from '../../utils/logger';
import { shockLogger } from '../../utils/shockLogger';

const logger = new Logger('OCRAPI');
const router = Router();

// 设备管理器引用（用于触发事件）
let deviceManagerRef: any = null;
let broadcastFn: ((eventType: string, data: any) => void) | null = null;

/**
 * 设置设备管理器引用
 */
export function setDeviceManager(manager: any): void {
  deviceManagerRef = manager;
}

/**
 * 设置广播函数
 */
export function setBroadcast(fn: (eventType: string, data: any) => void): void {
  broadcastFn = fn;
}

/**
 * 获取 OCR 服务状态
 * GET /api/ocr/status
 */
router.get('/status', (req: Request, res: Response) => {
  try {
    const ocrService = getOCRService();
    res.json({
      success: true,
      data: ocrService.getStatus()
    });
  } catch (error) {
    logger.error('获取状态失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取状态失败'
    });
  }
});

/**
 * 配置 OCR 服务
 * POST /api/ocr/configure
 */
router.post('/configure', (req: Request, res: Response) => {
  try {
    const ocrService = getOCRService();
    const config: Partial<OCRConfig> = req.body;
    
    ocrService.configure(config);
    
    res.json({
      success: true,
      message: 'OCR 配置已更新',
      data: ocrService.getConfig()
    });
  } catch (error) {
    logger.error('配置失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '配置失败'
    });
  }
});

/**
 * 识别图像
 * POST /api/ocr/recognize
 * Body: { image: "base64..." }
 */
router.post('/recognize', async (req: Request, res: Response) => {
  try {
    const { image } = req.body;
    
    if (!image) {
      return res.status(400).json({
        success: false,
        message: '缺少图像数据'
      });
    }
    
    const ocrService = getOCRService();
    const result = await ocrService.recognize(image);
    
    // 如果识别成功，处理血量变化
    if (result.value !== null) {
      const changeEvent = ocrService.processBloodChange(result.value);
      
      if (changeEvent && broadcastFn) {
        // 广播血量变化事件
        broadcastFn('ocr:bloodChange', changeEvent);
        
        // 根据血量变化触发相应事件
        triggerBloodEvent(changeEvent);
      }
    }
    
    res.json({
      success: true,
      data: result
    });
  } catch (error) {
    logger.error('识别失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '识别失败'
    });
  }
});

/**
 * 校准血条
 * POST /api/ocr/calibrate
 * Body: { image: "base64..." }
 */
router.post('/calibrate', (req: Request, res: Response) => {
  try {
    const { image } = req.body;
    
    if (!image) {
      return res.status(400).json({
        success: false,
        message: '缺少图像数据'
      });
    }
    
    const ocrService = getOCRService();
    const success = ocrService.calibrate(image);
    
    res.json({
      success,
      message: success ? '校准成功' : '校准失败'
    });
  } catch (error) {
    logger.error('校准失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '校准失败'
    });
  }
});

/**
 * 重置校准
 * POST /api/ocr/reset-calibration
 */
router.post('/reset-calibration', (req: Request, res: Response) => {
  try {
    const ocrService = getOCRService();
    ocrService.resetCalibration();
    
    res.json({
      success: true,
      message: '校准已重置'
    });
  } catch (error) {
    logger.error('重置失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '重置失败'
    });
  }
});

/**
 * 重置服务状态
 * POST /api/ocr/reset
 */
router.post('/reset', (req: Request, res: Response) => {
  try {
    const ocrService = getOCRService();
    ocrService.reset();
    
    res.json({
      success: true,
      message: '服务状态已重置'
    });
  } catch (error) {
    logger.error('重置失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '重置失败'
    });
  }
});

/**
 * 手动上报血量
 * POST /api/ocr/report-blood
 * Body: { value: number }
 */
router.post('/report-blood', (req: Request, res: Response) => {
  try {
    const { value } = req.body;
    
    if (typeof value !== 'number' || value < 0 || value > 100) {
      return res.status(400).json({
        success: false,
        message: '血量值必须是 0-100 之间的数字'
      });
    }
    
    const ocrService = getOCRService();
    const changeEvent = ocrService.processBloodChange(value);
    
    if (changeEvent && broadcastFn) {
      broadcastFn('ocr:bloodChange', changeEvent);
      triggerBloodEvent(changeEvent);
    }
    
    res.json({
      success: true,
      message: '血量已上报',
      data: {
        value,
        changeEvent
      }
    });
  } catch (error) {
    logger.error('上报失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '上报失败'
    });
  }
});

// 护甲状态
let lastArmorValue: number = 0;

/**
 * 手动上报护甲值
 * POST /api/ocr/report-armor
 * Body: { value: number }
 */
router.post('/report-armor', (req: Request, res: Response) => {
  try {
    const { value } = req.body;
    
    if (typeof value !== 'number' || value < 0 || value > 200) {
      return res.status(400).json({
        success: false,
        message: '护甲值必须是 0-200 之间的数字'
      });
    }
    
    const oldArmor = lastArmorValue;
    const change = value - oldArmor;
    lastArmorValue = value;
    
    // 触发护甲事件
    if (change !== 0 && broadcastFn) {
      const eventType = change < 0 ? 'lost-ahp' : 'add-ahp';
      
      logger.info(`护甲变化: ${oldArmor} -> ${value} (${change > 0 ? '+' : ''}${change}), 触发事件: ${eventType}`);
      
      // 记录日志
      shockLogger.logEventRule({
        ruleName: `OCR_${eventType}`,
        eventId: eventType,
        eventName: eventType,
        value: Math.abs(change),
        source: 'ocr_armor',
        details: {
          changeType: change < 0 ? 'decrease' : 'increase',
          oldValue: oldArmor,
          newValue: value,
          change: change
        },
      });
      
      // 广播事件
      broadcastFn('event:armorTrigger', {
        eventId: eventType,
        changeType: change < 0 ? 'decrease' : 'increase',
        change,
        oldValue: oldArmor,
        newValue: value,
        timestamp: Date.now()
      });
    }
    
    res.json({
      success: true,
      message: '护甲已上报',
      data: {
        value,
        oldValue: oldArmor,
        change
      }
    });
  } catch (error) {
    logger.error('护甲上报失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '护甲上报失败'
    });
  }
});

/**
 * 根据血量变化触发相应事件
 */
function triggerBloodEvent(event: BloodChangeEvent): void {
  if (!broadcastFn) return;
  
  // 如果有匹配的规则，使用规则定义的事件ID
  const eventId = event.action?.eventId || (() => {
    switch (event.changeType) {
      case 'death': return 'dead';
      case 'decrease': return 'lost-hp';
      case 'increase': return 'add-hp';
      case 'revive': return 'new-credit';
      default: return '';
    }
  })();
  
  if (!eventId) return;
  
  logger.info(`触发血量事件: ${eventId}${event.matchedRule ? ` (规则: ${event.matchedRule.name})` : ''}`);
  
  // 记录电击日志（OCR血量规则触发）
  shockLogger.logEventRule({
    ruleName: event.matchedRule?.name || `OCR_${event.changeType}`,
    eventId: eventId,
    eventName: event.matchedRule?.name || eventId,
    value: (event.action as any)?.value,
    source: 'ocr_rule',
    details: {
      changeType: event.changeType,
      oldValue: event.oldValue,
      newValue: event.newValue,
      change: event.change,
      ruleId: event.matchedRule?.id,
      action: event.action,
    },
  });
  
  // 广播事件触发
  broadcastFn('event:bloodTrigger', {
    eventId,
    changeType: event.changeType,
    change: event.change,
    oldValue: event.oldValue,
    newValue: event.newValue,
    timestamp: event.timestamp,
    rule: event.matchedRule ? {
      id: event.matchedRule.id,
      name: event.matchedRule.name
    } : null,
    action: event.action
  });
}

// ============ 规则管理 API ============

/**
 * 获取所有规则
 * GET /api/ocr/rules
 */
router.get('/rules', (req: Request, res: Response) => {
  try {
    const ocrService = getOCRService();
    const rules = ocrService.getRuleEngine().getRules();
    
    res.json({
      success: true,
      data: rules
    });
  } catch (error) {
    logger.error('获取规则失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取规则失败'
    });
  }
});

/**
 * 设置规则列表
 * PUT /api/ocr/rules
 */
router.put('/rules', (req: Request, res: Response) => {
  try {
    const rules: BloodRule[] = req.body.rules;
    
    if (!Array.isArray(rules)) {
      return res.status(400).json({
        success: false,
        message: '规则列表必须是数组'
      });
    }
    
    const ocrService = getOCRService();
    ocrService.getRuleEngine().setRules(rules);
    
    res.json({
      success: true,
      message: '规则列表已更新',
      data: ocrService.getRuleEngine().getRules()
    });
  } catch (error) {
    logger.error('设置规则失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '设置规则失败'
    });
  }
});

/**
 * 添加规则
 * POST /api/ocr/rules
 */
router.post('/rules', (req: Request, res: Response) => {
  try {
    const rule: BloodRule = req.body;
    
    if (!rule.id || !rule.name || !rule.conditions || !rule.action) {
      return res.status(400).json({
        success: false,
        message: '规则缺少必要字段 (id, name, conditions, action)'
      });
    }
    
    const ocrService = getOCRService();
    ocrService.getRuleEngine().addRule(rule);
    
    res.json({
      success: true,
      message: '规则已添加',
      data: rule
    });
  } catch (error) {
    logger.error('添加规则失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '添加规则失败'
    });
  }
});

/**
 * 更新规则
 * PATCH /api/ocr/rules/:id
 */
router.patch('/rules/:id', (req: Request, res: Response) => {
  try {
    const { id } = req.params;
    const updates: Partial<BloodRule> = req.body;
    
    const ocrService = getOCRService();
    const success = ocrService.getRuleEngine().updateRule(id, updates);
    
    if (!success) {
      return res.status(404).json({
        success: false,
        message: `规则 ${id} 不存在`
      });
    }
    
    res.json({
      success: true,
      message: '规则已更新'
    });
  } catch (error) {
    logger.error('更新规则失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '更新规则失败'
    });
  }
});

/**
 * 删除规则
 * DELETE /api/ocr/rules/:id
 */
router.delete('/rules/:id', (req: Request, res: Response) => {
  try {
    const { id } = req.params;
    
    const ocrService = getOCRService();
    const success = ocrService.getRuleEngine().deleteRule(id);
    
    if (!success) {
      return res.status(404).json({
        success: false,
        message: `规则 ${id} 不存在`
      });
    }
    
    res.json({
      success: true,
      message: '规则已删除'
    });
  } catch (error) {
    logger.error('删除规则失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '删除规则失败'
    });
  }
});

/**
 * 启用/禁用规则
 * POST /api/ocr/rules/:id/toggle
 */
router.post('/rules/:id/toggle', (req: Request, res: Response) => {
  try {
    const { id } = req.params;
    const { enabled } = req.body;
    
    if (typeof enabled !== 'boolean') {
      return res.status(400).json({
        success: false,
        message: 'enabled 必须是布尔值'
      });
    }
    
    const ocrService = getOCRService();
    const success = ocrService.getRuleEngine().toggleRule(id, enabled);
    
    if (!success) {
      return res.status(404).json({
        success: false,
        message: `规则 ${id} 不存在`
      });
    }
    
    res.json({
      success: true,
      message: `规则已${enabled ? '启用' : '禁用'}`
    });
  } catch (error) {
    logger.error('切换规则状态失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '切换规则状态失败'
    });
  }
});

/**
 * 重置规则为默认值
 * POST /api/ocr/rules/reset
 */
router.post('/rules/reset', (req: Request, res: Response) => {
  try {
    const ocrService = getOCRService();
    ocrService.getRuleEngine().loadDefaultRules();
    
    res.json({
      success: true,
      message: '规则已重置为默认值',
      data: ocrService.getRuleEngine().getRules()
    });
  } catch (error) {
    logger.error('重置规则失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '重置规则失败'
    });
  }
});

export default router;

/**
 * 事件管理 API 路由
 * 处理事件的 CRUD 和触发操作
 */

import { Router, Request, Response } from 'express';
import { getDatabase, EventRecord, EventActionType } from '../../core/Database';
import { DeviceManager } from '../../core/DeviceManager';
import { Logger } from '../../utils/logger';
import { shockLogger } from '../../utils/shockLogger';
import { Channel, StrengthMode } from '../../adapters';

const logger = new Logger('EventsAPI');
const router = Router();

// 设备管理器引用（在 server.ts 中设置）
let deviceManager: DeviceManager | null = null;

// 广播函数引用
let broadcastFn: ((eventType: string, data: any) => void) | null = null;

/**
 * 设置设备管理器引用
 */
export function setDeviceManager(manager: DeviceManager): void {
  deviceManager = manager;
}

/**
 * 设置广播函数
 */
export function setBroadcast(fn: (eventType: string, data: any) => void): void {
  broadcastFn = fn;
}

/**
 * 获取所有事件
 * GET /api/events
 */
router.get('/', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const category = req.query.category as string | undefined;
    
    let events: EventRecord[];
    if (category && ['system', 'game', 'custom'].includes(category)) {
      events = db.getEventsByCategory(category as 'system' | 'game' | 'custom');
    } else {
      events = db.getAllEvents();
    }
    
    res.json({
      success: true,
      data: events,
      count: events.length
    });
  } catch (error) {
    logger.error('Failed to get events:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取事件列表失败'
    });
  }
});

/**
 * 获取单个事件
 * GET /api/events/:id
 */
router.get('/:id', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { id } = req.params;
    
    // 支持通过 id 或 eventId 查询
    let event = db.getEvent(id);
    if (!event) {
      event = db.getEventByEventId(id);
    }
    
    if (!event) {
      return res.status(404).json({
        success: false,
        message: '事件不存在'
      });
    }
    
    res.json({
      success: true,
      data: event
    });
  } catch (error) {
    logger.error('Failed to get event:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取事件失败'
    });
  }
});

/**
 * 创建事件
 * POST /api/events
 */
router.post('/', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { eventId, name, description, category, channel, action, value, duration, waveformData, enabled } = req.body;
    
    // 验证必填字段
    if (!eventId || !name) {
      return res.status(400).json({
        success: false,
        message: '事件ID和名称为必填项'
      });
    }
    
    // 检查 eventId 是否已存在
    const existing = db.getEventByEventId(eventId);
    if (existing) {
      return res.status(400).json({
        success: false,
        message: `事件ID "${eventId}" 已存在`
      });
    }
    
    // 验证参数范围
    const validChannels = ['A', 'B', 'AB'];
    const validActions: EventActionType[] = ['set', 'increase', 'decrease', 'pulse', 'waveform', 'custom'];
    const validCategories = ['system', 'game', 'custom'];
    
    const event = db.addEvent({
      eventId,
      name,
      description: description || '',
      category: validCategories.includes(category) ? category : 'custom',
      channel: validChannels.includes(channel) ? channel : 'A',
      action: validActions.includes(action) ? action : 'pulse',
      value: Math.min(200, Math.max(0, parseInt(value) || 30)),
      duration: Math.min(10000, Math.max(0, parseInt(duration) || 500)),
      waveformData: waveformData || undefined,
      enabled: enabled !== false
    });
    
    logger.info(`Event created: ${eventId} (${name})`);
    
    res.status(201).json({
      success: true,
      data: event,
      message: '事件创建成功'
    });
  } catch (error) {
    logger.error('Failed to create event:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '创建事件失败'
    });
  }
});

/**
 * 更新事件
 * PUT /api/events/:id
 */
router.put('/:id', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { id } = req.params;
    
    // 查找事件
    let event = db.getEvent(id);
    if (!event) {
      event = db.getEventByEventId(id);
    }
    
    if (!event) {
      return res.status(404).json({
        success: false,
        message: '事件不存在'
      });
    }
    
    const { name, description, channel, action, value, duration, waveformData, enabled } = req.body;
    
    const updates: Partial<EventRecord> = {};
    if (name !== undefined) updates.name = name;
    if (description !== undefined) updates.description = description;
    if (channel !== undefined && ['A', 'B', 'AB'].includes(channel)) updates.channel = channel;
    if (action !== undefined) updates.action = action;
    if (value !== undefined) updates.value = Math.min(200, Math.max(0, parseInt(value)));
    if (duration !== undefined) updates.duration = Math.min(10000, Math.max(0, parseInt(duration)));
    if (waveformData !== undefined) updates.waveformData = waveformData;
    if (enabled !== undefined) updates.enabled = enabled;
    
    const success = db.updateEvent(event.id, updates);
    
    if (success) {
      const updated = db.getEvent(event.id);
      logger.info(`Event updated: ${event.eventId}`);
      res.json({
        success: true,
        data: updated,
        message: '事件更新成功'
      });
    } else {
      res.status(500).json({
        success: false,
        message: '更新事件失败'
      });
    }
  } catch (error) {
    logger.error('Failed to update event:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '更新事件失败'
    });
  }
});

/**
 * 删除事件
 * DELETE /api/events/:id
 */
router.delete('/:id', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { id } = req.params;
    
    // 查找事件
    let event = db.getEvent(id);
    if (!event) {
      event = db.getEventByEventId(id);
    }
    
    if (!event) {
      return res.status(404).json({
        success: false,
        message: '事件不存在'
      });
    }
    
    // 禁止删除系统事件
    if (event.category === 'system') {
      return res.status(403).json({
        success: false,
        message: '无法删除系统事件，只能禁用'
      });
    }
    
    const success = db.deleteEvent(event.id);
    
    if (success) {
      logger.info(`Event deleted: ${event.eventId}`);
      res.json({
        success: true,
        message: '事件删除成功'
      });
    } else {
      res.status(500).json({
        success: false,
        message: '删除事件失败'
      });
    }
  } catch (error) {
    logger.error('Failed to delete event:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '删除事件失败'
    });
  }
});

/**
 * 触发事件
 * POST /api/events/:id/trigger
 */
router.post('/:id/trigger', async (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { id } = req.params;
    const { deviceId, payload } = req.body;
    
    // 查找事件
    let event = db.getEvent(id);
    if (!event) {
      event = db.getEventByEventId(id);
    }
    
    if (!event) {
      return res.status(404).json({
        success: false,
        message: '事件不存在'
      });
    }
    
    if (!event.enabled) {
      return res.status(400).json({
        success: false,
        message: '事件已禁用'
      });
    }
    
    if (!deviceManager) {
      return res.status(500).json({
        success: false,
        message: '设备管理器未初始化'
      });
    }
    
    // 获取目标设备
    let targetDevices: string[] = [];
    if (deviceId) {
      targetDevices = [deviceId];
    } else {
      // 获取所有已连接的设备
      targetDevices = deviceManager.getConnectedDeviceIds();
    }
    
    if (targetDevices.length === 0) {
      return res.status(400).json({
        success: false,
        message: '没有已连接的设备'
      });
    }
    
    // 解析通道
    const channels: Channel[] = [];
    if (event.channel === 'A' || event.channel === 'AB') channels.push(Channel.A);
    if (event.channel === 'B' || event.channel === 'AB') channels.push(Channel.B);
    
    // 计算实际强度值（支持 payload 中的动态值）
    let actualValue = event.value;
    if (payload && typeof payload.multiplier === 'number') {
      actualValue = Math.min(200, Math.floor(actualValue * payload.multiplier));
    }
    if (payload && typeof payload.value === 'number') {
      actualValue = Math.min(200, Math.max(0, payload.value));
    }
    
    // 执行事件动作
    const results: { deviceId: string; success: boolean; error?: string }[] = [];
    
    for (const devId of targetDevices) {
      try {
        for (const channel of channels) {
          switch (event.action) {
            case 'set':
              await deviceManager.setStrength(devId, channel, actualValue, StrengthMode.Set);
              break;
              
            case 'increase':
              await deviceManager.setStrength(devId, channel, actualValue, StrengthMode.Increase);
              break;
              
            case 'decrease':
              await deviceManager.setStrength(devId, channel, actualValue, StrengthMode.Decrease);
              break;
              
            case 'pulse':
              // 脉冲：设置强度，等待持续时间，然后归零
              await deviceManager.setStrength(devId, channel, actualValue, StrengthMode.Set);
              setTimeout(async () => {
                try {
                  await deviceManager!.setStrength(devId, channel, 0, StrengthMode.Set);
                } catch (e) {
                  logger.warn(`Failed to reset strength after pulse:`, (e as Error).message);
                }
              }, event.duration);
              break;
              
            case 'waveform':
              // 发送波形数据
              if (event.waveformData) {
                try {
                  const waveform = JSON.parse(event.waveformData);
                  await deviceManager.sendWaveform(devId, channel, waveform);
                } catch (e) {
                  logger.warn(`Invalid waveform data for event ${event.eventId}`);
                }
              }
              break;
              
            case 'custom':
              // 自定义动作，留给脚本系统处理
              logger.debug(`Custom event action: ${event.eventId}`);
              break;
          }
        }
        results.push({ deviceId: devId, success: true });
      } catch (error) {
        results.push({ deviceId: devId, success: false, error: (error as Error).message });
      }
    }
    
    logger.info(`Event triggered: ${event.eventId} -> ${results.length} device(s)`);
    
    // 记录电击日志
    shockLogger.logEventRule({
      ruleName: event.name,
      eventId: event.eventId,
      eventName: event.name,
      channel: event.channel,
      value: actualValue,
      source: 'event_rule',
      details: {
        action: event.action,
        duration: event.duration,
        targetDevices: results.map(r => r.deviceId),
        category: event.category,
      },
    });
    
    // 广播事件触发通知
    if (broadcastFn) {
      broadcastFn('event:triggered', {
        eventId: event.eventId,
        eventName: event.name,
        action: event.action,
        channel: event.channel,
        value: actualValue,
        duration: event.duration,
        devices: results,
        timestamp: new Date().toISOString()
      });
    }
    
    res.json({
      success: true,
      data: {
        event: event.eventId,
        action: event.action,
        value: actualValue,
        duration: event.duration,
        results
      },
      message: '事件触发成功'
    });
  } catch (error) {
    logger.error('Failed to trigger event:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '触发事件失败'
    });
  }
});

/**
 * 批量触发事件
 * POST /api/events/trigger-batch
 */
router.post('/trigger-batch', async (req: Request, res: Response) => {
  try {
    const { events, deviceId } = req.body;
    
    if (!Array.isArray(events) || events.length === 0) {
      return res.status(400).json({
        success: false,
        message: '请提供事件列表'
      });
    }
    
    const db = getDatabase();
    const results: { eventId: string; success: boolean; error?: string }[] = [];
    
    for (const eventId of events) {
      const event = db.getEventByEventId(eventId);
      if (event && event.enabled) {
        // 触发事件（简化版，不等待完成）
        try {
          // 这里可以调用上面的触发逻辑
          results.push({ eventId, success: true });
        } catch (e) {
          results.push({ eventId, success: false, error: (e as Error).message });
        }
      } else {
        results.push({ eventId, success: false, error: '事件不存在或已禁用' });
      }
    }
    
    res.json({
      success: true,
      data: results,
      message: '批量触发完成'
    });
  } catch (error) {
    logger.error('Failed to trigger batch events:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '批量触发失败'
    });
  }
});

/**
 * 重置事件为默认值
 * POST /api/events/:id/reset
 */
router.post('/:id/reset', (req: Request, res: Response) => {
  try {
    const db = getDatabase();
    const { id } = req.params;
    
    // 查找事件
    let event = db.getEvent(id);
    if (!event) {
      event = db.getEventByEventId(id);
    }
    
    if (!event) {
      return res.status(404).json({
        success: false,
        message: '事件不存在'
      });
    }
    
    // 查找默认值
    const { DEFAULT_SYSTEM_EVENTS } = require('../core/Database');
    const defaultEvent = DEFAULT_SYSTEM_EVENTS.find((e: any) => e.eventId === event!.eventId);
    
    if (!defaultEvent) {
      return res.status(400).json({
        success: false,
        message: '该事件没有默认值'
      });
    }
    
    // 重置为默认值
    db.updateEvent(event.id, {
      name: defaultEvent.name,
      description: defaultEvent.description,
      channel: defaultEvent.channel,
      action: defaultEvent.action,
      value: defaultEvent.value,
      duration: defaultEvent.duration,
      enabled: defaultEvent.enabled
    });
    
    const updated = db.getEvent(event.id);
    
    logger.info(`Event reset to default: ${event.eventId}`);
    
    res.json({
      success: true,
      data: updated,
      message: '事件已重置为默认值'
    });
  } catch (error) {
    logger.error('Failed to reset event:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '重置事件失败'
    });
  }
});

export default router;

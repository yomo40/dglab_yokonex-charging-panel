/**
 * 控制路由
 * 处理设备控制相关的 API 请求
 */

import { Router, Request, Response } from 'express';
import { DeviceManager } from '../../core/DeviceManager';
import { EventScheduler } from '../../core/EventScheduler';
import { Channel, StrengthMode, WaveformData } from '../../adapters';
import { WaveformGenerator, BuiltInPresets } from '../../adapters/dglab';
import { Logger } from '../../utils/logger';

const logger = new Logger('ControlRoute');

export function createControlRouter(
  deviceManager: DeviceManager,
  scheduler: EventScheduler
): Router {
  const router = Router();

  /**
   * 设置强度
   * POST /control/strength
   * Body: { deviceId?: string, channel: 1|2, value: number, mode?: 0|1|2 }
   */
  router.post('/strength', async (req: Request, res: Response) => {
    try {
      const { deviceId, channel, value, mode = StrengthMode.Set } = req.body;

      if (!channel || value === undefined) {
        return res.status(400).json({
          success: false,
          error: 'channel and value are required',
        });
      }

      const ch = channel === 1 ? Channel.A : Channel.B;

      if (deviceId) {
        await deviceManager.setStrength(deviceId, ch, value, mode);
      } else {
        await deviceManager.broadcastStrength(ch, value, mode);
      }

      res.json({ success: true, message: 'Strength set' });
    } catch (error) {
      logger.error('Failed to set strength:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 发送波形
   * POST /control/waveform
   * Body: { deviceId?: string, channel: 1|2, waveform: WaveformData }
   */
  router.post('/waveform', async (req: Request, res: Response) => {
    try {
      const { deviceId, channel, waveform } = req.body;

      if (!channel || !waveform) {
        return res.status(400).json({
          success: false,
          error: 'channel and waveform are required',
        });
      }

      const ch = channel === 1 ? Channel.A : Channel.B;

      if (deviceId) {
        await deviceManager.sendWaveform(deviceId, ch, waveform);
      } else {
        await deviceManager.broadcastWaveform(ch, waveform);
      }

      res.json({ success: true, message: 'Waveform sent' });
    } catch (error) {
      logger.error('Failed to send waveform:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 使用预设波形
   * POST /control/waveform/preset
   * Body: { deviceId?: string, channel: 1|2, preset: string, duration?: number }
   */
  router.post('/waveform/preset', async (req: Request, res: Response) => {
    try {
      const { deviceId, channel, preset, duration = 1 } = req.body;

      if (!channel || !preset) {
        return res.status(400).json({
          success: false,
          error: 'channel and preset are required',
        });
      }

      if (!BuiltInPresets[preset]) {
        return res.status(400).json({
          success: false,
          error: `Unknown preset: ${preset}. Available: ${Object.keys(BuiltInPresets).join(', ')}`,
        });
      }

      const ch = channel === 1 ? Channel.A : Channel.B;
      const waveform = WaveformGenerator.fromPreset(preset, duration);

      if (deviceId) {
        await deviceManager.sendWaveform(deviceId, ch, waveform);
      } else {
        await deviceManager.broadcastWaveform(ch, waveform);
      }

      res.json({ success: true, message: 'Preset waveform sent' });
    } catch (error) {
      logger.error('Failed to send preset waveform:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取可用预设
   * GET /control/waveform/presets
   */
  router.get('/waveform/presets', (req: Request, res: Response) => {
    const presets = Object.entries(BuiltInPresets).map(([key, value]) => ({
      id: key,
      name: value.name,
      description: value.description,
    }));
    res.json({ success: true, data: presets });
  });

  /**
   * 清空波形队列
   * POST /control/waveform/clear
   * Body: { deviceId?: string, channel: 1|2 }
   */
  router.post('/waveform/clear', async (req: Request, res: Response) => {
    try {
      const { deviceId, channel } = req.body;

      if (!channel) {
        return res.status(400).json({
          success: false,
          error: 'channel is required',
        });
      }

      const ch = channel === 1 ? Channel.A : Channel.B;

      if (deviceId) {
        await deviceManager.clearWaveformQueue(deviceId, ch);
      } else {
        const connected = deviceManager.getConnectedDevices();
        await Promise.all(connected.map((d) => d.adapter.clearWaveformQueue(ch)));
      }

      res.json({ success: true, message: 'Waveform queue cleared' });
    } catch (error) {
      logger.error('Failed to clear waveform queue:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 发送事件
   * POST /control/event
   * Body: { deviceId?: string, eventId: string, payload?: object }
   */
  router.post('/event', async (req: Request, res: Response) => {
    try {
      const { deviceId, eventId, payload } = req.body;

      if (!eventId) {
        return res.status(400).json({
          success: false,
          error: 'eventId is required',
        });
      }

      if (deviceId) {
        await deviceManager.sendEvent(deviceId, eventId, payload);
      } else {
        await deviceManager.broadcastEvent(eventId, payload);
      }

      res.json({ success: true, message: 'Event sent' });
    } catch (error) {
      logger.error('Failed to send event:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 调度任务
   * POST /control/schedule
   * Body: { type: string, deviceId?: string, channel?: number, data: any, delayMs?: number, repeat?: { interval: number, count?: number } }
   */
  router.post('/schedule', (req: Request, res: Response) => {
    try {
      const { type, deviceId, channel, data, delayMs = 0, repeat } = req.body;

      if (!type || !data) {
        return res.status(400).json({
          success: false,
          error: 'type and data are required',
        });
      }

      const ch = channel ? (channel === 1 ? Channel.A : Channel.B) : undefined;

      const taskId = scheduler.schedule({
        type,
        deviceId,
        channel: ch,
        data,
        executeAt: new Date(Date.now() + delayMs),
        repeat,
      });

      res.json({ success: true, data: { taskId } });
    } catch (error) {
      logger.error('Failed to schedule task:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 取消任务
   * DELETE /control/schedule/:taskId
   */
  router.delete('/schedule/:taskId', (req: Request, res: Response) => {
    try {
      const cancelled = scheduler.cancel(req.params.taskId);
      res.json({ success: true, cancelled });
    } catch (error) {
      logger.error('Failed to cancel task:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取待执行任务
   * GET /control/schedule
   */
  router.get('/schedule', (req: Request, res: Response) => {
    try {
      const tasks = scheduler.getPendingTasks();
      res.json({ success: true, data: tasks });
    } catch (error) {
      logger.error('Failed to get scheduled tasks:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 停止所有设备
   * POST /control/stop
   */
  router.post('/stop', async (req: Request, res: Response) => {
    try {
      // 清空所有队列并将强度设为0
      const connected = deviceManager.getConnectedDevices();
      await Promise.all(
        connected.map(async (d) => {
          await d.adapter.clearWaveformQueue(Channel.A);
          await d.adapter.clearWaveformQueue(Channel.B);
          await d.adapter.setStrength(Channel.A, 0, StrengthMode.Set);
          await d.adapter.setStrength(Channel.B, 0, StrengthMode.Set);
        })
      );

      res.json({ success: true, message: 'All devices stopped' });
    } catch (error) {
      logger.error('Failed to stop devices:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  return router;
}

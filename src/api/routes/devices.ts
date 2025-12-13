/**
 * 设备路由
 * 处理设备相关的 API 请求
 */

import { Router, Request, Response } from 'express';
import { DeviceManager, DeviceInfo } from '../../core/DeviceManager';
import { ConnectionConfig, DeviceStatus } from '../../adapters';
import { Logger } from '../../utils/logger';

const logger = new Logger('DevicesRoute');

export function createDevicesRouter(deviceManager: DeviceManager): Router {
  const router = Router();

  /**
   * 获取所有设备
   * GET /devices
   */
  router.get('/', (req: Request, res: Response) => {
    try {
      const devices = deviceManager.getAllDevices();
      const result = devices.map(formatDeviceInfo);
      res.json({ success: true, data: result });
    } catch (error) {
      logger.error('Failed to get devices:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取单个设备
   * GET /devices/:id
   */
  router.get('/:id', (req: Request, res: Response) => {
    try {
      const device = deviceManager.getDevice(req.params.id);
      res.json({ success: true, data: formatDeviceInfo(device) });
    } catch (error) {
      logger.error('Failed to get device:', (error as Error).message);
      res.status(404).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 添加设备
   * POST /devices
   * Body: { type: 'dglab' | 'yokonex', name?: string, config: ConnectionConfig }
   * 注: yokonex 即 役次元 设备
   */
  router.post('/', async (req: Request, res: Response) => {
    try {
      const { type, name, config } = req.body;

      if (!type || !config) {
        return res.status(400).json({
          success: false,
          error: 'type and config are required',
        });
      }

      if (type !== 'dglab' && type !== 'yokonex') {
        return res.status(400).json({
          success: false,
          error: 'type must be "dglab" or "yokonex" (役次元)',
        });
      }

      const deviceId = await deviceManager.addDevice(type, config as ConnectionConfig, name);
      const device = deviceManager.getDevice(deviceId);

      res.status(201).json({
        success: true,
        data: formatDeviceInfo(device),
      });
    } catch (error) {
      logger.error('Failed to add device:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 删除设备
   * DELETE /devices/:id
   */
  router.delete('/:id', async (req: Request, res: Response) => {
    try {
      await deviceManager.removeDevice(req.params.id);
      res.json({ success: true, message: 'Device removed' });
    } catch (error) {
      logger.error('Failed to remove device:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 连接设备
   * POST /devices/:id/connect
   */
  router.post('/:id/connect', async (req: Request, res: Response) => {
    try {
      await deviceManager.connectDevice(req.params.id);
      const device = deviceManager.getDevice(req.params.id);
      
      // 返回额外信息（如DG-LAB的clientId用于生成二维码）
      const extraInfo: Record<string, any> = {};
      if (device.type === 'dglab' && device.adapter) {
        const adapter = device.adapter as any;
        if (adapter.getClientId) {
          extraInfo.clientId = adapter.getClientId();
        }
        if (adapter.getQRCodeContent) {
          extraInfo.qrCodeContent = adapter.getQRCodeContent();
        }
      }
      
      res.json({
        success: true,
        data: {
          ...formatDeviceInfo(device),
          ...extraInfo
        },
      });
    } catch (error) {
      logger.error('Failed to connect device:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 断开设备
   * POST /devices/:id/disconnect
   */
  router.post('/:id/disconnect', async (req: Request, res: Response) => {
    try {
      await deviceManager.disconnectDevice(req.params.id);
      const device = deviceManager.getDevice(req.params.id);
      res.json({
        success: true,
        data: formatDeviceInfo(device),
      });
    } catch (error) {
      logger.error('Failed to disconnect device:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取设备状态
   * GET /devices/:id/state
   */
  router.get('/:id/state', (req: Request, res: Response) => {
    try {
      const state = deviceManager.getDeviceState(req.params.id);
      res.json({ success: true, data: state });
    } catch (error) {
      logger.error('Failed to get device state:', (error as Error).message);
      res.status(404).json({ success: false, error: (error as Error).message });
    }
  });

  /**
   * 获取已连接设备
   * GET /devices/connected
   */
  router.get('/status/connected', (req: Request, res: Response) => {
    try {
      const devices = deviceManager.getConnectedDevices();
      const result = devices.map(formatDeviceInfo);
      res.json({ success: true, data: result });
    } catch (error) {
      logger.error('Failed to get connected devices:', (error as Error).message);
      res.status(500).json({ success: false, error: (error as Error).message });
    }
  });

  return router;
}

/**
 * 格式化设备信息（隐藏敏感数据）
 */
function formatDeviceInfo(device: DeviceInfo): Record<string, any> {
  return {
    id: device.id,
    name: device.name,
    type: device.type,
    status: device.status,
    isConnected: device.status === DeviceStatus.Connected,
  };
}

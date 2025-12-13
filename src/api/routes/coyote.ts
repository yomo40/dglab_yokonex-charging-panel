/**
 * 郊狼服务器 API 路由
 * 管理内置 WebSocket 服务器
 * 
 * @version 0.95
 */

import { Router, Request, Response } from 'express';
import { getCoyoteServer, CoyoteServerConfig, CoyoteWebSocketServer, closeCoyoteServer } from '../../core/CoyoteWebSocketServer';
import { Logger } from '../../utils/logger';

const logger = new Logger('CoyoteAPI');
const router = Router();

// 服务器实例
let coyoteServer: CoyoteWebSocketServer | null = null;

/**
 * 获取服务器状态
 * GET /api/coyote/status
 */
router.get('/status', (req: Request, res: Response) => {
  try {
    if (coyoteServer) {
      res.json({
        success: true,
        data: coyoteServer.getStatus()
      });
    } else {
      res.json({
        success: true,
        data: {
          running: false,
          url: null,
          clientCount: 0,
          bindingCount: 0,
          clients: []
        }
      });
    }
  } catch (error) {
    logger.error('获取状态失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '获取状态失败'
    });
  }
});

/**
 * 启动服务器
 * POST /api/coyote/start
 * Body: { port?: number, host?: string }
 */
router.post('/start', async (req: Request, res: Response) => {
  try {
    const { port = 9999, host = '0.0.0.0' } = req.body;
    
    if (coyoteServer) {
      const status = coyoteServer.getStatus();
      if (status.running) {
        return res.json({
          success: true,
          message: '服务器已在运行',
          data: status
        });
      }
    }
    
    const config: CoyoteServerConfig = {
      port: parseInt(port),
      host
    };
    
    coyoteServer = getCoyoteServer(config);
    const url = await coyoteServer.start();
    
    logger.info(`郊狼服务器已启动: ${url}`);
    
    res.json({
      success: true,
      message: '服务器启动成功',
      data: coyoteServer.getStatus()
    });
  } catch (error) {
    logger.error('启动失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: `启动失败: ${(error as Error).message}`
    });
  }
});

/**
 * 停止服务器
 * POST /api/coyote/stop
 */
router.post('/stop', async (req: Request, res: Response) => {
  try {
    if (coyoteServer) {
      await closeCoyoteServer();
      coyoteServer = null;
    }
    
    res.json({
      success: true,
      message: '服务器已停止'
    });
  } catch (error) {
    logger.error('停止失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '停止失败'
    });
  }
});

/**
 * 生成绑定二维码内容
 * GET /api/coyote/qrcode/:clientId
 */
router.get('/qrcode/:clientId', (req: Request, res: Response) => {
  try {
    if (!coyoteServer) {
      return res.status(400).json({
        success: false,
        message: '服务器未启动'
      });
    }
    
    const { clientId } = req.params;
    const qrContent = coyoteServer.generateBindUrl(clientId);
    const publicUrl = coyoteServer.getPublicServerUrl();
    
    res.json({
      success: true,
      data: {
        clientId,
        qrContent,
        serverUrl: coyoteServer.getServerUrl(),
        publicUrl
      }
    });
  } catch (error) {
    logger.error('生成二维码失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '生成二维码失败'
    });
  }
});

/**
 * 创建第三方客户端并生成二维码
 * POST /api/coyote/create-binding
 * 用于前端创建一个第三方客户端连接，然后生成二维码供APP扫描
 */
router.post('/create-binding', async (req: Request, res: Response) => {
  try {
    if (!coyoteServer) {
      return res.status(400).json({
        success: false,
        message: '服务器未启动'
      });
    }
    
    const status = coyoteServer.getStatus();
    if (!status.running) {
      return res.status(400).json({
        success: false,
        message: '服务器未运行'
      });
    }
    
    // 获取一个可用的第三方客户端ID（类型为third的未绑定客户端）
    const availableClient = status.clients.find((c: { type: string; boundTo: string | null }) => c.type === 'third' && !c.boundTo);
    
    if (availableClient) {
      // 已有可用客户端，直接生成二维码
      const qrContent = coyoteServer.generateBindUrl(availableClient.id);
      const publicUrl = coyoteServer.getPublicServerUrl();
      
      res.json({
        success: true,
        data: {
          clientId: availableClient.id,
          qrContent,
          serverUrl: coyoteServer.getServerUrl(),
          publicUrl,
          message: '使用DG-LAB APP扫描二维码完成绑定'
        }
      });
    } else {
      // 没有可用客户端，需要前端通过WebSocket连接后获取ID
      const publicUrl = coyoteServer.getPublicServerUrl();
      
      res.json({
        success: true,
        data: {
          serverUrl: coyoteServer.getServerUrl(),
          publicUrl,
          needConnect: true,
          message: '请先通过WebSocket连接到服务器获取客户端ID'
        }
      });
    }
  } catch (error) {
    logger.error('创建绑定失败:', (error as Error).message);
    res.status(500).json({
      success: false,
      message: '创建绑定失败'
    });
  }
});

/**
 * 获取服务器URL用于设备连接
 * GET /api/coyote/url
 */
router.get('/url', (req: Request, res: Response) => {
  try {
    if (!coyoteServer) {
      return res.status(400).json({
        success: false,
        message: '服务器未启动'
      });
    }
    
    res.json({
      success: true,
      data: {
        url: coyoteServer.getServerUrl()
      }
    });
  } catch (error) {
    res.status(500).json({
      success: false,
      message: '获取URL失败'
    });
  }
});

export default router;

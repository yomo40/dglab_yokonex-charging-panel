/**
 * 脚本 API 路由
 * 提供脚本的 CRUD 和执行控制
 */

import { Router, Request, Response } from 'express';
import { ScriptManager, GameScript } from '../../core/ScriptManager';
import { Logger } from '../../utils/logger';

const logger = new Logger('ScriptsRoute');

export function createScriptsRouter(scriptManager: ScriptManager): Router {
  const router = Router();
  
  /**
   * GET /api/scripts - 获取所有脚本
   */
  router.get('/', (req: Request, res: Response) => {
    try {
      const scripts = scriptManager.getAllScripts();
      const result = scripts.map((s: GameScript) => ({
        ...s,
        running: scriptManager.isScriptRunning(s.id),
        code: undefined, // 列表不返回代码
      }));
      
      res.json({
        success: true,
        data: result,
      });
    } catch (error) {
      logger.error('Failed to get scripts:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * GET /api/scripts/:id - 获取单个脚本
   */
  router.get('/:id', (req: Request, res: Response) => {
    try {
      const script = scriptManager.getScript(req.params.id);
      if (!script) {
        return res.status(404).json({
          success: false,
          message: 'Script not found',
        });
      }
      
      res.json({
        success: true,
        data: {
          ...script,
          isRunning: scriptManager.isScriptRunning(script.id),
        },
      });
    } catch (error) {
      logger.error('Failed to get script:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * POST /api/scripts - 添加新脚本
   */
  router.post('/', (req: Request, res: Response) => {
    try {
      const { name, game, description, code, author, version } = req.body;
      
      if (!name || !game || !code) {
        return res.status(400).json({
          success: false,
          message: 'name, game, and code are required',
        });
      }
      
      const script = scriptManager.addScript({
        name,
        game,
        description: description || '',
        code,
        author,
        version,
      });
      
      res.status(201).json({
        success: true,
        data: script,
        message: 'Script added successfully',
      });
    } catch (error) {
      logger.error('Failed to add script:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * PUT /api/scripts/:id - 更新脚本
   */
  router.put('/:id', (req: Request, res: Response) => {
    try {
      const updates = req.body;
      const script = scriptManager.updateScript(req.params.id, updates);
      
      if (!script) {
        return res.status(404).json({
          success: false,
          message: 'Script not found',
        });
      }
      
      res.json({
        success: true,
        data: script,
        message: 'Script updated successfully',
      });
    } catch (error) {
      logger.error('Failed to update script:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * DELETE /api/scripts/:id - 删除脚本
   */
  router.delete('/:id', (req: Request, res: Response) => {
    try {
      const deleted = scriptManager.deleteScript(req.params.id);
      
      if (!deleted) {
        return res.status(404).json({
          success: false,
          message: 'Script not found',
        });
      }
      
      res.json({
        success: true,
        message: 'Script deleted successfully',
      });
    } catch (error) {
      logger.error('Failed to delete script:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * POST /api/scripts/:id/start - 启动脚本
   */
  router.post('/:id/start', (req: Request, res: Response) => {
    try {
      const started = scriptManager.startScript(req.params.id);
      
      if (!started) {
        return res.status(400).json({
          success: false,
          message: 'Failed to start script (not found or already running)',
        });
      }
      
      res.json({
        success: true,
        message: 'Script started successfully',
      });
    } catch (error) {
      logger.error('Failed to start script:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * POST /api/scripts/:id/stop - 停止脚本
   */
  router.post('/:id/stop', (req: Request, res: Response) => {
    try {
      const stopped = scriptManager.stopScript(req.params.id);
      
      if (!stopped) {
        return res.status(400).json({
          success: false,
          message: 'Script not running',
        });
      }
      
      res.json({
        success: true,
        message: 'Script stopped successfully',
      });
    } catch (error) {
      logger.error('Failed to stop script:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * POST /api/scripts/trigger - 触发脚本事件
   */
  router.post('/trigger', (req: Request, res: Response) => {
    try {
      const { event, data } = req.body;
      
      if (!event) {
        return res.status(400).json({
          success: false,
          message: 'event is required',
        });
      }
      
      scriptManager.triggerEvent(event, data || {});
      
      res.json({
        success: true,
        message: `Event '${event}' triggered`,
      });
    } catch (error) {
      logger.error('Failed to trigger event:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  /**
   * GET /api/scripts/running - 获取运行中的脚本
   */
  router.get('/status/running', (req: Request, res: Response) => {
    try {
      const runningIds = scriptManager.getRunningScripts();
      const scripts = runningIds.map((id: string) => {
        const script = scriptManager.getScript(id);
        return script ? { id, name: script.name, game: script.game } : null;
      }).filter(Boolean);
      
      res.json({
        success: true,
        data: scripts,
      });
    } catch (error) {
      logger.error('Failed to get running scripts:', (error as Error).message);
      res.status(500).json({
        success: false,
        message: (error as Error).message,
      });
    }
  });
  
  return router;
}

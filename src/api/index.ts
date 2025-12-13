/**
 * API 模块导出
 */

export { ApiServer } from './server';
export { createDevicesRouter } from './routes/devices';
export { createControlRouter } from './routes/control';

// v0.95 新增路由
export { default as ocrRouter } from './routes/ocr';
export { default as coyoteRouter } from './routes/coyote';

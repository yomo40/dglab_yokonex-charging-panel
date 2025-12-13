/**
 * 核心模块导出
 */

export { DeviceManager, DeviceInfo, deviceManager } from './DeviceManager';
export {
  EventScheduler,
  TaskType,
  ScheduledTask,
  TaskResult,
} from './EventScheduler';
export {
  ConfigStore,
  AppConfig,
  ServerConfig,
  DGLabConfig,
  YokonexConfig,
  WaveformConfig,
  LoggingConfig,
  SavedDeviceConfig,
  configStore,
} from './ConfigStore';
export {
  DatabaseManager,
  DeviceRecord,
  EventRecord,
  ScriptRecord,
  SettingRecord,
  EventActionType,
  DEFAULT_SYSTEM_EVENTS,
  getDatabase,
  closeDatabase,
} from './Database';
export { ScriptManager, GameScript, ScriptMetadata, ScriptContext } from './ScriptManager';

// v0.95 新增模块
export {
  CoyoteWebSocketServer,
  CoyoteServerConfig,
  getCoyoteServer,
  closeCoyoteServer,
} from './CoyoteWebSocketServer';

export {
  OCRService,
  OCRConfig,
  RecognitionArea,
  HealthBarConfig,
  RecognitionResult,
  BloodChangeEvent,
  HealthBarAnalyzer,
  getOCRService,
} from './OCRService';

/**
 * 适配器模块统一导出
 */

// 接口和基类
export {
  IDeviceAdapter,
  BaseAdapter,
  Channel,
  StrengthMode,
  DeviceStatus,
  ConnectionConfig,
  WaveformData,
  StrengthInfo,
  DeviceState,
  FeedbackCallback,
  StatusChangeCallback,
  StrengthChangeCallback,
  ErrorCallback,
} from './IDeviceAdapter';

// DG-LAB 适配器
export {
  DGLabAdapter,
  DGLabProtocol,
  DGLabMessage,
  ErrorCodes,
  FrontendProtocolConverter,
  WaveformGenerator,
  WaveformParams,
  WaveformPreset,
  BuiltInPresets,
} from './dglab';

// YOKONEX 适配器
export {
  YokonexAdapter,
  EventMapper,
  EventConfig,
  GameConfig,
  DeviceAction,
  ActionType,
} from './yokonex';

// 虚拟设备适配器（用于测试）
export { VirtualAdapter } from './VirtualAdapter';

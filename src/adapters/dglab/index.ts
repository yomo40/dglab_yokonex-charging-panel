/**
 * DG-LAB 适配器模块导出
 */

export { DGLabAdapter } from './DGLabAdapter';
export { DGLabProtocol, DGLabMessage, ErrorCodes, FrontendProtocolConverter } from './protocol';
export { WaveformGenerator, WaveformParams, WaveformPreset, BuiltInPresets } from './waveform';

// 蓝牙协议相关导出
export {
  DGLabBluetoothProtocol,
  DGLabBluetoothAdapterBase,
  BluetoothConnection,
  BluetoothDevice,
  BluetoothConnectionState,
  StrengthParsingMode,
  ChannelWaveform,
  B0CommandData,
  BFCommandData,
  B1Response,
  BLE_SERVICE_UUID,
  BLE_WRITE_CHARACTERISTIC,
  BLE_NOTIFY_CHARACTERISTIC,
  BLE_BATTERY_SERVICE,
  BLE_BATTERY_CHARACTERISTIC,
  DEVICE_NAME_PREFIX_V3,
  DEVICE_NAME_PREFIX_SENSOR,
} from './bluetooth';

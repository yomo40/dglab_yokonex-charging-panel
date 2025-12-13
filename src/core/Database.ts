/**
 * JSON 文件数据库管理器
 * 使用 JSON 文件替代 SQLite，避免原生模块编译问题
 */

import path from 'path';
import fs from 'fs';
import { Logger } from '../utils/logger';

const logger = new Logger('Database');

/**
 * 设备记录
 */
export interface DeviceRecord {
  id: string;
  name: string;
  type: 'dglab' | 'yokonex';
  config: string;
  autoConnect: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * 事件动作类型
 */
export type EventActionType = 'set' | 'increase' | 'decrease' | 'pulse' | 'waveform' | 'custom';

/**
 * 事件记录
 */
export interface EventRecord {
  id: string;
  eventId: string;
  name: string;
  description: string;
  category: 'system' | 'game' | 'custom';
  channel: 'A' | 'B' | 'AB';
  action: EventActionType;
  value: number;
  duration: number;
  waveformData?: string;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * 脚本记录
 */
export interface ScriptRecord {
  id: string;
  name: string;
  game: string;
  description: string;
  version: string;
  author: string;
  code: string;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * 设置记录
 */
export interface SettingRecord {
  key: string;
  value: string;
  category: string;
  updatedAt: string;
}

/**
 * 数据库数据结构
 */
interface DatabaseData {
  devices: DeviceRecord[];
  events: EventRecord[];
  scripts: ScriptRecord[];
  settings: SettingRecord[];
}

/**
 * 默认系统事件
 */
export const DEFAULT_SYSTEM_EVENTS: Omit<EventRecord, 'id' | 'createdAt' | 'updatedAt'>[] = [
  { eventId: 'lost-ahp', name: '护甲血量减少', description: '角色护甲值减少时触发', category: 'system', channel: 'A', action: 'pulse', value: 30, duration: 300, enabled: true },
  { eventId: 'lost-hp', name: '角色血量减少', description: '角色生命值减少时触发', category: 'system', channel: 'A', action: 'pulse', value: 50, duration: 400, enabled: true },
  { eventId: 'add-ahp', name: '护甲血量恢复', description: '角色护甲值恢复时触发', category: 'system', channel: 'B', action: 'pulse', value: 10, duration: 200, enabled: true },
  { eventId: 'add-hp', name: '角色血量恢复', description: '角色生命值恢复时触发', category: 'system', channel: 'B', action: 'pulse', value: 15, duration: 300, enabled: true },
  { eventId: 'character-debuff', name: '角色Debuff', description: '角色受到负面状态时触发', category: 'system', channel: 'AB', action: 'pulse', value: 25, duration: 500, enabled: true },
  { eventId: 'query', name: '血量状态同步', description: '用于UI显示和状态监控', category: 'system', channel: 'A', action: 'custom', value: 0, duration: 0, enabled: true },
  { eventId: 'dead', name: '角色死亡', description: '角色死亡时触发', category: 'system', channel: 'AB', action: 'pulse', value: 100, duration: 1500, enabled: true },
  { eventId: 'new-credit', name: '关卡重置', description: '新关卡开始或角色复活时触发', category: 'system', channel: 'AB', action: 'set', value: 0, duration: 0, enabled: true },
];

/**
 * 数据库管理器类
 */
export class DatabaseManager {
  private data: DatabaseData;
  private dbPath: string;
  private saveTimeout: NodeJS.Timeout | null = null;
  
  constructor(dataDir: string = './data') {
    if (!fs.existsSync(dataDir)) {
      fs.mkdirSync(dataDir, { recursive: true });
    }
    
    this.dbPath = path.join(dataDir, 'device_adapter.json');
    this.data = this.loadData();
    
    logger.info(`Database initialized: ${this.dbPath}`);
  }
  
  private loadData(): DatabaseData {
    try {
      if (fs.existsSync(this.dbPath)) {
        const content = fs.readFileSync(this.dbPath, 'utf-8');
        return JSON.parse(content);
      }
    } catch (e) {
      logger.warn('Failed to load database, creating new one');
    }
    
    const data: DatabaseData = { devices: [], events: [], scripts: [], settings: [] };
    const now = new Date().toISOString();
    
    for (const event of DEFAULT_SYSTEM_EVENTS) {
      data.events.push({ id: `evt_${event.eventId}`, ...event, createdAt: now, updatedAt: now });
    }
    
    const defaultSettings = [
      { key: 'server.port', value: '3000', category: 'server' },
      { key: 'server.host', value: '"0.0.0.0"', category: 'server' },
      { key: 'safety.autoStop', value: 'true', category: 'safety' },
      { key: 'safety.defaultLimit', value: '100', category: 'safety' },
      { key: 'safety.maxStrength', value: '200', category: 'safety' },
    ];
    for (const s of defaultSettings) {
      data.settings.push({ ...s, updatedAt: now });
    }
    
    this.saveDataSync(data);
    return data;
  }
  
  private saveData(): void {
    if (this.saveTimeout) clearTimeout(this.saveTimeout);
    this.saveTimeout = setTimeout(() => this.saveDataSync(this.data), 100);
  }
  
  private saveDataSync(data: DatabaseData): void {
    try {
      fs.writeFileSync(this.dbPath, JSON.stringify(data, null, 2), 'utf-8');
    } catch (e) {
      logger.error('Failed to save database:', e);
    }
  }
  
  // ==================== 设备操作 ====================
  
  getAllDevices(): DeviceRecord[] { return [...this.data.devices]; }
  
  getDevice(id: string): DeviceRecord | undefined {
    return this.data.devices.find(d => d.id === id);
  }
  
  addDevice(device: Omit<DeviceRecord, 'createdAt' | 'updatedAt'>): DeviceRecord {
    const now = new Date().toISOString();
    const record: DeviceRecord = { ...device, createdAt: now, updatedAt: now };
    this.data.devices.push(record);
    this.saveData();
    return record;
  }
  
  updateDevice(id: string, updates: Partial<DeviceRecord>): boolean {
    const idx = this.data.devices.findIndex(d => d.id === id);
    if (idx === -1) return false;
    this.data.devices[idx] = { ...this.data.devices[idx], ...updates, updatedAt: new Date().toISOString() };
    this.saveData();
    return true;
  }
  
  deleteDevice(id: string): boolean {
    const idx = this.data.devices.findIndex(d => d.id === id);
    if (idx === -1) return false;
    this.data.devices.splice(idx, 1);
    this.saveData();
    return true;
  }
  
  // ==================== 事件操作 ====================
  
  getAllEvents(): EventRecord[] { return [...this.data.events]; }
  
  getEventsByCategory(category: 'system' | 'game' | 'custom'): EventRecord[] {
    return this.data.events.filter(e => e.category === category);
  }
  
  getEventByEventId(eventId: string): EventRecord | undefined {
    return this.data.events.find(e => e.eventId === eventId);
  }
  
  getEvent(id: string): EventRecord | undefined {
    return this.data.events.find(e => e.id === id);
  }
  
  addEvent(event: Omit<EventRecord, 'id' | 'createdAt' | 'updatedAt'>): EventRecord {
    const now = new Date().toISOString();
    const id = `evt_${Date.now()}_${Math.random().toString(36).substr(2, 6)}`;
    const record: EventRecord = { id, ...event, createdAt: now, updatedAt: now };
    this.data.events.push(record);
    this.saveData();
    return record;
  }
  
  updateEvent(id: string, updates: Partial<EventRecord>): boolean {
    const idx = this.data.events.findIndex(e => e.id === id);
    if (idx === -1) return false;
    this.data.events[idx] = { ...this.data.events[idx], ...updates, updatedAt: new Date().toISOString() };
    this.saveData();
    return true;
  }
  
  deleteEvent(id: string): boolean {
    const idx = this.data.events.findIndex(e => e.id === id);
    if (idx === -1) return false;
    this.data.events.splice(idx, 1);
    this.saveData();
    return true;
  }
  
  deleteEventByEventId(eventId: string): boolean {
    const idx = this.data.events.findIndex(e => e.eventId === eventId);
    if (idx === -1) return false;
    this.data.events.splice(idx, 1);
    this.saveData();
    return true;
  }
  
  // ==================== 脚本操作 ====================
  
  getAllScripts(): ScriptRecord[] { return [...this.data.scripts]; }
  
  getScript(id: string): ScriptRecord | undefined {
    return this.data.scripts.find(s => s.id === id);
  }
  
  addScript(script: Omit<ScriptRecord, 'id' | 'createdAt' | 'updatedAt'>): ScriptRecord {
    const now = new Date().toISOString();
    const id = `script_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
    const record: ScriptRecord = { id, ...script, createdAt: now, updatedAt: now };
    this.data.scripts.push(record);
    this.saveData();
    return record;
  }
  
  updateScript(id: string, updates: Partial<ScriptRecord>): boolean {
    const idx = this.data.scripts.findIndex(s => s.id === id);
    if (idx === -1) return false;
    this.data.scripts[idx] = { ...this.data.scripts[idx], ...updates, updatedAt: new Date().toISOString() };
    this.saveData();
    return true;
  }
  
  deleteScript(id: string): boolean {
    const idx = this.data.scripts.findIndex(s => s.id === id);
    if (idx === -1) return false;
    this.data.scripts.splice(idx, 1);
    this.saveData();
    return true;
  }
  
  // ==================== 设置操作 ====================
  
  getAllSettings(): SettingRecord[] { return [...this.data.settings]; }
  
  getSetting(key: string): any {
    const setting = this.data.settings.find(s => s.key === key);
    if (!setting) return undefined;
    try { return JSON.parse(setting.value); } catch { return setting.value; }
  }
  
  setSetting(key: string, value: any, category: string = 'general'): void {
    const valueStr = typeof value === 'string' ? value : JSON.stringify(value);
    const now = new Date().toISOString();
    const idx = this.data.settings.findIndex(s => s.key === key);
    if (idx >= 0) {
      this.data.settings[idx] = { key, value: valueStr, category, updatedAt: now };
    } else {
      this.data.settings.push({ key, value: valueStr, category, updatedAt: now });
    }
    this.saveData();
  }
  
  deleteSetting(key: string): boolean {
    const idx = this.data.settings.findIndex(s => s.key === key);
    if (idx === -1) return false;
    this.data.settings.splice(idx, 1);
    this.saveData();
    return true;
  }
  
  getSettingsByCategory(category: string): Record<string, any> {
    const result: Record<string, any> = {};
    for (const s of this.data.settings.filter(s => s.category === category)) {
      const shortKey = s.key.replace(`${category}.`, '');
      try { result[shortKey] = JSON.parse(s.value); } catch { result[shortKey] = s.value; }
    }
    return result;
  }
  
  // ==================== 数据导出/导入 ====================
  
  exportData() {
    return { ...this.data, exportedAt: new Date().toISOString(), version: '1.0.0' };
  }
  
  importData(data: Partial<DatabaseData>, options: { merge?: boolean; overwrite?: boolean } = {}) {
    const stats = { devices: 0, events: 0, scripts: 0, settings: 0 };
    
    if (options.overwrite) {
      this.data = { devices: [], events: [], scripts: [], settings: [] };
      const now = new Date().toISOString();
      for (const event of DEFAULT_SYSTEM_EVENTS) {
        this.data.events.push({ id: `evt_${event.eventId}`, ...event, createdAt: now, updatedAt: now });
      }
    }
    
    if (data.devices) {
      for (const d of data.devices) {
        const existing = this.getDevice(d.id);
        if (existing && options.merge) this.updateDevice(d.id, d);
        else if (!existing) this.data.devices.push(d);
        stats.devices++;
      }
    }
    
    if (data.events) {
      for (const e of data.events) {
        const existing = this.getEventByEventId(e.eventId);
        if (existing) this.updateEvent(existing.id, e);
        else this.data.events.push(e);
        stats.events++;
      }
    }
    
    if (data.scripts) {
      for (const s of data.scripts) {
        const existing = this.getScript(s.id);
        if (existing && options.merge) this.updateScript(s.id, s);
        else if (!existing) this.data.scripts.push(s);
        stats.scripts++;
      }
    }
    
    if (data.settings) {
      for (const s of data.settings) {
        this.setSetting(s.key, s.value, s.category);
        stats.settings++;
      }
    }
    
    this.saveData();
    return stats;
  }
  
  resetDatabase(): void {
    this.data = { devices: [], events: [], scripts: [], settings: [] };
    const now = new Date().toISOString();
    for (const event of DEFAULT_SYSTEM_EVENTS) {
      this.data.events.push({ id: `evt_${event.eventId}`, ...event, createdAt: now, updatedAt: now });
    }
    this.saveData();
    logger.info('Database reset to defaults');
  }
  
  close(): void {
    if (this.saveTimeout) {
      clearTimeout(this.saveTimeout);
      this.saveDataSync(this.data);
    }
    logger.info('Database connection closed');
  }
}

// 导出单例
let dbInstance: DatabaseManager | null = null;

export function getDatabase(dataDir?: string): DatabaseManager {
  if (!dbInstance) dbInstance = new DatabaseManager(dataDir);
  return dbInstance;
}

export function closeDatabase(): void {
  if (dbInstance) { dbInstance.close(); dbInstance = null; }
}

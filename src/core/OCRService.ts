/**
 * OCR 血量识别服务
 * 基于役次元开源的血量识别项目
 * 
 * 功能：
 * - 数字血量OCR识别
 * - 血条颜色识别  
 * - 截图区域分析
 * - 血量变化事件触发
 * - 规则引擎：根据血量变化触发不同设备动作
 * 
 * @version 0.95
 */

import { Logger } from '../utils/logger';
import { EventEmitter } from 'events';

const logger = new Logger('OCRService');

/**
 * Node.js 环境的 ImageData 接口定义
 */
interface ImageData {
  data: Uint8ClampedArray;
  width: number;
  height: number;
  colorSpace?: string;
}

/**
 * 血条颜色配置
 */
interface ColorProfile {
  r: [number, number];
  g: [number, number];
  b: [number, number];
}

/**
 * 识别区域配置
 */
export interface RecognitionArea {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * 血条分析配置
 */
export interface HealthBarConfig {
  color: 'auto' | 'red' | 'green' | 'yellow' | 'blue' | 'orange';
  tolerance: number;        // 颜色容差 0-255
  sampleRows: number;       // 采样行数 1-5
  minMatchRate: number;     // 最小匹配率
  edgeDetection: boolean;   // 边缘检测
}

/**
 * OCR 配置
 */
export interface OCRConfig {
  mode: 'auto' | 'digital' | 'healthbar';
  interval: number;         // 识别间隔（毫秒）
  area: RecognitionArea;
  initialBlood: number;
  healthbar?: HealthBarConfig;
  // 护甲配置（可选）
  armor?: {
    enabled: boolean;
    area: RecognitionArea;
    initialArmor: number;
    healthbar?: HealthBarConfig;
  };
}

/**
 * 识别结果
 */
export interface RecognitionResult {
  value: number | null;
  confidence: number;
  mode: string;
  timestamp: Date;
}

/**
 * 血量变化事件
 */
export interface BloodChangeEvent {
  oldValue: number;
  newValue: number;
  change: number;
  changeType: 'decrease' | 'increase' | 'death' | 'revive';
  timestamp: Date;
  matchedRule?: BloodRule;  // 匹配到的规则
  action?: RuleAction;      // 要执行的动作
  isArmor?: boolean;        // 是否是护甲变化事件
}

/**
 * 规则条件类型
 */
export type RuleConditionType = 'death' | 'decrease' | 'increase' | 'revive' | 'threshold';

/**
 * 规则条件
 */
export interface RuleCondition {
  type: RuleConditionType;
  minChange?: number;       // 最小变化量（百分比）
  maxChange?: number;       // 最大变化量（百分比）
  threshold?: number;       // 阈值（用于 threshold 类型）
  direction?: 'below' | 'above';  // 阈值方向
}

/**
 * 规则动作
 */
export interface RuleAction {
  eventId: string;          // 触发的事件 ID
  strength?: number;        // 强度值 (0-200)
  duration?: number;        // 持续时间（毫秒）
  channel?: 'A' | 'B' | 'AB';  // 目标通道
}

/**
 * 血量变化规则
 */
export interface BloodRule {
  id: string;
  name: string;
  enabled: boolean;
  priority: number;         // 优先级，数字越小优先级越高
  conditions: RuleCondition;
  action: RuleAction;
}

/**
 * 预定义颜色配置
 */
const COLOR_PROFILES: Record<string, ColorProfile> = {
  red: { r: [150, 255], g: [0, 100], b: [0, 100] },
  green: { r: [0, 100], g: [150, 255], b: [0, 100] },
  yellow: { r: [180, 255], g: [180, 255], b: [0, 100] },
  blue: { r: [0, 100], g: [0, 150], b: [150, 255] },
  orange: { r: [200, 255], g: [100, 180], b: [0, 80] }
};

/**
 * 默认规则列表
 */
const DEFAULT_RULES: BloodRule[] = [
  {
    id: 'rule_death',
    name: '角色死亡',
    enabled: true,
    priority: 1,
    conditions: { type: 'death' },
    action: { eventId: 'dead', strength: 100, duration: 2000, channel: 'AB' }
  },
  {
    id: 'rule_large_decrease',
    name: '大量掉血 (≥30%)',
    enabled: true,
    priority: 2,
    conditions: { type: 'decrease', minChange: 30 },
    action: { eventId: 'lost-hp', strength: 80, duration: 1000, channel: 'A' }
  },
  {
    id: 'rule_medium_decrease',
    name: '中量掉血 (10-30%)',
    enabled: true,
    priority: 3,
    conditions: { type: 'decrease', minChange: 10, maxChange: 30 },
    action: { eventId: 'lost-hp', strength: 50, duration: 500, channel: 'A' }
  },
  {
    id: 'rule_small_decrease',
    name: '少量掉血 (1-10%)',
    enabled: true,
    priority: 4,
    conditions: { type: 'decrease', minChange: 1, maxChange: 10 },
    action: { eventId: 'lost-hp', strength: 30, duration: 300, channel: 'A' }
  },
  {
    id: 'rule_increase',
    name: '血量恢复',
    enabled: false,
    priority: 5,
    conditions: { type: 'increase', minChange: 5 },
    action: { eventId: 'add-hp', strength: 20, duration: 200, channel: 'B' }
  },
  {
    id: 'rule_low_health',
    name: '低血量警告 (<20%)',
    enabled: false,
    priority: 6,
    conditions: { type: 'threshold', threshold: 20, direction: 'below' },
    action: { eventId: 'character-debuff', strength: 40, duration: 500, channel: 'B' }
  }
];

/**
 * 血条分析器
 * 使用多点找色法识别血条
 */
export class HealthBarAnalyzer {
  private config: HealthBarConfig;
  private fullBarLength: number | null = null;
  private isCalibrated = false;

  constructor(config?: Partial<HealthBarConfig>) {
    this.config = {
      color: 'auto',
      tolerance: 30,
      sampleRows: 3,
      minMatchRate: 0.3,
      edgeDetection: true,
      ...config
    };
  }

  /**
   * 设置配置
   */
  setConfig(config: Partial<HealthBarConfig>): void {
    this.config = { ...this.config, ...config };
  }

  /**
   * 校准血条（满血状态下调用）
   */
  calibrate(imageData: ImageData): boolean {
    try {
      const targetColor = this.getTargetColor(imageData);
      const barInfo = this.multiPointColorSearch(imageData, targetColor);

      if (!barInfo || barInfo.fillLength < 5) {
        logger.error('校准失败：血条太短或未找到');
        return false;
      }

      this.fullBarLength = barInfo.fillLength;
      this.isCalibrated = true;
      logger.info(`血条校准成功，基准长度: ${this.fullBarLength}px`);
      return true;
    } catch (err) {
      logger.error('血条校准失败:', (err as Error).message);
      return false;
    }
  }

  /**
   * 重置校准
   */
  resetCalibration(): void {
    this.fullBarLength = null;
    this.isCalibrated = false;
    logger.info('血条校准已重置');
  }

  /**
   * 分析血条
   */
  analyze(imageData: ImageData): number | null {
    try {
      const targetColor = this.getTargetColor(imageData);
      const barInfo = this.multiPointColorSearch(imageData, targetColor);

      if (!barInfo) {
        // 如果已校准但找不到血条，可能是血量为0
        if (this.isCalibrated) {
          return 0;
        }
        return null;
      }

      // 自动校准
      if (!this.isCalibrated) {
        this.fullBarLength = barInfo.fillLength;
        this.isCalibrated = true;
        logger.info(`自动校准，基准长度: ${this.fullBarLength}px`);
        return 100;
      }

      const percentage = (barInfo.fillLength / this.fullBarLength!) * 100;
      return Math.max(0, Math.min(100, Math.round(percentage)));
    } catch (err) {
      logger.error('血条分析失败:', (err as Error).message);
      return null;
    }
  }

  /**
   * 获取目标颜色
   */
  private getTargetColor(imageData: ImageData): ColorProfile {
    if (this.config.color !== 'auto' && COLOR_PROFILES[this.config.color]) {
      return COLOR_PROFILES[this.config.color];
    }
    return this.detectDominantColor(imageData);
  }

  /**
   * 检测主导颜色
   */
  private detectDominantColor(imageData: ImageData): ColorProfile {
    const data = imageData.data;
    const colorCounts: Record<string, number> = {};

    // 遍历像素，统计颜色
    for (let i = 0; i < data.length; i += 4) {
      const r = data[i];
      const g = data[i + 1];
      const b = data[i + 2];

      // 忽略接近黑色或白色的像素
      if ((r + g + b) < 60 || (r + g + b) > 700) continue;

      for (const [colorName, profile] of Object.entries(COLOR_PROFILES)) {
        if (this.matchColor(r, g, b, profile, this.config.tolerance)) {
          colorCounts[colorName] = (colorCounts[colorName] || 0) + 1;
        }
      }
    }

    // 找出最多的颜色
    let maxColor = 'red';
    let maxCount = 0;
    for (const [color, count] of Object.entries(colorCounts)) {
      if (count > maxCount) {
        maxCount = count;
        maxColor = color;
      }
    }

    return COLOR_PROFILES[maxColor];
  }

  /**
   * 多点找色搜索
   */
  private multiPointColorSearch(imageData: ImageData, targetColor: ColorProfile): { startX: number; endX: number; fillLength: number } | null {
    const { width, height, data } = imageData;
    const sampleRows = Math.min(this.config.sampleRows, height);
    const rowIndices: number[] = [];

    // 计算采样行
    for (let i = 0; i < sampleRows; i++) {
      const y = Math.floor((i + 1) * height / (sampleRows + 1));
      rowIndices.push(y);
    }

    const rowResults: { start: number; end: number }[] = [];

    // 对每行进行扫描
    for (const y of rowIndices) {
      let start = -1;
      let end = -1;

      for (let x = 0; x < width; x++) {
        const idx = (y * width + x) * 4;
        const r = data[idx];
        const g = data[idx + 1];
        const b = data[idx + 2];

        if (this.matchColor(r, g, b, targetColor, this.config.tolerance)) {
          if (start === -1) start = x;
          end = x;
        }
      }

      if (start !== -1 && end !== -1) {
        rowResults.push({ start, end });
      }
    }

    if (rowResults.length === 0) return null;

    // 计算平均值
    const avgStart = Math.round(rowResults.reduce((sum, r) => sum + r.start, 0) / rowResults.length);
    const avgEnd = Math.round(rowResults.reduce((sum, r) => sum + r.end, 0) / rowResults.length);
    const fillLength = avgEnd - avgStart + 1;

    if (fillLength < 1) return null;

    return { startX: avgStart, endX: avgEnd, fillLength };
  }

  /**
   * 颜色匹配
   */
  private matchColor(r: number, g: number, b: number, profile: ColorProfile, tolerance: number): boolean {
    return r >= profile.r[0] - tolerance && r <= profile.r[1] + tolerance &&
           g >= profile.g[0] - tolerance && g <= profile.g[1] + tolerance &&
           b >= profile.b[0] - tolerance && b <= profile.b[1] + tolerance;
  }
}

/**
 * 规则引擎
 * 根据血量变化匹配规则并返回对应动作
 */
export class RuleEngine {
  private rules: BloodRule[] = [];

  constructor() {
    this.loadDefaultRules();
  }

  /**
   * 加载默认规则
   */
  loadDefaultRules(): void {
    this.rules = JSON.parse(JSON.stringify(DEFAULT_RULES));
    logger.info(`已加载 ${this.rules.length} 条默认规则`);
  }

  /**
   * 获取所有规则
   */
  getRules(): BloodRule[] {
    return [...this.rules];
  }

  /**
   * 设置规则列表
   */
  setRules(rules: BloodRule[]): void {
    this.rules = rules;
    logger.info(`规则列表已更新，共 ${this.rules.length} 条`);
  }

  /**
   * 添加规则
   */
  addRule(rule: BloodRule): void {
    // 检查是否存在相同ID的规则
    const existingIndex = this.rules.findIndex(r => r.id === rule.id);
    if (existingIndex >= 0) {
      this.rules[existingIndex] = rule;
      logger.info(`规则 ${rule.id} 已更新`);
    } else {
      this.rules.push(rule);
      logger.info(`规则 ${rule.id} 已添加`);
    }
    this.sortByPriority();
  }

  /**
   * 更新规则
   */
  updateRule(id: string, updates: Partial<BloodRule>): boolean {
    const rule = this.rules.find(r => r.id === id);
    if (!rule) return false;
    
    Object.assign(rule, updates);
    this.sortByPriority();
    logger.info(`规则 ${id} 已更新`);
    return true;
  }

  /**
   * 删除规则
   */
  deleteRule(id: string): boolean {
    const index = this.rules.findIndex(r => r.id === id);
    if (index < 0) return false;
    
    this.rules.splice(index, 1);
    logger.info(`规则 ${id} 已删除`);
    return true;
  }

  /**
   * 启用/禁用规则
   */
  toggleRule(id: string, enabled: boolean): boolean {
    const rule = this.rules.find(r => r.id === id);
    if (!rule) return false;
    
    rule.enabled = enabled;
    logger.info(`规则 ${id} 已${enabled ? '启用' : '禁用'}`);
    return true;
  }

  /**
   * 按优先级排序
   */
  private sortByPriority(): void {
    this.rules.sort((a, b) => a.priority - b.priority);
  }

  /**
   * 匹配规则
   * @param oldBlood 原血量
   * @param newBlood 新血量
   * @returns 匹配结果或 null
   */
  match(oldBlood: number, newBlood: number): { rule: BloodRule; action: RuleAction; change: number } | null {
    const change = oldBlood - newBlood;
    const absChange = Math.abs(change);

    // 按优先级遍历已启用的规则
    for (const rule of this.rules) {
      if (!rule.enabled) continue;

      const cond = rule.conditions;

      // 死亡检测：新血量为0且原血量大于0
      if (cond.type === 'death') {
        if (newBlood === 0 && oldBlood > 0) {
          return { rule, action: rule.action, change: absChange };
        }
        continue;
      }

      // 复活检测：原血量为0且新血量大于0
      if (cond.type === 'revive') {
        if (oldBlood === 0 && newBlood > 0) {
          return { rule, action: rule.action, change: absChange };
        }
        continue;
      }

      // 掉血检测
      if (cond.type === 'decrease' && change > 0) {
        const minChange = cond.minChange ?? 0;
        const maxChange = cond.maxChange ?? Infinity;
        
        if (absChange >= minChange && absChange <= maxChange) {
          return { rule, action: rule.action, change: absChange };
        }
        continue;
      }

      // 加血检测
      if (cond.type === 'increase' && change < 0) {
        const minChange = cond.minChange ?? 0;
        const maxChange = cond.maxChange ?? Infinity;
        
        if (absChange >= minChange && absChange <= maxChange) {
          return { rule, action: rule.action, change: absChange };
        }
        continue;
      }

      // 阈值检测
      if (cond.type === 'threshold' && cond.threshold !== undefined) {
        const crossedThreshold = cond.direction === 'below' 
          ? (oldBlood >= cond.threshold && newBlood < cond.threshold)
          : (oldBlood <= cond.threshold && newBlood > cond.threshold);
        
        if (crossedThreshold) {
          return { rule, action: rule.action, change: absChange };
        }
      }
    }

    return null;
  }
}

/**
 * OCR 血量识别服务
 * 提供 HTTP API 接口进行血量识别
 */
export class OCRService extends EventEmitter {
  private config: OCRConfig;
  private healthBarAnalyzer: HealthBarAnalyzer;
  private armorBarAnalyzer: HealthBarAnalyzer;  // 护甲分析器
  private ruleEngine: RuleEngine;
  private isRunning = false;
  private recognitionTimer: NodeJS.Timeout | null = null;
  private lastBloodValue = 100;
  private lastArmorValue = 0;   // 护甲当前值
  private hasSuccessfulRecognition = false;
  private consecutiveFailures = 0;

  constructor() {
    super();
    this.config = {
      mode: 'healthbar',
      interval: 2000,
      area: { x: 0, y: 0, width: 100, height: 20 },
      initialBlood: 100,
      armor: {
        enabled: false,
        area: { x: 0, y: 0, width: 100, height: 20 },
        initialArmor: 0
      }
    };
    this.healthBarAnalyzer = new HealthBarAnalyzer();
    this.armorBarAnalyzer = new HealthBarAnalyzer();
    this.ruleEngine = new RuleEngine();
  }

  /**
   * 配置服务
   */
  configure(config: Partial<OCRConfig>): void {
    this.config = { ...this.config, ...config };
    if (config.healthbar) {
      this.healthBarAnalyzer.setConfig(config.healthbar);
    }
    // 配置护甲分析器
    if (config.armor?.healthbar) {
      this.armorBarAnalyzer.setConfig(config.armor.healthbar);
    }
    if (config.armor?.initialArmor !== undefined) {
      this.lastArmorValue = config.armor.initialArmor;
    }
    logger.info('OCR服务配置已更新');
  }

  /**
   * 获取当前配置
   */
  getConfig(): OCRConfig {
    return { ...this.config };
  }

  /**
   * 获取规则引擎
   */
  getRuleEngine(): RuleEngine {
    return this.ruleEngine;
  }

  /**
   * 处理图像数据进行识别
   * @param imageBase64 Base64 编码的图像数据
   */
  async recognize(imageBase64: string): Promise<RecognitionResult> {
    try {
      // 解码 base64 图像
      const imageData = this.decodeBase64Image(imageBase64);
      
      let value: number | null = null;
      let confidence = 0;

      if (this.config.mode === 'healthbar' || this.config.mode === 'auto') {
        value = this.healthBarAnalyzer.analyze(imageData);
        confidence = value !== null ? 0.8 : 0;
      }

      return {
        value,
        confidence,
        mode: this.config.mode,
        timestamp: new Date()
      };
    } catch (err) {
      logger.error('识别失败:', (err as Error).message);
      return {
        value: null,
        confidence: 0,
        mode: this.config.mode,
        timestamp: new Date()
      };
    }
  }

  /**
   * 处理血量变化
   */
  processBloodChange(newValue: number): BloodChangeEvent | null {
    if (newValue === this.lastBloodValue) {
      return null;
    }

    const oldValue = this.lastBloodValue;
    const change = oldValue - newValue;
    
    let changeType: BloodChangeEvent['changeType'];
    if (newValue === 0 && oldValue > 0) {
      changeType = 'death';
    } else if (oldValue === 0 && newValue > 0) {
      changeType = 'revive';
    } else if (change > 0) {
      changeType = 'decrease';
    } else {
      changeType = 'increase';
    }

    this.lastBloodValue = newValue;

    // 使用规则引擎匹配规则
    const matchResult = this.ruleEngine.match(oldValue, newValue);

    const event: BloodChangeEvent = {
      oldValue,
      newValue,
      change: Math.abs(change),
      changeType,
      timestamp: new Date(),
      matchedRule: matchResult?.rule,
      action: matchResult?.action
    };

    // 发射血量变化事件
    this.emit('bloodChange', event);
    
    if (matchResult) {
      logger.info(`血量变化: ${oldValue} -> ${newValue} (${changeType}), 触发规则: ${matchResult.rule.name}`);
      // 发射规则匹配事件
      this.emit('ruleMatched', { event, rule: matchResult.rule, action: matchResult.action });
    } else {
      logger.info(`血量变化: ${oldValue} -> ${newValue} (${changeType})`);
    }

    return event;
  }

  /**
   * 校准血条
   */
  calibrate(imageBase64: string): boolean {
    try {
      const imageData = this.decodeBase64Image(imageBase64);
      return this.healthBarAnalyzer.calibrate(imageData);
    } catch (err) {
      logger.error('校准失败:', (err as Error).message);
      return false;
    }
  }

  /**
   * 重置校准
   */
  resetCalibration(): void {
    this.healthBarAnalyzer.resetCalibration();
  }

  /**
   * 重置血量状态
   */
  reset(): void {
    this.lastBloodValue = this.config.initialBlood;
    this.hasSuccessfulRecognition = false;
    this.consecutiveFailures = 0;
    logger.info('OCR服务状态已重置');
  }

  /**
   * 获取当前状态
   */
  getStatus() {
    return {
      isRunning: this.isRunning,
      lastBloodValue: this.lastBloodValue,
      lastArmorValue: this.lastArmorValue,
      armorEnabled: this.config.armor?.enabled ?? false,
      config: this.config,
      hasSuccessfulRecognition: this.hasSuccessfulRecognition,
      rulesCount: this.ruleEngine.getRules().length,
      enabledRulesCount: this.ruleEngine.getRules().filter(r => r.enabled).length
    };
  }

  /**
   * 解码 Base64 图像为 ImageData
   * 注意：这是一个简化实现，实际使用需要 canvas 环境
   */
  private decodeBase64Image(base64: string): ImageData {
    // 移除 data URL 前缀
    const data = base64.replace(/^data:image\/\w+;base64,/, '');
    const buffer = Buffer.from(data, 'base64');
    
    // 简单解析 - 实际应使用图像处理库
    // 这里返回一个模拟的 ImageData
    // 在实际使用中，应该使用 sharp 或 canvas 库来处理
    
    // 为了演示，创建一个空的 ImageData 结构
    const width = this.config.area.width || 100;
    const height = this.config.area.height || 20;
    
    return {
      data: new Uint8ClampedArray(width * height * 4),
      width,
      height,
      colorSpace: 'srgb'
    };
  }
}

// 单例实例
let ocrServiceInstance: OCRService | null = null;

/**
 * 获取 OCR 服务实例
 */
export function getOCRService(): OCRService {
  if (!ocrServiceInstance) {
    ocrServiceInstance = new OCRService();
  }
  return ocrServiceInstance;
}

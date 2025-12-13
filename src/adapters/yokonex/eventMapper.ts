/**
 * YOKONEX 事件映射器
 * 管理游戏事件与设备动作的映射关系
 */

/**
 * 设备动作类型
 */
export enum ActionType {
  Estim = 'estim', // 电击
  Vibrate = 'vibrate', // 震动
  Custom = 'custom', // 自定义
}

/**
 * 设备动作配置
 */
export interface DeviceAction {
  type: ActionType;
  channel?: number; // 通道 (1 或 2)
  strength?: number; // 强度 (0-100)
  duration?: number; // 持续时间(ms)
  waveformId?: string; // 波形ID
}

/**
 * 事件配置
 */
export interface EventConfig {
  id: string; // 事件ID
  name: string; // 事件名称
  description?: string; // 事件描述
  actions: DeviceAction[]; // 设备动作列表
  cooldown?: number; // 冷却时间(ms)
  enabled: boolean; // 是否启用
}

/**
 * 游戏配置
 */
export interface GameConfig {
  gameId: string; // 游戏唯一码
  gameName: string; // 游戏名称
  author: string; // 作者
  events: EventConfig[]; // 事件列表
  createdAt: Date;
  updatedAt: Date;
}

/**
 * 事件映射器类
 */
export class EventMapper {
  private events: Map<string, EventConfig> = new Map();
  private cooldowns: Map<string, number> = new Map();

  /**
   * 加载游戏配置
   */
  loadGameConfig(config: GameConfig): void {
    this.events.clear();
    for (const event of config.events) {
      this.events.set(event.id, event);
    }
  }

  /**
   * 注册单个事件
   */
  registerEvent(event: EventConfig): void {
    this.events.set(event.id, event);
  }

  /**
   * 移除事件
   */
  removeEvent(eventId: string): boolean {
    return this.events.delete(eventId);
  }

  /**
   * 获取事件配置
   */
  getEvent(eventId: string): EventConfig | undefined {
    return this.events.get(eventId);
  }

  /**
   * 获取所有事件
   */
  getAllEvents(): EventConfig[] {
    return Array.from(this.events.values());
  }

  /**
   * 检查事件是否在冷却中
   */
  isInCooldown(eventId: string): boolean {
    const lastTime = this.cooldowns.get(eventId);
    if (!lastTime) return false;

    const event = this.events.get(eventId);
    if (!event || !event.cooldown) return false;

    return Date.now() - lastTime < event.cooldown;
  }

  /**
   * 触发事件，返回要执行的动作
   */
  triggerEvent(eventId: string): DeviceAction[] | null {
    const event = this.events.get(eventId);

    if (!event) {
      console.warn(`Event not found: ${eventId}`);
      return null;
    }

    if (!event.enabled) {
      console.warn(`Event is disabled: ${eventId}`);
      return null;
    }

    if (this.isInCooldown(eventId)) {
      console.warn(`Event is in cooldown: ${eventId}`);
      return null;
    }

    // 记录触发时间
    this.cooldowns.set(eventId, Date.now());

    return event.actions;
  }

  /**
   * 重置冷却
   */
  resetCooldown(eventId?: string): void {
    if (eventId) {
      this.cooldowns.delete(eventId);
    } else {
      this.cooldowns.clear();
    }
  }

  /**
   * 创建默认事件配置
   */
  static createDefaultEvent(
    id: string,
    name: string,
    strength: number = 30,
    duration: number = 1000
  ): EventConfig {
    return {
      id,
      name,
      enabled: true,
      actions: [
        {
          type: ActionType.Estim,
          channel: 1,
          strength,
          duration,
        },
      ],
    };
  }

  /**
   * 创建示例游戏配置
   */
  static createSampleGameConfig(): GameConfig {
    return {
      gameId: 'sample.game.demo',
      gameName: '示例游戏',
      author: 'Developer',
      events: [
        EventMapper.createDefaultEvent('hurt', '受伤', 20, 500),
        EventMapper.createDefaultEvent('hit', '命中', 30, 800),
        EventMapper.createDefaultEvent('kill', '击杀', 50, 1500),
        EventMapper.createDefaultEvent('death', '死亡', 70, 2000),
        {
          id: 'heal',
          name: '治疗',
          enabled: true,
          actions: [
            {
              type: ActionType.Vibrate,
              channel: 2,
              strength: 15,
              duration: 1000,
            },
          ],
        },
      ],
      createdAt: new Date(),
      updatedAt: new Date(),
    };
  }
}

/**
 * 事件调度器
 * 处理定时任务、事件队列和触发逻辑
 */

import { Channel, WaveformData, StrengthMode } from '../adapters';
import { DeviceManager } from './DeviceManager';
import { Logger } from '../utils/logger';

const logger = new Logger('EventScheduler');

/**
 * 调度任务类型
 */
export enum TaskType {
  Strength = 'strength',
  Waveform = 'waveform',
  Event = 'event',
  Custom = 'custom',
}

/**
 * 调度任务
 */
export interface ScheduledTask {
  id: string;
  type: TaskType;
  deviceId?: string; // 不指定则广播
  channel?: Channel;
  data: any;
  executeAt: Date;
  repeat?: {
    interval: number; // 毫秒
    count?: number; // 重复次数，不指定则无限
  };
  callback?: () => void;
}

/**
 * 任务执行结果
 */
export interface TaskResult {
  taskId: string;
  success: boolean;
  error?: string;
  executedAt: Date;
}

/**
 * 事件队列项
 */
interface QueueItem {
  task: ScheduledTask;
  remainingRepeats?: number;
}

/**
 * 事件调度器类
 */
export class EventScheduler {
  private deviceManager: DeviceManager;
  private tasks: Map<string, QueueItem> = new Map();
  private timers: Map<string, NodeJS.Timeout> = new Map();
  private taskCounter = 0;
  private isRunning = false;

  constructor(deviceManager: DeviceManager) {
    this.deviceManager = deviceManager;
  }

  /**
   * 启动调度器
   */
  start(): void {
    if (this.isRunning) return;
    this.isRunning = true;
    logger.info('Event scheduler started');
  }

  /**
   * 停止调度器
   */
  stop(): void {
    this.isRunning = false;
    // 清除所有定时器
    for (const timer of this.timers.values()) {
      clearTimeout(timer);
    }
    this.timers.clear();
    logger.info('Event scheduler stopped');
  }

  /**
   * 添加任务
   */
  schedule(task: Omit<ScheduledTask, 'id'>): string {
    const id = `task_${++this.taskCounter}`;
    const fullTask: ScheduledTask = { ...task, id };

    const queueItem: QueueItem = {
      task: fullTask,
      remainingRepeats: task.repeat?.count,
    };

    this.tasks.set(id, queueItem);
    this.scheduleExecution(queueItem);

    logger.debug(`Task scheduled: ${id}`, { type: task.type, executeAt: task.executeAt });
    return id;
  }

  /**
   * 取消任务
   */
  cancel(taskId: string): boolean {
    const timer = this.timers.get(taskId);
    if (timer) {
      clearTimeout(timer);
      this.timers.delete(taskId);
    }

    const removed = this.tasks.delete(taskId);
    if (removed) {
      logger.debug(`Task cancelled: ${taskId}`);
    }
    return removed;
  }

  /**
   * 立即执行任务
   */
  async executeNow(task: Omit<ScheduledTask, 'id' | 'executeAt'>): Promise<TaskResult> {
    const id = `immediate_${++this.taskCounter}`;
    const fullTask: ScheduledTask = {
      ...task,
      id,
      executeAt: new Date(),
    };

    return this.executeTask(fullTask);
  }

  /**
   * 延迟执行
   */
  delay(
    task: Omit<ScheduledTask, 'id' | 'executeAt'>,
    delayMs: number
  ): string {
    return this.schedule({
      ...task,
      executeAt: new Date(Date.now() + delayMs),
    });
  }

  /**
   * 创建周期任务
   */
  repeat(
    task: Omit<ScheduledTask, 'id' | 'executeAt' | 'repeat'>,
    intervalMs: number,
    count?: number
  ): string {
    return this.schedule({
      ...task,
      executeAt: new Date(),
      repeat: { interval: intervalMs, count },
    });
  }

  /**
   * 快捷方法：调度强度变化
   */
  scheduleStrength(
    deviceId: string | undefined,
    channel: Channel,
    value: number,
    mode: StrengthMode,
    delayMs: number = 0
  ): string {
    return this.schedule({
      type: TaskType.Strength,
      deviceId,
      channel,
      data: { value, mode },
      executeAt: new Date(Date.now() + delayMs),
    });
  }

  /**
   * 快捷方法：调度波形发送
   */
  scheduleWaveform(
    deviceId: string | undefined,
    channel: Channel,
    waveform: WaveformData,
    delayMs: number = 0
  ): string {
    return this.schedule({
      type: TaskType.Waveform,
      deviceId,
      channel,
      data: waveform,
      executeAt: new Date(Date.now() + delayMs),
    });
  }

  /**
   * 快捷方法：调度事件发送
   */
  scheduleEvent(
    deviceId: string | undefined,
    eventId: string,
    payload?: Record<string, unknown>,
    delayMs: number = 0
  ): string {
    return this.schedule({
      type: TaskType.Event,
      deviceId,
      data: { eventId, payload },
      executeAt: new Date(Date.now() + delayMs),
    });
  }

  /**
   * 获取待执行任务
   */
  getPendingTasks(): ScheduledTask[] {
    return Array.from(this.tasks.values()).map((item) => item.task);
  }

  /**
   * 获取任务状态
   */
  getTaskStatus(taskId: string): QueueItem | undefined {
    return this.tasks.get(taskId);
  }

  /**
   * 安排任务执行
   */
  private scheduleExecution(item: QueueItem): void {
    if (!this.isRunning) return;

    const now = Date.now();
    const executeAt = item.task.executeAt.getTime();
    const delay = Math.max(0, executeAt - now);

    const timer = setTimeout(async () => {
      await this.executeAndReschedule(item);
    }, delay);

    this.timers.set(item.task.id, timer);
  }

  /**
   * 执行任务并处理重复
   */
  private async executeAndReschedule(item: QueueItem): Promise<void> {
    this.timers.delete(item.task.id);

    // 执行任务
    const result = await this.executeTask(item.task);
    logger.debug(`Task executed: ${item.task.id}`, { success: result.success });

    // 处理重复
    if (item.task.repeat) {
      if (item.remainingRepeats !== undefined) {
        item.remainingRepeats--;
        if (item.remainingRepeats <= 0) {
          this.tasks.delete(item.task.id);
          return;
        }
      }

      // 安排下次执行
      item.task.executeAt = new Date(Date.now() + item.task.repeat.interval);
      this.scheduleExecution(item);
    } else {
      this.tasks.delete(item.task.id);
    }
  }

  /**
   * 执行单个任务
   */
  private async executeTask(task: ScheduledTask): Promise<TaskResult> {
    const result: TaskResult = {
      taskId: task.id,
      success: false,
      executedAt: new Date(),
    };

    try {
      switch (task.type) {
        case TaskType.Strength:
          await this.executeStrengthTask(task);
          break;
        case TaskType.Waveform:
          await this.executeWaveformTask(task);
          break;
        case TaskType.Event:
          await this.executeEventTask(task);
          break;
        case TaskType.Custom:
          if (task.callback) {
            task.callback();
          }
          break;
      }

      result.success = true;
    } catch (error) {
      result.error = (error as Error).message;
      logger.error(`Task execution failed: ${task.id}`, result.error);
    }

    return result;
  }

  /**
   * 执行强度任务
   */
  private async executeStrengthTask(task: ScheduledTask): Promise<void> {
    const { value, mode } = task.data;
    const channel = task.channel!;

    if (task.deviceId) {
      await this.deviceManager.setStrength(task.deviceId, channel, value, mode);
    } else {
      await this.deviceManager.broadcastStrength(channel, value, mode);
    }
  }

  /**
   * 执行波形任务
   */
  private async executeWaveformTask(task: ScheduledTask): Promise<void> {
    const waveform = task.data as WaveformData;
    const channel = task.channel!;

    if (task.deviceId) {
      await this.deviceManager.sendWaveform(task.deviceId, channel, waveform);
    } else {
      await this.deviceManager.broadcastWaveform(channel, waveform);
    }
  }

  /**
   * 执行事件任务
   */
  private async executeEventTask(task: ScheduledTask): Promise<void> {
    const { eventId, payload } = task.data;

    if (task.deviceId) {
      await this.deviceManager.sendEvent(task.deviceId, eventId, payload);
    } else {
      await this.deviceManager.broadcastEvent(eventId, payload);
    }
  }
}

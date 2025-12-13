/**
 * Minecraft 联动脚本 - 完整示例
 * 
 * 功能说明:
 * 1. 玩家受伤时根据伤害值触发相应强度的反馈
 * 2. 玩家死亡时触发强烈反馈
 * 3. 玩家击杀怪物时触发轻微奖励反馈
 * 4. 环境伤害(火焰、摔落、溺水等)触发特定反馈
 * 5. 支持自定义配置
 * 
 * 使用方法:
 * 1. 在 Minecraft 服务器端安装 mod/插件 发送 HTTP 请求
 * 2. 或使用客户端 mod 监听游戏事件并发送到本服务
 * 
 * 事件 API:
 * POST /api/scripts/trigger
 * Content-Type: application/json
 * 
 * 事件类型:
 * - player_hurt: 玩家受伤 { damage: number, source?: string }
 * - player_death: 玩家死亡 { cause?: string }
 * - player_kill: 玩家击杀 { target?: string, weapon?: string }
 * - env_damage: 环境伤害 { type: 'fire'|'lava'|'fall'|'drown'|'void', damage: number }
 * - player_respawn: 玩家重生
 * - game_pause: 游戏暂停
 * - game_resume: 游戏继续
 */

// ==================== 配置区域 ====================
const CONFIG = {
  // 受伤配置
  hurt: {
    baseStrength: 25,      // 基础强度
    multiplier: 2.5,       // 伤害倍数 (强度 = 基础 + 伤害 * 倍数)
    maxStrength: 120,      // 最大强度限制
    duration: 400,         // 持续时间 (ms)
    channel: 'A',          // 使用通道
  },
  
  // 死亡配置
  death: {
    strength: 150,         // 死亡强度
    duration: 3000,        // 持续时间 (ms)
    channel: 'A',
    fadeOut: true,         // 是否渐弱
    fadeSteps: 6,          // 渐弱步数
  },
  
  // 击杀配置
  kill: {
    strength: 15,          // 击杀奖励强度
    duration: 200,         // 持续时间 (ms)
    channel: 'B',          // 使用 B 通道区分
  },
  
  // 环境伤害配置
  envDamage: {
    fire: { strength: 35, duration: 300 },
    lava: { strength: 80, duration: 500 },
    fall: { multiplier: 4, minStrength: 20, duration: 300 },
    drown: { strength: 45, duration: 400, repeat: true, interval: 1000 },
    void: { strength: 100, duration: 1000 },
  },
  
  // 全局配置
  global: {
    cooldown: 200,         // 全局冷却时间 (ms)
    safetyLimit: 180,      // 安全上限 (永不超过此值)
    enabled: true,         // 脚本启用状态
  }
};

// ==================== 状态管理 ====================
let lastTriggerTime = 0;
let isGamePaused = false;
let isDrowning = false;
let drownTimer = null;

// ==================== 工具函数 ====================

/**
 * 检查是否可以触发 (冷却检查)
 */
function canTrigger() {
  if (!CONFIG.global.enabled) {
    script.log('脚本已禁用，跳过触发');
    return false;
  }
  
  if (isGamePaused) {
    script.log('游戏已暂停，跳过触发');
    return false;
  }
  
  const now = Date.now();
  if (now - lastTriggerTime < CONFIG.global.cooldown) {
    return false;
  }
  lastTriggerTime = now;
  return true;
}

/**
 * 获取通道枚举值
 */
function getChannel(channelName) {
  return channelName === 'A' ? Channel.A : Channel.B;
}

/**
 * 安全的强度值
 */
function safeStrength(value) {
  return script.clamp(value, 0, CONFIG.global.safetyLimit);
}

/**
 * 触发设备反馈
 */
async function triggerFeedback(channel, strength, duration, options = {}) {
  const devices = device.getConnectedDevices();
  
  if (devices.length === 0) {
    script.warn('没有已连接的设备');
    return;
  }
  
  const ch = getChannel(channel);
  const safeValue = safeStrength(strength);
  
  script.log(`触发反馈: 通道=${channel}, 强度=${safeValue}, 持续=${duration}ms`);
  
  // 设置强度
  for (const deviceId of devices) {
    try {
      await device.setStrength(deviceId, ch, safeValue);
    } catch (error) {
      script.error(`设置强度失败 (${deviceId}): ${error.message}`);
    }
  }
  
  // 渐弱效果
  if (options.fadeOut && options.fadeSteps > 0) {
    const stepDuration = duration / options.fadeSteps;
    const stepStrength = safeValue / options.fadeSteps;
    
    for (let i = options.fadeSteps - 1; i >= 0; i--) {
      await script.sleep(stepDuration);
      const currentStrength = Math.round(stepStrength * i);
      
      for (const deviceId of devices) {
        try {
          await device.setStrength(deviceId, ch, currentStrength);
        } catch (error) {
          // 忽略渐弱过程中的错误
        }
      }
    }
  } else {
    // 普通持续
    await script.sleep(duration);
    
    // 归零
    for (const deviceId of devices) {
      try {
        await device.setStrength(deviceId, ch, 0);
      } catch (error) {
        script.error(`归零失败 (${deviceId}): ${error.message}`);
      }
    }
  }
}

/**
 * 发送到 YOKONEX 设备
 */
async function sendToYokonex(eventId) {
  const devices = device.getConnectedDevices();
  
  for (const deviceId of devices) {
    try {
      await device.sendEvent(deviceId, eventId);
      script.log(`YOKONEX 事件已发送: ${eventId} -> ${deviceId}`);
    } catch (error) {
      // 非 YOKONEX 设备会失败，忽略
    }
  }
}

// ==================== 事件处理 ====================

/**
 * 玩家受伤事件
 */
events.on('player_hurt', async (data) => {
  if (!canTrigger()) return;
  
  const damage = data.damage || 5;
  const source = data.source || 'unknown';
  
  // 计算强度
  const config = CONFIG.hurt;
  let strength = config.baseStrength + (damage * config.multiplier);
  strength = script.clamp(strength, 10, config.maxStrength);
  
  script.log(`玩家受伤: 伤害=${damage}, 来源=${source} -> 强度=${Math.round(strength)}`);
  
  // 发送 YOKONEX 事件
  await sendToYokonex('hurt');
  
  // 触发 DG-LAB 反馈
  await triggerFeedback(config.channel, strength, config.duration);
});

/**
 * 玩家死亡事件
 */
events.on('player_death', async (data) => {
  const cause = data.cause || 'unknown';
  script.log(`玩家死亡! 原因: ${cause}`);
  
  // 停止溺水循环
  if (drownTimer) {
    clearInterval(drownTimer);
    drownTimer = null;
    isDrowning = false;
  }
  
  lastTriggerTime = Date.now(); // 重置冷却
  
  // 发送 YOKONEX 事件
  await sendToYokonex('death');
  
  // 触发强烈反馈
  const config = CONFIG.death;
  await triggerFeedback(config.channel, config.strength, config.duration, {
    fadeOut: config.fadeOut,
    fadeSteps: config.fadeSteps,
  });
});

/**
 * 玩家击杀事件
 */
events.on('player_kill', async (data) => {
  const target = data.target || '未知生物';
  const weapon = data.weapon || '未知武器';
  
  script.log(`玩家击杀: ${target} (使用 ${weapon})`);
  
  // 发送 YOKONEX 事件
  await sendToYokonex('kill');
  
  // 触发轻微奖励反馈
  const config = CONFIG.kill;
  await triggerFeedback(config.channel, config.strength, config.duration);
});

/**
 * 环境伤害事件
 */
events.on('env_damage', async (data) => {
  if (!canTrigger()) return;
  
  const type = data.type || 'unknown';
  const damage = data.damage || 5;
  
  const envConfig = CONFIG.envDamage[type];
  if (!envConfig) {
    script.warn(`未知的环境伤害类型: ${type}`);
    return;
  }
  
  script.log(`环境伤害: 类型=${type}, 伤害=${damage}`);
  
  // 计算强度
  let strength;
  if (envConfig.multiplier) {
    strength = Math.max(envConfig.minStrength || 20, damage * envConfig.multiplier);
  } else {
    strength = envConfig.strength;
  }
  
  // 特殊处理: 溺水循环
  if (type === 'drown' && envConfig.repeat) {
    if (!isDrowning) {
      isDrowning = true;
      script.log('开始溺水循环反馈');
      
      drownTimer = setInterval(async () => {
        if (!isDrowning) {
          clearInterval(drownTimer);
          drownTimer = null;
          return;
        }
        await triggerFeedback('A', strength, envConfig.duration);
      }, envConfig.interval);
      
      // 立即触发第一次
      await triggerFeedback('A', strength, envConfig.duration);
    }
  } else {
    await triggerFeedback('A', strength, envConfig.duration);
  }
});

/**
 * 玩家重生事件
 */
events.on('player_respawn', async () => {
  script.log('玩家重生');
  
  // 停止所有持续效果
  isDrowning = false;
  if (drownTimer) {
    clearInterval(drownTimer);
    drownTimer = null;
  }
  
  // 确保设备归零
  const devices = device.getConnectedDevices();
  for (const deviceId of devices) {
    try {
      await device.setStrength(deviceId, Channel.A, 0);
      await device.setStrength(deviceId, Channel.B, 0);
    } catch (error) {
      // 忽略
    }
  }
});

/**
 * 游戏暂停/继续
 */
events.on('game_pause', () => {
  script.log('游戏已暂停');
  isGamePaused = true;
  
  // 停止溺水循环
  isDrowning = false;
  if (drownTimer) {
    clearInterval(drownTimer);
    drownTimer = null;
  }
});

events.on('game_resume', () => {
  script.log('游戏继续');
  isGamePaused = false;
});

/**
 * 自定义配置更新
 */
events.on('config_update', (data) => {
  if (data.enabled !== undefined) {
    CONFIG.global.enabled = data.enabled;
    script.log(`脚本启用状态: ${CONFIG.global.enabled}`);
  }
  
  if (data.safetyLimit !== undefined) {
    CONFIG.global.safetyLimit = script.clamp(data.safetyLimit, 50, 200);
    script.log(`安全上限更新: ${CONFIG.global.safetyLimit}`);
  }
  
  if (data.cooldown !== undefined) {
    CONFIG.global.cooldown = script.clamp(data.cooldown, 100, 2000);
    script.log(`冷却时间更新: ${CONFIG.global.cooldown}ms`);
  }
});

// ==================== 脚本启动 ====================
script.log('==========================================');
script.log('  Minecraft 联动脚本 v1.0.0');
script.log('==========================================');
script.log('作者: Device Adapter Team');
script.log('功能: 玩家受伤/死亡/击杀/环境伤害联动');
script.log('');
script.log('配置信息:');
script.log(`  - 受伤基础强度: ${CONFIG.hurt.baseStrength}`);
script.log(`  - 受伤倍数: ${CONFIG.hurt.multiplier}x`);
script.log(`  - 死亡强度: ${CONFIG.death.strength}`);
script.log(`  - 安全上限: ${CONFIG.global.safetyLimit}`);
script.log(`  - 全局冷却: ${CONFIG.global.cooldown}ms`);
script.log('');
script.log('等待游戏事件...');

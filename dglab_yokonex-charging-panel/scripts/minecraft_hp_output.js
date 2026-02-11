// Minecraft HP Output Mod 适配脚本
// 
// 适配 mc-mod 项目 (https://github.com/yomo40/mc-mod)
// 通过监听 UDP 端口 39571 接收 Minecraft 血量/受击事件
//
// 事件类型:
//   - health: 血量更新
//   - damage: 受伤事件
//   - heal: 治疗事件
//   - death: 死亡事件
//
// 可用 API:
//   device.setStrength(channel, value)      - 设置强度
//   device.addStrength(channel, value)      - 增加强度
//   device.sendWaveform(channel, waveform)  - 发送波形
//   events.trigger(eventId)                 - 触发事件
//   events.onBloodChange(callback)          - 监听血量变化
//   storage.get(key)                        - 获取存储值
//   storage.set(key, value)                 - 设置存储值
//   script.log(message)                     - 输出日志

// ============ 配置区域 ============
var config = {
    // UDP 端口 (需与 mc-mod 配置一致)
    udpPort: 39571,
    
    // 基础强度 (0-200)
    baseStrength: 30,
    
    // 伤害强度倍率 (伤害值 * 倍率 = 额外强度)
    damageMultiplier: 8,
    
    // 最大强度限制
    maxStrength: 150,
    
    // 死亡时的强度
    deathStrength: 200,
    
    // 死亡惩罚持续时间 (毫秒)
    deathDuration: 3000,
    
    // 是否启用治疗时减少强度
    healReduceStrength: true,
    
    // 治疗减少强度倍率
    healMultiplier: 5,
    
    // 目标通道 ('A', 'B', 'AB')
    channel: 'AB'
};

// ============ 初始化 ============
script.log('Minecraft HP Output 适配脚本已加载');
script.log('监听 UDP 端口: ' + config.udpPort);

// 存储上一次的血量百分比
var lastHealthPercent = storage.get('lastHealthPercent') || 100;

// ============ 血量变化监听 ============
// 注意: 这里使用 events.onBloodChange 来模拟 UDP 数据接收
// 实际的 UDP 监听需要在主程序中实现，然后转换为血量变化事件

events.onBloodChange(function(oldValue, newValue) {
    var change = oldValue - newValue;
    var healthPercent = newValue;
    
    // 保存当前血量
    storage.set('lastHealthPercent', healthPercent);
    
    if (change > 0) {
        // ===== 受伤处理 =====
        script.log('[MC] 受伤: -' + change.toFixed(1) + '% | 当前血量: ' + healthPercent.toFixed(1) + '%');
        
        // 计算强度: 基础强度 + 伤害值 * 倍率
        var strength = config.baseStrength + Math.round(change * config.damageMultiplier);
        
        // 限制最大强度
        if (strength > config.maxStrength) {
            strength = config.maxStrength;
        }
        
        // 根据伤害大小选择不同的响应
        if (change >= 50) {
            // 致命伤害 (超过50%血量)
            script.log('[MC] 致命伤害! 强度: ' + strength);
            device.setStrength(config.channel, strength);
            events.trigger('critical_damage');
            
        } else if (change >= 25) {
            // 重伤 (25-50%血量)
            script.log('[MC] 重伤! 强度: ' + strength);
            device.setStrength(config.channel, strength);
            events.trigger('heavy_damage');
            
        } else if (change >= 10) {
            // 中等伤害 (10-25%血量)
            script.log('[MC] 中等伤害 强度: ' + strength);
            device.setStrength(config.channel, strength);
            
        } else {
            // 轻伤 (<10%血量)
            script.log('[MC] 轻伤 强度: +' + Math.round(change * config.damageMultiplier));
            device.addStrength(config.channel, Math.round(change * config.damageMultiplier));
        }
        
        // 检查是否死亡 (血量为0)
        if (healthPercent <= 0) {
            script.log('[MC] 玩家死亡!');
            device.setStrength(config.channel, config.deathStrength);
            events.trigger('death');
        }
        
    } else if (change < 0 && config.healReduceStrength) {
        // ===== 治疗处理 =====
        var healAmount = -change;
        script.log('[MC] 治疗: +' + healAmount.toFixed(1) + '% | 当前血量: ' + healthPercent.toFixed(1) + '%');
        
        // 治疗时减少强度
        var reduceAmount = Math.round(healAmount * config.healMultiplier);
        device.addStrength(config.channel, -reduceAmount);
    }
});

// ============ 返回脚本元信息 ============
return {
    name: 'Minecraft HP Output',
    game: 'Minecraft',
    version: '1.0.0',
    author: 'Kiro',
    description: '适配 mc-mod 项目，监听 UDP 端口接收血量/受击事件'
};

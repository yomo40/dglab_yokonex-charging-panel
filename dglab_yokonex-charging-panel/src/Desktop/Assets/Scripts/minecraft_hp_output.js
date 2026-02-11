// Minecraft HP Output Mod 适配脚本
//请下载mod https://github.com/yomo40/-mc_mod-hp-output
// 
// 适配 mc-mod 项目 - 通过 UDP 接收 Minecraft 血量/受击事件
// 
// UDP 数据格式 (JSON):
//   - health: {"type":"health","health":18.0,"maxHealth":20.0,"percentage":0.9}
//   - damage: {"type":"damage","damage":3.0,"health":17.0,"source":"mob:Zombie"}
//   - heal:   {"type":"heal","amount":2.0,"health":19.0}
//   - death:  {"type":"death","source":"fall"}

// ============ 用户配置区域 ============
var config = {
    // UDP 端口 (需与 mc-mod 游戏内配置一致，默认 39571)
    udpPort: 39571,
    
    // 基础强度 (0-200)
    baseStrength: 30,
    
    // 伤害强度倍率 (伤害值 * 倍率 = 额外强度)
    // MC中1点伤害=半颗心，满血20点
    damageMultiplier: 10,
    
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
// ============ 配置结束 ============

script.log('Minecraft HP Output 适配脚本已加载');
script.log('UDP 端口: ' + config.udpPort);

// 监听血量变化 (由主程序将UDP数据转换为血量百分比变化)
events.onBloodChange(function(oldValue, newValue) {
    var change = oldValue - newValue;
    
    if (change > 0) {
        // 受伤
        script.log('[MC] 受伤: -' + change.toFixed(1) + '% | 血量: ' + newValue.toFixed(1) + '%');
        
        var strength = config.baseStrength + Math.round(change * config.damageMultiplier);
        if (strength > config.maxStrength) strength = config.maxStrength;
        
        if (change >= 50) {
            // 致命伤害
            script.log('[MC] 致命伤害! 强度: ' + strength);
            device.setStrength(config.channel, strength);
        } else if (change >= 25) {
            // 重伤
            script.log('[MC] 重伤! 强度: ' + strength);
            device.setStrength(config.channel, strength);
        } else if (change >= 10) {
            // 中等伤害
            device.setStrength(config.channel, strength);
        } else {
            // 轻伤 - 叠加强度
            device.addStrength(config.channel, Math.round(change * config.damageMultiplier));
        }
        
        // 死亡检测
        if (newValue <= 0) {
            script.log('[MC] 玩家死亡!');
            device.setStrength(config.channel, config.deathStrength);
            events.trigger('death');
        }
        
    } else if (change < 0 && config.healReduceStrength) {
        // 治疗 - 减少强度
        var healAmount = -change;
        script.log('[MC] 治疗: +' + healAmount.toFixed(1) + '%');
        device.addStrength(config.channel, -Math.round(healAmount * config.healMultiplier));
    }
});

return {
    name: 'Minecraft HP Output',
    game: 'Minecraft',
    version: '1.0.0'
};

# MOD 开发文档

> 更新时间：2026-02-11  
> 适用版本：v1.0.1_alpha~new

## 1. 谁该看？

本文面向 MOD 开发者，解决两个问题：

1. 如何把游戏 MOD 接入本程序。  
2. 如何把游戏事件稳定上报到规则引擎并触发设备动作。

## 2. 接入总览

程序当前提供三种 MOD 事件接入通道：

- `WebSocket`：`ws://127.0.0.1:39001`
- `HTTP`：`http://127.0.0.1:39002/api/event`
- `UDP`：`udp://127.0.0.1:{port}`（端口由脚本声明）

所有通道最终都会转成统一 `GameEvent`：

```text
MOD -> ModBridgeService -> EventBus -> EventProcessor -> EventService -> DeviceManager
```

## 3. 最小可用接入

### 3.1 方案A：HTTP（最简单）

适合只做单向上报，不需要会话。

- 请求地址：`POST http://127.0.0.1:39002/api/event`
- 请求体最小字段：

```json
{
  "eventId": "lost-hp",//触发事件
  "oldValue": 100,//旧值
  "newValue": 80//新值
}
```

- 成功返回：

```json
{
  "type": "ack",
  "eventId": "lost-hp",//触发事件
  "timestamp": 1730000000000//规则引擎动作时间戳
}
```

### 3.2 方案B：WebSocket（推荐）

适合实时事件、需要双向 ACK/错误返回。

1. 连接：`ws://127.0.0.1:39001`  
2. 发送 `hello` 建立会话  
3. 发送 `event`  4. 定时 `heartbeat`

`hello` 示例：

```json
{
  "type": "hello",//起始字段
  "scriptId": "my_game_mod",//脚本唯一标识符
  "name": "MyGame Mod",//定义mod名称
  "game": "MyGame",//定义适用游戏
  "version": "1.0.0"//定义版本
}
```

服务端 `welcome`：

```json
{
  "type": "welcome",//起始数据头
  "sessionId": "...",//会话id
  "rulesAccepted": 0,//返回接受的规则
  "rulesRejected": 0,//返回无法实现的规则
  "rejectedEventIds": [],//无法实现规则id
  "connectedDevices": []//软件连接的设备id
}
```

`event` 示例：

```json
{
  "type": "event",
  "sessionId": "<from_welcome>",
  "eventId": "lost-hp",
  "gameEventType": "HealthLost",
  "oldValue": 100,
  "newValue": 80,
  "data": {
    "change": 20
  }
}
```

### 3.3 方案C：UDP

适合本地高频上报。虽然是 UDP，但程序实现了逻辑会话。

流程：`hello -> welcome(sessionId) -> event/heartbeat -> goodbye`

可发送：

```json
{ "type": "template" }
```

获取程序返回的 UDP 协议模板。

## 4. 事件字段规范

`event` 通用字段：

- `eventId`：必填，规则事件 ID。
- `gameEventType`：可选，枚举字符串（如 `HealthLost`、`Death`），缺省为 `Custom`。
- `oldValue/newValue`：可选，数值事件建议携带。
- `targetDeviceId`：可选，指定目标设备。
- `source`：可选，不填时服务端自动生成。
- `data`：可选，对象，附加业务字段。

注意：`eventId` 为空会被拒绝，返回 `invalid_event`。

## 5. 会话级规则自注册（hello.rules）

你可以在 `hello` 中携带 `rules`，让 MOD 动态注入规则：

```json
{
  "type": "hello",
  "scriptId": "my_game_mod",
  "rules": [
    {
      "eventId": "boss-hit",
      "name": "Boss受击",
      "action": "set",
      "channel": "A",
      "value": 30,
      "duration": 500,
      "triggerType": "value-decrease",
      "minChange": 5,
      "condition": {
        "field": "newValue",
        "operator": "<=",
        "value": 40
      }
    }
  ]
}
```

规则特点：

- 仅会话有效（断开自动清理）。
- 单会话最多 `20` 条。
- 与已有 `eventId` 冲突会拒绝。
- 命中后走统一规则执行链路。

## 6. 内置脚本桥接 API（给程序内 JS 脚本）

在脚本里可用 `bridge` API：

- `bridge.config({name, version})`
- `bridge.startWebSocket()`
- `bridge.startHTTP()`
- `bridge.startUDP({port})`
- `bridge.onEvent(fn)`

示例：

```javascript
bridge.config({ name: "My Mod", version: "1.0.0" });
bridge.startWebSocket();

bridge.onEvent(function (payload) {
  if (!payload) return null;
  return {
    eventId: payload.eventId || "custom-event",
    gameEventType: "Custom",
    oldValue: payload.oldValue || 0,
    newValue: payload.newValue || 0,
    data: payload.data || {}
  };
});
```

## 7. 限制与安全

- 监听地址只在 `127.0.0.1`（本机回环）。
- 默认限流：每 key 每秒 `50` 条。
- UDP 单包上限：`4096` 字节。
- WS 文本消息最大：`128KB`（超限会断开）。

## 8. 不能作为“触发规则”的事件 ID

以下事件是设备遥测/系统事件，不能注册为触发规则：

- `query`
- `new-credit`
- `toy-device-info-changed`
- `pressure-changed`
- `step-count-changed`
- `external-voltage-changed`
- `enema-status-changed`
- `dglab-feedback`
- `device-battery-changed`
- `channel-disconnected`
- `channel-connected`
- `angle-changed`

## 9. 排错清单

### 9.1 发了事件但没有设备动作

按顺序检查：

1. `eventId` 是否为空。  
2. 是否命中了规则。  
3. `eventId` 是否被当作非触发事件。  
4. 目标设备是否在线。  
5. 规则冷却/阈值是否拦截。

### 9.2 UDP 一直报 `session_required`

你没有先发 `hello`，或 `sessionId` 失效。

### 9.3 规则注入被拒绝

常见原因：

- `eventId` 冲突。
- 使用了非触发事件 ID。
- 超过单会话规则上限。

## 10. MOD 开发清单

下面是最常用的 3 类事件写法，直接可用于 `HTTP /api/event` 或 WebSocket `type=event` 的 payload 主体。

### 10.1 开发清单

1. 先确认程序里存在对应规则（例如系统默认规则 `lost-hp`、`dead`、`new-round`）。  
2. 上报时至少带 `eventId`。  
3. 数值变化类事件建议同时带 `oldValue/newValue`。  
4. 若你用 WebSocket/UDP，会话事件请带 `sessionId`。  
5. `eventId` 不要使用文档第 8 节列出的“非触发事件 ID”。

### 10.2 示例 A：血量减少（HealthLost）

推荐写法：

```json
{
  "eventId": "lost-hp",
  "gameEventType": "HealthLost",
  "oldValue": 100,
  "newValue": 76,
  "data": {
    "change": 24,
    "source": "my_game_mod"
  }
}
```

说明：

- `eventId` 用 `lost-hp` 可直接命中默认“血量损失”规则。
- `change` 建议传绝对变化值，便于规则倍率计算。

### 10.3 示例 B：下一回合（NewRound）

推荐写法：

```json
{
  "eventId": "new-round",
  "gameEventType": "NewRound",
  "data": {
    "roundIndex": 2,
    "map": "arena_01"
  }
}
```

说明：

- `new-round` 是当前程序内置支持的回合事件 ID。  
- 回合事件通常不需要 `oldValue/newValue`，放在 `data` 即可。

### 10.4 示例 C：角色死亡（Death）

推荐写法：

```json
{
  "eventId": "dead",
  "gameEventType": "Death",
  "data": {
    "victim": "player_local",
    "killer": "enemy_01",
    "reason": "hp_zero"
  }
}
```

说明：

- `eventId=dead` 会命中默认“角色死亡”规则。  
- `gameEventType=Death` 会被事件处理器映射为死亡路径，适配 PVP/房间惩罚逻辑。

### 10.5 WebSocket 完整包示范

如果你走 WebSocket，这 3 类事件外层要加 `type` 与 `sessionId`：

```json
{
  "type": "event",
  "sessionId": "<from_welcome>",
  "eventId": "lost-hp",
  "gameEventType": "HealthLost",
  "oldValue": 100,
  "newValue": 76,
  "data": {
    "change": 24
  }
}
```

## 11. 程序端 JS 脚本

这一节是“程序内脚本编辑器（游戏适配页）”使用的写法，不是游戏外部 MOD 代码。

### 11.1 当前可用 API（以当前代码实现为准）

- `console.log/info/warn/error/debug(...)`：输出脚本日志
- `event.trigger(eventId, deviceId?, multiplier?)`：直接触发规则事件
- `game.publishEvent(eventType, oldValue?, newValue?)`：发布游戏事件到事件总线
- `device.getConnectedDevices()`：获取已连接设备
- `device.setStrength(deviceId, channel, value)`：设置强度（`A/B/AB`）
- `device.increaseStrength(deviceId, channel, value)`：增量设置强度
- `device.decreaseStrength(deviceId, channel, value)`：减量设置强度
- `device.sendWaveform(deviceId, channel, frequency, strength, duration)`：发送波形
- `device.emergencyStop()`：全部设备紧急停止
- `utils.delay(ms)` / `utils.random(min,max)` / `utils.clamp(v,min,max)` 等工具方法
- `bridge.config(...)` / `bridge.startWebSocket()` / `bridge.startHTTP()` / `bridge.startUDP({port})` / `bridge.onEvent(fn)`：声明式 MOD 接入

### 11.2 程序端最小桥接脚本模板（推荐）

下面示范把外部 MOD 上报统一映射成 3 个事件：`血量减少`、`下一回合`、`角色死亡`。

```javascript
// 1) 声明脚本信息
bridge.config({
  name: "MyGame Bridge",
  version: "1.0.0"
});

// 2) 声明需要的接入通道（按需开启）
bridge.startWebSocket(); // ws://127.0.0.1:39001
bridge.startHTTP();      // http://127.0.0.1:39002/api/event
// bridge.startUDP({ port: 39571 }); // 需要 UDP 时再打开

// 3) 映射外部 payload -> 程序标准事件
bridge.onEvent(function (payload) {
  if (!payload) return null;

  var t = (payload.type || payload.event || "").toLowerCase();

  // 血量减少
  if (t === "damage" || t === "hp_lost") {
    var oldHp = Number(payload.oldHp || payload.oldValue || 100);
    var newHp = Number(payload.hp || payload.newValue || 0);
    return {
      eventId: "lost-hp",
      gameEventType: "HealthLost",
      oldValue: oldHp,
      newValue: newHp,
      multiplier: 1.0
    };
  }

  // 下一回合
  if (t === "next_turn" || t === "new_round") {
    return {
      eventId: "new-round",
      gameEventType: "NewRound",
      oldValue: 0,
      newValue: 0
    };
  }

  // 角色死亡
  if (t === "death" || t === "player_dead") {
    return {
      eventId: "dead",
      gameEventType: "Death",
      oldValue: Number(payload.oldValue || 1),
      newValue: 0
    };
  }

  // 其他事件不处理
  return null;
});
```

### 11.3 只在程序内做测试的脚本（不依赖外部 MOD）

```javascript
console.log("开始发送测试事件");

// 血量减少
game.publishEvent("lost-hp", 100, 75);
utils.delay(300);

// 下一回合
game.publishEvent("new-round", 0, 0);
utils.delay(300);

// 角色死亡
game.publishEvent("dead", 10, 0);
```

### 11.4 关键注意事项

1. `eventId` 建议优先用：`lost-hp` / `new-round` / `dead`。  
2. 数值事件尽量带 `oldValue/newValue`，便于规则倍率计算。  
3. 如果 `bridge.onEvent` 返回 `null`，该条消息会被忽略。  
4. 文档或旧模板中如果出现 `script.log`、`events.trigger` 这类旧写法，请统一替换为当前 API（`console.log`、`event.trigger`）。  

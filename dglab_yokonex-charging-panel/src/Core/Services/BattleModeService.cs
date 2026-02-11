using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.OCR;
using Serilog;

namespace ChargingPanel.Core.Services;

/// <summary>
/// 对战模式服务
/// 实现玩家之间的对战逻辑，与OCR死亡检测集成
/// </summary>
public class BattleModeService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<BattleModeService>();
    
    private readonly DeviceManager _deviceManager;
    private readonly EventBus _eventBus;
    private readonly OCRService? _ocrService;
    
    private readonly ConcurrentDictionary<string, BattlePlayer> _players = new();
    private readonly ConcurrentDictionary<string, BattleRoom> _rooms = new();
    
    private IDisposable? _eventSubscription;
    private bool _isRunning;
    
    /// <summary>对战开始事件</summary>
    public event EventHandler<BattleStartedEventArgs>? BattleStarted;
    
    /// <summary>对战结束事件</summary>
    public event EventHandler<BattleEndedEventArgs>? BattleEnded;
    
    /// <summary>玩家死亡事件</summary>
    public event EventHandler<PlayerDeathEventArgs>? PlayerDeath;
    
    /// <summary>惩罚触发事件</summary>
    public event EventHandler<PunishmentTriggeredEventArgs>? PunishmentTriggered;
    
    public BattleModeService(DeviceManager deviceManager, EventBus eventBus, OCRService? ocrService = null)
    {
        _deviceManager = deviceManager;
        _eventBus = eventBus;
        _ocrService = ocrService;
    }
    
    /// <summary>
    /// 启动对战模式服务
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;
        
        // 订阅游戏事件
        _eventSubscription = _eventBus.Subscribe<GameEvent>(OnGameEvent);
        
        // 订阅OCR事件
        if (_ocrService != null)
        {
            _ocrService.DeathDetected += OnDeathDetected;
            _ocrService.RoundStateChanged += OnRoundStateChanged;
        }
        
        _isRunning = true;
        Logger.Information("对战模式服务已启动");
    }
    
    /// <summary>
    /// 停止对战模式服务
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;
        
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        
        if (_ocrService != null)
        {
            _ocrService.DeathDetected -= OnDeathDetected;
            _ocrService.RoundStateChanged -= OnRoundStateChanged;
        }
        
        _isRunning = false;
        Logger.Information("对战模式服务已停止");
    }
    
    /// <summary>
    /// 创建对战房间
    /// </summary>
    public BattleRoom CreateRoom(string roomName, BattleSettings settings)
    {
        var room = new BattleRoom
        {
            Id = Guid.NewGuid().ToString("N")[..8].ToUpper(),
            Name = roomName,
            Settings = settings,
            CreatedAt = DateTime.UtcNow,
            Status = BattleRoomStatus.Waiting
        };
        
        _rooms[room.Id] = room;
        Logger.Information("创建对战房间: {RoomId} - {RoomName}", room.Id, roomName);
        
        return room;
    }
    
    /// <summary>
    /// 加入对战房间
    /// </summary>
    public bool JoinRoom(string roomId, BattlePlayer player)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            Logger.Warning("房间不存在: {RoomId}", roomId);
            return false;
        }
        
        if (room.Status != BattleRoomStatus.Waiting)
        {
            Logger.Warning("房间已开始或已结束: {RoomId}", roomId);
            return false;
        }
        
        if (room.Players.Count >= room.Settings.MaxPlayers)
        {
            Logger.Warning("房间已满: {RoomId}", roomId);
            return false;
        }
        
        player.RoomId = roomId;
        room.Players[player.Id] = player;
        _players[player.Id] = player;
        
        Logger.Information("玩家 {PlayerId} 加入房间 {RoomId}", player.Id, roomId);
        return true;
    }
    
    /// <summary>
    /// 离开对战房间
    /// </summary>
    public void LeaveRoom(string playerId)
    {
        if (!_players.TryRemove(playerId, out var player)) return;
        
        if (!string.IsNullOrEmpty(player.RoomId) && _rooms.TryGetValue(player.RoomId, out var room))
        {
            room.Players.TryRemove(playerId, out _);
            Logger.Information("玩家 {PlayerId} 离开房间 {RoomId}", playerId, player.RoomId);
        }
    }
    
    /// <summary>
    /// 开始对战
    /// </summary>
    public async Task<bool> StartBattleAsync(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
        {
            return false;
        }
        
        if (room.Players.Count < 2)
        {
            Logger.Warning("玩家数量不足，无法开始对战: {RoomId}", roomId);
            return false;
        }
        
        room.Status = BattleRoomStatus.InProgress;
        room.StartedAt = DateTime.UtcNow;
        room.CurrentRound = 1;
        
        // 重置所有玩家状态
        foreach (var player in room.Players.Values)
        {
            player.IsAlive = true;
            player.DeathCount = 0;
            player.KillCount = 0;
        }
        
        Logger.Information("对战开始: {RoomId}, 玩家数: {PlayerCount}", roomId, room.Players.Count);
        
        BattleStarted?.Invoke(this, new BattleStartedEventArgs
        {
            RoomId = roomId,
            PlayerCount = room.Players.Count
        });
        
        return true;
    }
    
    /// <summary>
    /// 结束对战
    /// </summary>
    public void EndBattle(string roomId, string? winnerId = null)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;
        
        room.Status = BattleRoomStatus.Ended;
        room.EndedAt = DateTime.UtcNow;
        room.WinnerId = winnerId;
        
        Logger.Information("对战结束: {RoomId}, 获胜者: {WinnerId}", roomId, winnerId ?? "无");
        
        BattleEnded?.Invoke(this, new BattleEndedEventArgs
        {
            RoomId = roomId,
            WinnerId = winnerId,
            Duration = room.EndedAt.Value - room.StartedAt!.Value
        });
    }
    
    /// <summary>
    /// 手动触发玩家死亡
    /// </summary>
    public async Task TriggerPlayerDeathAsync(string playerId)
    {
        if (!_players.TryGetValue(playerId, out var player)) return;
        if (!player.IsAlive) return;
        
        player.IsAlive = false;
        player.DeathCount++;
        
        Logger.Information("玩家死亡: {PlayerId}, 死亡次数: {DeathCount}", playerId, player.DeathCount);
        
        PlayerDeath?.Invoke(this, new PlayerDeathEventArgs
        {
            PlayerId = playerId,
            DeathCount = player.DeathCount
        });
        
        // 触发惩罚
        await TriggerPunishmentAsync(player);
        
        // 检查对战是否结束
        CheckBattleEnd(player.RoomId);
    }
    
    /// <summary>
    /// 触发惩罚
    /// </summary>
    private async Task TriggerPunishmentAsync(BattlePlayer player)
    {
        if (string.IsNullOrEmpty(player.RoomId)) return;
        if (!_rooms.TryGetValue(player.RoomId, out var room)) return;
        
        var settings = room.Settings;
        
        // 获取玩家绑定的设备
        IDevice device;
        try
        {
            device = _deviceManager.GetDevice(player.DeviceId);
        }
        catch (KeyNotFoundException)
        {
            Logger.Warning("玩家 {PlayerId} 未绑定设备或设备不存在: {DeviceId}", player.Id, player.DeviceId);
            return;
        }
        
        // 计算惩罚强度
        var strength = settings.BaseStrength;
        if (settings.StrengthIncreasePerDeath > 0)
        {
            strength += settings.StrengthIncreasePerDeath * (player.DeathCount - 1);
        }
        strength = Math.Min(strength, settings.MaxStrength);
        
        Logger.Information("触发惩罚: 玩家={PlayerId}, 设备={DeviceId}, 强度={Strength}, 持续={Duration}ms",
            player.Id, player.DeviceId, strength, settings.PunishmentDuration);
        
        PunishmentTriggered?.Invoke(this, new PunishmentTriggeredEventArgs
        {
            PlayerId = player.Id,
            DeviceId = player.DeviceId,
            Strength = strength,
            Duration = settings.PunishmentDuration
        });
        
        // 发送电击
        try
        {
            await device.SetStrengthAsync(settings.TargetChannel, strength, StrengthMode.Set);
            
            // 持续指定时间后停止
            _ = Task.Run(async () =>
            {
                await Task.Delay(settings.PunishmentDuration);
                await device.SetStrengthAsync(settings.TargetChannel, 0, StrengthMode.Set);
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "发送惩罚失败: {PlayerId}", player.Id);
        }
    }
    
    private void CheckBattleEnd(string? roomId)
    {
        if (string.IsNullOrEmpty(roomId)) return;
        if (!_rooms.TryGetValue(roomId, out var room)) return;
        if (room.Status != BattleRoomStatus.InProgress) return;
        
        var alivePlayers = room.Players.Values.Where(p => p.IsAlive).ToList();
        
        if (alivePlayers.Count <= 1)
        {
            var winnerId = alivePlayers.FirstOrDefault()?.Id;
            EndBattle(roomId, winnerId);
        }
    }
    
    private void OnGameEvent(GameEvent evt)
    {
        // 处理来自规则引擎的游戏事件
        if (evt.Type == GameEventType.Death)
        {
            // 查找本地玩家
            var localPlayer = _players.Values.FirstOrDefault(p => p.IsLocal);
            if (localPlayer != null)
            {
                _ = TriggerPlayerDeathAsync(localPlayer.Id);
            }
        }
    }
    
    private void OnDeathDetected(object? sender, EventArgs e)
    {
        // OCR检测到死亡
        var localPlayer = _players.Values.FirstOrDefault(p => p.IsLocal);
        if (localPlayer != null)
        {
            _ = TriggerPlayerDeathAsync(localPlayer.Id);
        }
    }
    
    private void OnRoundStateChanged(object? sender, RoundStateChangedEventArgs e)
    {
        if (e.NewState == RoundState.NewRound)
        {
            // 新回合，重置玩家存活状态
            foreach (var player in _players.Values)
            {
                player.IsAlive = true;
            }
            
            // 更新房间回合数
            foreach (var room in _rooms.Values.Where(r => r.Status == BattleRoomStatus.InProgress))
            {
                room.CurrentRound++;
            }
        }
        else if (e.NewState == RoundState.GameOver)
        {
            // 游戏结束，结束所有进行中的对战
            foreach (var room in _rooms.Values.Where(r => r.Status == BattleRoomStatus.InProgress))
            {
                // 根据击杀数判断获胜者
                var winner = room.Players.Values.OrderByDescending(p => p.KillCount).FirstOrDefault();
                EndBattle(room.Id, winner?.Id);
            }
        }
    }
    
    public void Dispose()
    {
        Stop();
        _players.Clear();
        _rooms.Clear();
        GC.SuppressFinalize(this);
    }
}

#region 数据类型

/// <summary>对战房间状态</summary>
public enum BattleRoomStatus
{
    Waiting,
    InProgress,
    Ended
}

/// <summary>回合状态</summary>
public enum RoundState
{
    Playing,
    NewRound,
    GameOver
}

/// <summary>对战房间</summary>
public class BattleRoom
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public BattleSettings Settings { get; set; } = new();
    public ConcurrentDictionary<string, BattlePlayer> Players { get; } = new();
    public BattleRoomStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int CurrentRound { get; set; }
    public string? WinnerId { get; set; }
}

/// <summary>对战玩家</summary>
public class BattlePlayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RoomId { get; set; }
    public string DeviceId { get; set; } = "";
    public bool IsLocal { get; set; }
    public bool IsAlive { get; set; } = true;
    public int DeathCount { get; set; }
    public int KillCount { get; set; }
}

/// <summary>对战设置</summary>
public class BattleSettings
{
    /// <summary>最大玩家数</summary>
    public int MaxPlayers { get; set; } = 2;
    
    /// <summary>基础惩罚强度</summary>
    public int BaseStrength { get; set; } = 30;
    
    /// <summary>每次死亡增加的强度</summary>
    public int StrengthIncreasePerDeath { get; set; } = 5;
    
    /// <summary>最大惩罚强度</summary>
    public int MaxStrength { get; set; } = 100;
    
    /// <summary>惩罚持续时间 (ms)</summary>
    public int PunishmentDuration { get; set; } = 3000;
    
    /// <summary>目标通道</summary>
    public Channel TargetChannel { get; set; } = Channel.A;
    
    /// <summary>是否启用OCR检测</summary>
    public bool EnableOCRDetection { get; set; } = true;
}

/// <summary>对战开始事件参数</summary>
public class BattleStartedEventArgs : EventArgs
{
    public string RoomId { get; set; } = "";
    public int PlayerCount { get; set; }
}

/// <summary>对战结束事件参数</summary>
public class BattleEndedEventArgs : EventArgs
{
    public string RoomId { get; set; } = "";
    public string? WinnerId { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>玩家死亡事件参数</summary>
public class PlayerDeathEventArgs : EventArgs
{
    public string PlayerId { get; set; } = "";
    public int DeathCount { get; set; }
}

/// <summary>惩罚触发事件参数</summary>
public class PunishmentTriggeredEventArgs : EventArgs
{
    public string PlayerId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int Strength { get; set; }
    public int Duration { get; set; }
}

/// <summary>回合状态变化事件参数</summary>
public class RoundStateChangedEventArgs : EventArgs
{
    public RoundState OldState { get; set; }
    public RoundState NewState { get; set; }
}

#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Services;
using Serilog;

namespace ChargingPanel.Core.Network;

/// <summary>
/// 房间服务 - 管理 P2P 房间连接
/// </summary>
public class RoomService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<RoomService>();
    public const int DefaultRoomPort = 49152; // 固定大端口，便于局域网联机发现
    private static RoomService? _instance;
    private static readonly object _lock = new();

    public static RoomService Instance
    {
        get
        {
            lock (_lock)
            {
                _instance ??= new RoomService();
                return _instance;
            }
        }
    }

    private TcpListener? _server;
    private readonly ConcurrentDictionary<string, RoomMember> _members = new();
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
    private readonly RoomArbitrationService _arbitration = new();
    private readonly SemaphoreSlim _discoveryScanGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _discoveryCts;
    private Task? _discoveryTask;
    private Room? _currentRoom;
    private string? _userId;
    private RoomDiscoveryResult[] _lastDiscoveredRooms = Array.Empty<RoomDiscoveryResult>();
    
    public event EventHandler<RoomEventArgs>? RoomCreated;
    public event EventHandler<RoomEventArgs>? RoomJoined;
    public event EventHandler<RoomEventArgs>? RoomLeft;
    public event EventHandler<MemberEventArgs>? MemberJoined;
    public event EventHandler<MemberEventArgs>? MemberLeft;
    public event EventHandler<MessageEventArgs>? MessageReceived;
    public event EventHandler<ControlEventArgs>? ControlCommandReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IReadOnlyList<RoomDiscoveryResult>>? RoomsDiscovered;

    public Room? CurrentRoom => _currentRoom;
    public string? UserId => _userId;
    public IEnumerable<RoomMember> Members => _members.Values;
    public bool IsHost => _currentRoom?.OwnerId == _userId;
    public RoomControlMode ControlMode => _arbitration.Mode;
    public IReadOnlyList<RoomDiscoveryResult> LastDiscoveredRooms => _lastDiscoveredRooms;

    private RoomService()
    {
        _userId = GenerateUserId();
    }

    public void SetControlMode(RoomControlMode mode)
    {
        _arbitration.SetMode(mode);
        StatusChanged?.Invoke(this, $"联机控制模式切换为: {mode}");
    }

    /// <summary>
    /// 启动自动房间发现（程序启动后后台扫描局域网固定端口 49152）。
    /// </summary>
    public async Task StartAutoDiscoveryAsync(int intervalSeconds = 20, int timeoutMs = 220)
    {
        if (_discoveryCts != null)
        {
            return;
        }

        _discoveryCts = new CancellationTokenSource();
        var ct = _discoveryCts.Token;
        _discoveryTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanRoomsAndPublishAsync(timeoutMs, ct);
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Auto room discovery loop error");
                }
            }
        }, ct);

        // 首次立即扫描，确保 UI 首屏可见发现结果
        await ScanRoomsAndPublishAsync(timeoutMs, ct);
    }

    public async Task StopAutoDiscoveryAsync()
    {
        if (_discoveryCts == null)
        {
            return;
        }

        _discoveryCts.Cancel();
        if (_discoveryTask != null)
        {
            try
            {
                await _discoveryTask;
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Stop auto discovery task");
            }
        }

        _discoveryTask = null;
        _discoveryCts.Dispose();
        _discoveryCts = null;
    }

    public async Task<IReadOnlyList<RoomDiscoveryResult>> ScanRoomsAndPublishAsync(
        int timeoutMs = 220,
        CancellationToken ct = default)
    {
        await _discoveryScanGate.WaitAsync(ct);
        try
        {
            var discovered = await DiscoverRoomsAsync(DefaultRoomPort, timeoutMs, ct);
            var snapshot = discovered
                .OrderBy(r => r.RoomName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _lastDiscoveredRooms = snapshot;
            RoomsDiscovered?.Invoke(this, snapshot);
            return snapshot;
        }
        finally
        {
            _discoveryScanGate.Release();
        }
    }

    /// <summary>
    /// 创建房间 (作为主机)
    /// </summary>
    public async Task<Room> CreateRoomAsync(string roomName)
    {
        if (_currentRoom != null)
        {
            throw new InvalidOperationException("Already in a room. Leave first.");
        }

        _cts = new CancellationTokenSource();

        try
        {
            _server = new TcpListener(IPAddress.Any, DefaultRoomPort);
            _server.Start();
        }
        catch (SocketException ex)
        {
            Logger.Warning(ex, "房间固定端口 {Port} 被占用，无法创建房间", DefaultRoomPort);
            throw new InvalidOperationException($"固定房间端口 {DefaultRoomPort} 被占用，请关闭占用该端口的程序后重试。", ex);
        }

        var localIp = GetLocalIPAddress();
        var roomCode = GenerateRoomCode();

        _currentRoom = new Room
        {
            Id = Guid.NewGuid().ToString(),
            Code = roomCode,
            Name = roomName,
            OwnerId = _userId!,
            HostAddress = localIp,
            HostPort = DefaultRoomPort,
            MaxMembers = 10,
            CreatedAt = DateTime.UtcNow
        };

        _arbitration.Reset(RoomControlMode.SingleControl);
        PermissionService.Instance.ClearAll();

        // 添加自己为成员
        var selfMember = new RoomMember
        {
            Id = _userId!,
            Nickname = Environment.MachineName,
            Role = MemberRole.Owner,
            IsOnline = true,
            HasDevice = AppServices.IsInitialized && AppServices.Instance.DeviceManager.GetAllDevices().Any()
        };
        _members[_userId!] = selfMember;

        // 开始监听连接
        _ = AcceptClientsAsync(_cts.Token);

        Logger.Information("Room created: {Code} at {IP}:{Port}", roomCode, localIp, DefaultRoomPort);
        StatusChanged?.Invoke(this, $"房间已创建: {roomCode}");
        RoomCreated?.Invoke(this, new RoomEventArgs(_currentRoom));

        return _currentRoom;
    }

    /// <summary>
    /// 加入房间 (作为客户端)
    /// </summary>
    public async Task<bool> JoinRoomAsync(string hostAddress, int port = DefaultRoomPort)
    {
        if (_currentRoom != null)
        {
            throw new InvalidOperationException("Already in a room. Leave first.");
        }

        try
        {
            _cts = new CancellationTokenSource();
            var client = new TcpClient();
            await client.ConnectAsync(hostAddress, port);

            // 发送加入请求
            var joinRequest = new RoomMessage
            {
                Type = MessageType.JoinRequest,
                SenderId = _userId!,
                Data = JsonSerializer.Serialize(new JoinRequestData
                {
                    Nickname = Environment.MachineName,
                    HasDevice = AppServices.IsInitialized && AppServices.Instance.DeviceManager.GetAllDevices().Any()
                })
            };

            await SendMessageAsync(client, joinRequest);

            // 等待响应
            var response = await ReceiveMessageAsync(client);
            if (response?.Type == MessageType.JoinResponse)
            {
                var responseData = JsonSerializer.Deserialize<JoinResponseData>(response.Data);
                if (responseData?.Success == true)
                {
                    _currentRoom = new Room
                    {
                        Id = responseData.RoomId,
                        Code = responseData.RoomCode,
                        Name = responseData.RoomName,
                        OwnerId = responseData.OwnerId,
                        HostAddress = hostAddress,
                        HostPort = port
                    };

                    _arbitration.Reset(responseData.ControlMode ?? RoomControlMode.SingleControl);
                    PermissionService.Instance.ClearAll();

                    _connections["host"] = client;
                    
                    // 添加自己为成员
                    var selfMember = new RoomMember
                    {
                        Id = _userId!,
                        Nickname = Environment.MachineName,
                        Role = MemberRole.Member,
                        IsOnline = true,
                        HasDevice = AppServices.IsInitialized && AppServices.Instance.DeviceManager.GetAllDevices().Any()
                    };
                    _members[_userId!] = selfMember;

                    // 开始接收消息
                    _ = ReceiveMessagesAsync(client, "host", _cts.Token);

                    Logger.Information("Joined room: {Code}", _currentRoom.Code);
                    StatusChanged?.Invoke(this, $"已加入房间: {_currentRoom.Code}");
                    RoomJoined?.Invoke(this, new RoomEventArgs(_currentRoom));

                    return true;
                }
                else
                {
                    Logger.Warning("Join rejected: {Reason}", responseData?.Reason);
                    StatusChanged?.Invoke(this, $"加入失败: {responseData?.Reason}");
                    client.Close();
                    return false;
                }
            }

            client.Close();
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to join room at {Host}:{Port}", hostAddress, port);
            StatusChanged?.Invoke(this, $"连接失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 离开房间
    /// </summary>
    public async Task LeaveRoomAsync()
    {
        if (_currentRoom == null) return;

        try
        {
            // 通知其他成员
            await BroadcastAsync(new RoomMessage
            {
                Type = MessageType.MemberLeft,
                SenderId = _userId!,
                Data = _userId!
            });
        }
        catch { }

        Cleanup();
        PermissionService.Instance.ClearAll();
        
        var room = _currentRoom;
        _currentRoom = null;
        
        Logger.Information("Left room: {Code}", room?.Code);
        StatusChanged?.Invoke(this, "已离开房间");
        RoomLeft?.Invoke(this, new RoomEventArgs(room!));
    }

    /// <summary>
    /// 发送控制命令
    /// </summary>
    public async Task SendControlCommandAsync(string targetUserId, ControlCommand command)
    {
        if (_currentRoom == null) return;

        EnsureControlCommandEnvelope(command);

        var message = new RoomMessage
        {
            Type = MessageType.ControlCommand,
            SenderId = _userId!,
            TargetId = targetUserId,
            Data = JsonSerializer.Serialize(command)
        };

        if (IsHost)
        {
            await HandleHostControlCommandAsync(message, _userId!, command);
            return;
        }

        await BroadcastAsync(message);
        Logger.Debug("Sent control command to {Target}: {Command}({CommandId})", targetUserId, command.Action, command.CommandId);
    }

    /// <summary>
    /// 广播状态更新
    /// </summary>
    public async Task BroadcastStatusAsync(DeviceStatusUpdate status)
    {
        if (_currentRoom == null) return;

        var message = new RoomMessage
        {
            Type = MessageType.StatusUpdate,
            SenderId = _userId!,
            Data = JsonSerializer.Serialize(status)
        };

        await BroadcastAsync(message);
    }

    /// <summary>
    /// 广播权限角色更新
    /// </summary>
    public async Task BroadcastPermissionRoleAsync(UserPermissionRole role, bool acceptsControl)
    {
        if (_currentRoom == null) return;

        // 更新自己的成员信息
        if (_members.TryGetValue(_userId!, out var myMember))
        {
            myMember.PermissionRole = role;
            myMember.AcceptsControl = acceptsControl;
        }

        var message = new RoomMessage
        {
            Type = MessageType.PermissionUpdate,
            SenderId = _userId!,
            Data = JsonSerializer.Serialize(new PermissionUpdateData
            {
                UserId = _userId!,
                PermissionRole = role,
                AcceptsControl = acceptsControl
            })
        };

        await BroadcastAsync(message);
        Logger.Information("Permission role broadcast: {Role}, AcceptsControl: {Accepts}", role, acceptsControl);
    }

    /// <summary>
    /// 扫描局域网可加入房间（主动探测固定端口）。
    /// </summary>
    public async Task<IReadOnlyList<RoomDiscoveryResult>> DiscoverRoomsAsync(
        int? port = null,
        int timeoutMs = 200,
        CancellationToken ct = default)
    {
        var targetPort = port ?? DefaultRoomPort;
        var localAddresses = GetLocalIPv4Addresses();
        if (localAddresses.Count == 0)
        {
            return Array.Empty<RoomDiscoveryResult>();
        }

        var subnetPrefixes = localAddresses
            .Select(ip =>
            {
                var bytes = ip.GetAddressBytes();
                return bytes.Length == 4 ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}." : string.Empty;
            })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (subnetPrefixes.Count == 0)
        {
            return Array.Empty<RoomDiscoveryResult>();
        }

        var selfIpSet = localAddresses
            .Select(ip => ip.ToString())
            .ToHashSet(StringComparer.Ordinal);

        var semaphore = new SemaphoreSlim(24);
        var results = new ConcurrentBag<RoomDiscoveryResult>();
        var probes = new List<Task>(subnetPrefixes.Count * 254);

        foreach (var subnetPrefix in subnetPrefixes)
        {
            for (var host = 1; host <= 254; host++)
            {
                var address = $"{subnetPrefix}{host}";
                if (selfIpSet.Contains(address))
                {
                    continue;
                }

                probes.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var probe = await ProbeRoomAsync(address, targetPort, timeoutMs, ct);
                        if (probe != null)
                        {
                            results.Add(probe);
                        }
                    }
                    catch
                    {
                        // ignore probe failure
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }
        }

        await Task.WhenAll(probes);
        return results
            .GroupBy(r => $"{r.Address}:{r.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.LatencyMs).First())
            .OrderBy(r => r.Address, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<RoomDiscoveryResult?> ProbeRoomAsync(string address, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        var start = DateTime.UtcNow;
        await client.ConnectAsync(address, port, timeoutCts.Token);
        var latencyMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;

        var ping = new RoomMessage
        {
            Type = MessageType.DiscoveryPing,
            SenderId = _userId ?? "probe",
            Data = string.Empty
        };
        await SendMessageAsync(client, ping);

        var response = await ReceiveMessageAsync(client);
        if (response?.Type != MessageType.DiscoveryPong)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<DiscoveryPongData>(response.Data);
        if (payload == null)
        {
            return null;
        }

        return new RoomDiscoveryResult
        {
            Address = address,
            Port = port,
            RoomId = payload.RoomId,
            RoomCode = payload.RoomCode,
            RoomName = payload.RoomName,
            OwnerId = payload.OwnerId,
            MemberCount = payload.MemberCount,
            LatencyMs = latencyMs
        };
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _server != null)
        {
            try
            {
                var client = await _server.AcceptTcpClientAsync(ct);
                var clientId = Guid.NewGuid().ToString();
                _connections[clientId] = client;
                
                Logger.Debug("Client connected: {Id}", clientId);
                _ = HandleClientAsync(client, clientId, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Error accepting client");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, string clientId, CancellationToken ct)
    {
        var connectionKey = clientId;
        try
        {
            // 等待加入请求
            var message = await ReceiveMessageAsync(client);
            if (message?.Type == MessageType.DiscoveryPing)
            {
                await SendMessageAsync(client, new RoomMessage
                {
                    Type = MessageType.DiscoveryPong,
                    SenderId = _userId!,
                    Data = JsonSerializer.Serialize(new DiscoveryPongData
                    {
                        RoomId = _currentRoom?.Id ?? string.Empty,
                        RoomCode = _currentRoom?.Code ?? string.Empty,
                        RoomName = _currentRoom?.Name ?? string.Empty,
                        OwnerId = _currentRoom?.OwnerId ?? string.Empty,
                        MemberCount = _members.Count
                    })
                });
                return;
            }

            if (message?.Type == MessageType.JoinRequest)
            {
                var joinData = JsonSerializer.Deserialize<JoinRequestData>(message.Data);
                
                // 添加成员
                var member = new RoomMember
                {
                    Id = message.SenderId,
                    Nickname = joinData?.Nickname ?? "Unknown",
                    Role = MemberRole.Member,
                    IsOnline = true,
                    HasDevice = joinData?.HasDevice ?? false
                };
                _members[message.SenderId] = member;
                _connections.TryRemove(clientId, out _);
                _connections[message.SenderId] = client;
                connectionKey = message.SenderId;

                // 发送成功响应
                await SendMessageAsync(client, new RoomMessage
                {
                    Type = MessageType.JoinResponse,
                    Data = JsonSerializer.Serialize(new JoinResponseData
                    {
                        Success = true,
                        RoomId = _currentRoom!.Id,
                        RoomCode = _currentRoom.Code,
                        RoomName = _currentRoom.Name,
                        OwnerId = _currentRoom.OwnerId,
                        ControlMode = _arbitration.Mode
                    })
                });

                // 通知其他成员
                await BroadcastAsync(new RoomMessage
                {
                    Type = MessageType.MemberJoined,
                    SenderId = _userId!,
                    Data = JsonSerializer.Serialize(member)
                });

                Logger.Information("Member joined: {Nickname}", member.Nickname);
                MemberJoined?.Invoke(this, new MemberEventArgs(member));

                // 开始接收消息
                await ReceiveMessagesAsync(client, message.SenderId, ct);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Error handling client {Id}", clientId);
        }
        finally
        {
            _connections.TryRemove(connectionKey, out _);
            client.Close();
        }
    }

    private async Task ReceiveMessagesAsync(TcpClient client, string senderId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var message = await ReceiveMessageAsync(client);
                if (message == null) break;

                await HandleMessageAsync(message, senderId);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Connection lost: {Id}", senderId);
        }
        finally
        {
            if (_members.TryRemove(senderId, out var member))
            {
                MemberLeft?.Invoke(this, new MemberEventArgs(member));
                await BroadcastAsync(new RoomMessage
                {
                    Type = MessageType.MemberLeft,
                    SenderId = _userId!,
                    Data = senderId
                });
            }
        }
    }

    private async Task HandleMessageAsync(RoomMessage message, string senderId)
    {
        switch (message.Type)
        {
            case MessageType.DiscoveryPing:
            case MessageType.DiscoveryPong:
                // 发现报文仅用于建连前探测，不进入房间消息链路
                break;

            case MessageType.ControlCommand:
                var command = JsonSerializer.Deserialize<ControlCommand>(message.Data);
                if (command == null)
                {
                    break;
                }

                EnsureControlCommandEnvelope(command);

                if (message.TargetId == _userId)
                {
                    // 处理权限相关命令
                    await HandlePermissionCommandAsync(senderId, command);
                    ControlCommandReceived?.Invoke(this, new ControlEventArgs(senderId, command));
                }
                else if (IsHost)
                {
                    await HandleHostControlCommandAsync(message, senderId, command);
                }
                break;

            case MessageType.StatusUpdate:
                var status = JsonSerializer.Deserialize<DeviceStatusUpdate>(message.Data);
                if (status != null && _members.TryGetValue(message.SenderId, out var member))
                {
                    member.LastStatus = status;
                }
                if (IsHost)
                {
                    await BroadcastAsync(message, exclude: senderId);
                }
                break;

            case MessageType.MemberJoined:
                var newMember = JsonSerializer.Deserialize<RoomMember>(message.Data);
                if (newMember != null)
                {
                    _members[newMember.Id] = newMember;
                    MemberJoined?.Invoke(this, new MemberEventArgs(newMember));
                }
                break;

            case MessageType.MemberLeft:
                var leftId = message.Data;
                if (_members.TryRemove(leftId, out var leftMember))
                {
                    MemberLeft?.Invoke(this, new MemberEventArgs(leftMember));
                    // 清除相关权限
                    PermissionService.Instance.HandlePermissionRevoked(leftId);
                }
                break;

            case MessageType.Chat:
                MessageReceived?.Invoke(this, new MessageEventArgs(senderId, message.Data));
                if (IsHost)
                {
                    await BroadcastAsync(message, exclude: senderId);
                }
                break;
                
            case MessageType.PermissionUpdate:
                var permUpdate = JsonSerializer.Deserialize<PermissionUpdateData>(message.Data);
                if (permUpdate != null && _members.TryGetValue(permUpdate.UserId, out var updatedMember))
                {
                    updatedMember.PermissionRole = permUpdate.PermissionRole;
                    updatedMember.AcceptsControl = permUpdate.AcceptsControl;
                }
                if (IsHost)
                {
                    await BroadcastAsync(message, exclude: senderId);
                }
                break;
        }
    }

    /// <summary>
    /// 处理权限相关的控制命令
    /// </summary>
    private async Task HandlePermissionCommandAsync(string senderId, ControlCommand command)
    {
        switch (command.Action)
        {
            case "permission_request":
                var request = JsonSerializer.Deserialize<PermissionRequest>(command.WaveformData!);
                if (request != null)
                {
                    PermissionService.Instance.HandlePermissionRequest(request);
                }
                break;
                
            case "permission_response":
                var response = JsonSerializer.Deserialize<PermissionResponse>(command.WaveformData!);
                if (response != null)
                {
                    PermissionService.Instance.HandlePermissionResponse(response);
                }
                break;
                
            case "permission_revoked":
                PermissionService.Instance.HandlePermissionRevoked(senderId);
                break;
        }
    }

    private async Task HandleHostControlCommandAsync(RoomMessage message, string senderId, ControlCommand command)
    {
        var targetId = message.TargetId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        if (IsPermissionAction(command.Action))
        {
            await ForwardToMemberAsync(targetId, message);
            return;
        }

        if (!_members.TryGetValue(targetId, out var targetMember) || !targetMember.IsOnline)
        {
            await SendControlResultAsync(
                senderId,
                command,
                false,
                RoomArbitrationReason.TargetOffline,
                "目标用户不在线");
            return;
        }

        var permission = CanSenderIssueControl(senderId, targetId, targetMember, command);
        if (!permission.allowed)
        {
            await SendControlResultAsync(
                senderId,
                command,
                false,
                RoomArbitrationReason.PermissionDenied,
                permission.reason);
            return;
        }

        var request = new RoomArbitrationRequest(
            senderId,
            targetId,
            command.Action,
            command.CommandId,
            command.Priority,
            command.GetLeaseTtl());
        var decision = _arbitration.Evaluate(request);
        if (!decision.Allowed)
        {
            await SendControlResultAsync(senderId, command, false, decision.Reason, decision.Message);
            return;
        }

        await ForwardToMemberAsync(targetId, message);
        await SendControlResultAsync(senderId, command, true, decision.Reason, decision.Message);
    }

    private (bool allowed, string reason) CanSenderIssueControl(
        string senderId,
        string targetId,
        RoomMember targetMember,
        ControlCommand command)
    {
        if (string.Equals(senderId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "自控命令放行");
        }

        if (string.Equals(senderId, _userId, StringComparison.OrdinalIgnoreCase))
        {
            return (true, "房主本地命令放行");
        }

        if (!_members.TryGetValue(senderId, out var senderMember) || !senderMember.IsOnline)
        {
            return (false, "发送者不在线");
        }

        if (senderMember.PermissionRole != UserPermissionRole.Controller)
        {
            return (false, "发送者不是控制者角色");
        }

        if (targetMember.PermissionRole == UserPermissionRole.Controlled && !targetMember.AcceptsControl)
        {
            return (false, "目标未开启接受控制");
        }

        return (true, "权限检查通过");
    }

    private static bool IsPermissionAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        return action.StartsWith("permission_", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendControlResultAsync(
        string requesterId,
        ControlCommand sourceCommand,
        bool success,
        RoomArbitrationReason reason,
        string message)
    {
        var response = new ControlCommand
        {
            Action = success ? "control_ack" : "control_deny",
            CommandId = sourceCommand.CommandId,
            CorrelationId = sourceCommand.CommandId,
            RequireAck = false,
            WaveformData = JsonSerializer.Serialize(new ControlCommandResult
            {
                Success = success,
                Reason = reason.ToString(),
                Message = message,
                SourceAction = sourceCommand.Action,
                SourceCommandId = sourceCommand.CommandId,
                AtUtc = DateTime.UtcNow
            })
        };

        EnsureControlCommandEnvelope(response);

        if (string.Equals(requesterId, _userId, StringComparison.OrdinalIgnoreCase))
        {
            StatusChanged?.Invoke(this, $"{(success ? "仲裁通过" : "仲裁拒绝")}：{message}");
            return;
        }

        var responseMessage = new RoomMessage
        {
            Type = MessageType.ControlCommand,
            SenderId = _userId!,
            TargetId = requesterId,
            Data = JsonSerializer.Serialize(response)
        };

        await ForwardToMemberAsync(requesterId, responseMessage);
    }

    private static void EnsureControlCommandEnvelope(ControlCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            command.CommandId = Guid.NewGuid().ToString("N");
        }

        if (command.IssuedAtUtc == default)
        {
            command.IssuedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task BroadcastAsync(RoomMessage message, string? exclude = null)
    {
        var tasks = new List<Task>();
        foreach (var (id, client) in _connections)
        {
            if (id != exclude && client.Connected)
            {
                tasks.Add(SendMessageAsync(client, message));
            }
        }
        await Task.WhenAll(tasks);
    }

    private async Task ForwardToMemberAsync(string targetId, RoomMessage message)
    {
        if (_connections.TryGetValue(targetId, out var client) && client.Connected)
        {
            await SendMessageAsync(client, message);
        }
    }

    private async Task SendMessageAsync(TcpClient client, RoomMessage message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);
            var lengthBytes = BitConverter.GetBytes(data.Length);
            
            var stream = client.GetStream();
            await stream.WriteAsync(lengthBytes);
            await stream.WriteAsync(data);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send message");
        }
    }

    private async Task<RoomMessage?> ReceiveMessageAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var lengthBytes = new byte[4];
            var read = await stream.ReadAsync(lengthBytes, 0, 4);
            if (read < 4) return null;

            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > 1024 * 1024) return null;

            var data = new byte[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                read = await stream.ReadAsync(data, totalRead, length - totalRead);
                if (read <= 0) return null;
                totalRead += read;
            }

            var json = Encoding.UTF8.GetString(data);
            return JsonSerializer.Deserialize<RoomMessage>(json);
        }
        catch
        {
            return null;
        }
    }

    private void Cleanup()
    {
        _discoveryCts?.Cancel();
        try
        {
            _discoveryTask?.Wait(1000);
        }
        catch
        {
            // ignore
        }
        _discoveryTask = null;
        _discoveryCts?.Dispose();
        _discoveryCts = null;

        _cts?.Cancel();
        _server?.Stop();
        _server = null;

        foreach (var client in _connections.Values)
        {
            try { client.Close(); } catch { }
        }
        _connections.Clear();
        _members.Clear();
        _arbitration.Reset(RoomControlMode.SingleControl);
        PermissionService.Instance.ClearAll();
    }

    private static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 6).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    private static string GenerateUserId()
    {
        return $"user_{Environment.MachineName}_{Guid.NewGuid():N}"[..32];
    }

    private static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        var results = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var ipProperties = nic.GetIPProperties();
            foreach (var unicast in ipProperties.UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                results.Add(ip);
            }
        }

        return results;
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Dispose()
    {
        Cleanup();
    }
}

#region Models

public class Room
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string HostAddress { get; set; } = "";
    public int HostPort { get; set; }
    public int MaxMembers { get; set; } = 10;
    public DateTime CreatedAt { get; set; }
}

public class RoomMember
{
    public string Id { get; set; } = "";
    public string Nickname { get; set; } = "";
    public MemberRole Role { get; set; } = MemberRole.Member;
    public UserPermissionRole PermissionRole { get; set; } = UserPermissionRole.Controller;
    public bool IsOnline { get; set; }
    public bool HasDevice { get; set; }
    public bool AcceptsControl { get; set; } // 是否接受控制
    public DeviceStatusUpdate? LastStatus { get; set; }
}

public enum MemberRole
{
    Owner,
    Admin,
    Member,
    Observer
}

public class RoomMessage
{
    public MessageType Type { get; set; }
    public string SenderId { get; set; } = "";
    public string? TargetId { get; set; }
    public string Data { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum MessageType
{
    DiscoveryPing,
    DiscoveryPong,
    JoinRequest,
    JoinResponse,
    MemberJoined,
    MemberLeft,
    ControlCommand,
    StatusUpdate,
    Chat,
    PermissionUpdate
}

public class JoinRequestData
{
    public string Nickname { get; set; } = "";
    public bool HasDevice { get; set; }
}

public class JoinResponseData
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
    public string RoomId { get; set; } = "";
    public string RoomCode { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public RoomControlMode? ControlMode { get; set; }
}

public class DiscoveryPongData
{
    public string RoomId { get; set; } = "";
    public string RoomCode { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public int MemberCount { get; set; }
}

public class RoomDiscoveryResult
{
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string RoomId { get; set; } = "";
    public string RoomCode { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public int MemberCount { get; set; }
    public int LatencyMs { get; set; }
}

public class ControlCommand
{
    public string Action { get; set; } = ""; // set_strength, send_waveform, clear_queue
    public string Channel { get; set; } = "A";
    public int? Value { get; set; }
    public string? WaveformData { get; set; }
    public string? CommandId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public bool RequireAck { get; set; }
    public int Priority { get; set; }
    public int? LeaseTtlSeconds { get; set; }
    public string? Metadata { get; set; }

    public TimeSpan? GetLeaseTtl()
    {
        if (!LeaseTtlSeconds.HasValue)
        {
            return null;
        }

        if (LeaseTtlSeconds.Value <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(LeaseTtlSeconds.Value);
    }
}

public class ControlCommandResult
{
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? SourceAction { get; set; }
    public string? SourceCommandId { get; set; }
    public DateTime AtUtc { get; set; }
}

public class DeviceStatusUpdate
{
    public string? DeviceId { get; set; }
    public string? DeviceType { get; set; }
    public bool IsConnected { get; set; }
    public int StrengthA { get; set; }
    public int StrengthB { get; set; }
    public int? CurrentHP { get; set; }
    public int? MaxHP { get; set; }
}

public class PermissionUpdateData
{
    public string UserId { get; set; } = "";
    public UserPermissionRole PermissionRole { get; set; }
    public bool AcceptsControl { get; set; }
}

#endregion

#region Event Args

public class RoomEventArgs : EventArgs
{
    public Room Room { get; }
    public RoomEventArgs(Room room) => Room = room;
}

public class MemberEventArgs : EventArgs
{
    public RoomMember Member { get; }
    public MemberEventArgs(RoomMember member) => Member = member;
}

public class MessageEventArgs : EventArgs
{
    public string SenderId { get; }
    public string Message { get; }
    public MessageEventArgs(string senderId, string message)
    {
        SenderId = senderId;
        Message = message;
    }
}

public class ControlEventArgs : EventArgs
{
    public string SenderId { get; }
    public ControlCommand Command { get; }
    public ControlEventArgs(string senderId, ControlCommand command)
    {
        SenderId = senderId;
        Command = command;
    }
}

#endregion

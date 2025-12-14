using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace ChargingPanel.Core.Network;

/// <summary>
/// 房间服务 - 管理 P2P 房间连接
/// </summary>
public class RoomService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<RoomService>();
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
    private CancellationTokenSource? _cts;
    private Room? _currentRoom;
    private string? _userId;
    
    public event EventHandler<RoomEventArgs>? RoomCreated;
    public event EventHandler<RoomEventArgs>? RoomJoined;
    public event EventHandler<RoomEventArgs>? RoomLeft;
    public event EventHandler<MemberEventArgs>? MemberJoined;
    public event EventHandler<MemberEventArgs>? MemberLeft;
    public event EventHandler<MessageEventArgs>? MessageReceived;
    public event EventHandler<ControlEventArgs>? ControlCommandReceived;
    public event EventHandler<string>? StatusChanged;

    public Room? CurrentRoom => _currentRoom;
    public string? UserId => _userId;
    public IEnumerable<RoomMember> Members => _members.Values;
    public bool IsHost => _currentRoom?.OwnerId == _userId;

    private RoomService()
    {
        _userId = GenerateUserId();
    }

    /// <summary>
    /// 创建房间 (作为主机)
    /// </summary>
    public async Task<Room> CreateRoomAsync(string roomName, int port = 0, string? password = null)
    {
        if (_currentRoom != null)
        {
            throw new InvalidOperationException("Already in a room. Leave first.");
        }

        _cts = new CancellationTokenSource();
        
        // 选择端口 (0 = 自动选择)
        if (port == 0)
        {
            port = GetAvailablePort();
        }

        _server = new TcpListener(IPAddress.Any, port);
        _server.Start();

        var localIp = GetLocalIPAddress();
        var roomCode = GenerateRoomCode();

        _currentRoom = new Room
        {
            Id = Guid.NewGuid().ToString(),
            Code = roomCode,
            Name = roomName,
            OwnerId = _userId!,
            Password = password,
            HostAddress = localIp,
            HostPort = port,
            MaxMembers = 10,
            CreatedAt = DateTime.UtcNow
        };

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

        Logger.Information("Room created: {Code} at {IP}:{Port}", roomCode, localIp, port);
        StatusChanged?.Invoke(this, $"房间已创建: {roomCode}");
        RoomCreated?.Invoke(this, new RoomEventArgs(_currentRoom));

        return _currentRoom;
    }

    /// <summary>
    /// 加入房间 (作为客户端)
    /// </summary>
    public async Task<bool> JoinRoomAsync(string hostAddress, int port, string? password = null)
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
                    Password = password,
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

        var message = new RoomMessage
        {
            Type = MessageType.ControlCommand,
            SenderId = _userId!,
            TargetId = targetUserId,
            Data = JsonSerializer.Serialize(command)
        };

        await BroadcastAsync(message);
        Logger.Debug("Sent control command to {Target}: {Command}", targetUserId, command.Action);
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
        try
        {
            // 等待加入请求
            var message = await ReceiveMessageAsync(client);
            if (message?.Type == MessageType.JoinRequest)
            {
                var joinData = JsonSerializer.Deserialize<JoinRequestData>(message.Data);
                
                // 验证密码
                if (!string.IsNullOrEmpty(_currentRoom?.Password) && 
                    _currentRoom.Password != joinData?.Password)
                {
                    await SendMessageAsync(client, new RoomMessage
                    {
                        Type = MessageType.JoinResponse,
                        Data = JsonSerializer.Serialize(new JoinResponseData
                        {
                            Success = false,
                            Reason = "密码错误"
                        })
                    });
                    client.Close();
                    _connections.TryRemove(clientId, out _);
                    return;
                }

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
                        OwnerId = _currentRoom.OwnerId
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
            _connections.TryRemove(clientId, out _);
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
            case MessageType.ControlCommand:
                if (message.TargetId == _userId)
                {
                    var command = JsonSerializer.Deserialize<ControlCommand>(message.Data);
                    if (command != null)
                    {
                        // 处理权限相关命令
                        await HandlePermissionCommandAsync(senderId, command);
                        
                        // 触发控制命令事件
                        ControlCommandReceived?.Invoke(this, new ControlEventArgs(senderId, command));
                    }
                }
                else if (IsHost)
                {
                    // 转发给目标
                    await ForwardToMemberAsync(message.TargetId!, message);
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
        _cts?.Cancel();
        _server?.Stop();
        _server = null;

        foreach (var client in _connections.Values)
        {
            try { client.Close(); } catch { }
        }
        _connections.Clear();
        _members.Clear();
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

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
    public string? Password { get; set; }
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
    public string? Password { get; set; }
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
}

public class ControlCommand
{
    public string Action { get; set; } = ""; // set_strength, send_waveform, clear_queue
    public string Channel { get; set; } = "A";
    public int? Value { get; set; }
    public string? WaveformData { get; set; }
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

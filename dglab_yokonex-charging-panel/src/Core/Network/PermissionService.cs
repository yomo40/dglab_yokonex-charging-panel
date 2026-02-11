using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ChargingPanel.Core.Network;

/// <summary>
/// 权限管理服务
/// </summary>
public class PermissionService
{
    private static PermissionService? _instance;
    private static readonly object _lock = new();

    public static PermissionService Instance
    {
        get
        {
            lock (_lock)
            {
                _instance ??= new PermissionService();
                return _instance;
            }
        }
    }

    // 用户权限状态
    private UserPermissionRole _myRole = UserPermissionRole.Controller;
    
    // 授权关系: 被控者ID -> 控制者ID列表
    private readonly ConcurrentDictionary<string, HashSet<string>> _controlPermissions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _forcedControlPermissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _forcedControlLocks = new(StringComparer.OrdinalIgnoreCase);
    
    // 待处理的权限请求
    private readonly ConcurrentDictionary<string, PermissionRequest> _pendingRequests = new();
    
    public event EventHandler<PermissionRequestEventArgs>? PermissionRequested;
    public event EventHandler<PermissionGrantedEventArgs>? PermissionGranted;
    public event EventHandler<PermissionRevokedEventArgs>? PermissionRevoked;
    public event EventHandler<UserPermissionRole>? RoleChanged;

    /// <summary>
    /// 当前用户角色
    /// </summary>
    public UserPermissionRole MyRole
    {
        get => _myRole;
        set
        {
            if (IsControlLockedForCurrentUser() && value != UserPermissionRole.Controlled)
            {
                return;
            }

            if (_myRole != value)
            {
                _myRole = value;
                RoleChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// 是否可以被控制
    /// </summary>
    public bool CanBeControlled =>
        _myRole == UserPermissionRole.Controlled || HasForcedController(RoomService.Instance.UserId);

    /// <summary>
    /// 是否可以控制他人
    /// </summary>
    public bool CanControlOthers => _myRole == UserPermissionRole.Controller;

    public bool IsControlLockedForCurrentUser()
    {
        var userId = RoomService.Instance.UserId;
        return !string.IsNullOrWhiteSpace(userId) && _forcedControlLocks.ContainsKey(userId);
    }

    public bool TrySetMyRole(UserPermissionRole role)
    {
        if (IsControlLockedForCurrentUser() && role != UserPermissionRole.Controlled)
        {
            return false;
        }

        MyRole = role;
        return true;
    }

    public bool CanReceiveControlFrom(string controllerId)
    {
        if (string.IsNullOrWhiteSpace(controllerId))
        {
            return false;
        }

        var myId = RoomService.Instance.UserId;
        if (string.IsNullOrWhiteSpace(myId))
        {
            return false;
        }

        if (IsForcedController(controllerId, myId))
        {
            return true;
        }

        if (_myRole != UserPermissionRole.Controlled)
        {
            return false;
        }

        if (_controlPermissions.TryGetValue(myId, out var controllers))
        {
            return controllers.Contains(controllerId);
        }

        return false;
    }

    /// <summary>
    /// 请求控制某用户
    /// </summary>
    public async Task<bool> RequestControlAsync(string targetUserId)
    {
        if (!CanControlOthers)
            return false;

        var roomService = RoomService.Instance;
        if (roomService.CurrentRoom == null)
            return false;

        var request = new PermissionRequest
        {
            Id = Guid.NewGuid().ToString(),
            RequesterId = roomService.UserId!,
            TargetId = targetUserId,
            Type = PermissionType.Control,
            Status = PermissionRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        // 发送请求消息
        await roomService.SendControlCommandAsync(targetUserId, new ControlCommand
        {
            Action = "permission_request",
            WaveformData = System.Text.Json.JsonSerializer.Serialize(request)
        });

        _pendingRequests[request.Id] = request;
        return true;
    }

    public void ForceGrantControl(string controllerId, string controlledId, bool lockControlled = false)
    {
        if (string.IsNullOrWhiteSpace(controllerId) || string.IsNullOrWhiteSpace(controlledId))
        {
            return;
        }

        var controllers = _forcedControlPermissions.GetOrAdd(
            controlledId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        controllers[controllerId] = 0;

        if (lockControlled)
        {
            _forcedControlLocks[controlledId] = controllerId;
        }

        var myId = RoomService.Instance.UserId;
        if (string.Equals(myId, controlledId, StringComparison.OrdinalIgnoreCase) &&
            _myRole != UserPermissionRole.Controlled)
        {
            _myRole = UserPermissionRole.Controlled;
            RoleChanged?.Invoke(this, _myRole);
        }

        PermissionGranted?.Invoke(this, new PermissionGrantedEventArgs(controllerId, controlledId));
    }

    public void ApplyPvpControlLock(string controllerId, string controlledId, bool lockControlled)
    {
        if (string.IsNullOrWhiteSpace(controllerId) || string.IsNullOrWhiteSpace(controlledId))
        {
            return;
        }

        if (!lockControlled)
        {
            if (_forcedControlPermissions.TryGetValue(controlledId, out var controllers))
            {
                controllers.TryRemove(controllerId, out _);
                if (controllers.IsEmpty)
                {
                    _forcedControlPermissions.TryRemove(controlledId, out _);
                }
            }

            if (_forcedControlLocks.TryGetValue(controlledId, out var lockOwner) &&
                string.Equals(lockOwner, controllerId, StringComparison.OrdinalIgnoreCase))
            {
                _forcedControlLocks.TryRemove(controlledId, out _);
            }
            return;
        }

        ForceGrantControl(controllerId, controlledId, lockControlled: true);
    }

    /// <summary>
    /// 处理收到的权限请求
    /// </summary>
    public void HandlePermissionRequest(PermissionRequest request)
    {
        if (!CanBeControlled)
        {
            // 自动拒绝 - 不是被控角色
            _ = RespondToRequestAsync(request.Id, false, "用户不接受控制");
            return;
        }

        _pendingRequests[request.Id] = request;
        PermissionRequested?.Invoke(this, new PermissionRequestEventArgs(request));
    }

    /// <summary>
    /// 响应权限请求
    /// </summary>
    public async Task RespondToRequestAsync(string requestId, bool grant, string? reason = null)
    {
        if (!_pendingRequests.TryRemove(requestId, out var request))
            return;

        var roomService = RoomService.Instance;
        if (roomService.CurrentRoom == null)
            return;

        if (grant)
        {
            // 授予权限
            var controllers = _controlPermissions.GetOrAdd(roomService.UserId!, _ => new HashSet<string>());
            controllers.Add(request.RequesterId);
            
            PermissionGranted?.Invoke(this, new PermissionGrantedEventArgs(request.RequesterId, roomService.UserId!));
        }

        // 发送响应
        await roomService.SendControlCommandAsync(request.RequesterId, new ControlCommand
        {
            Action = "permission_response",
            WaveformData = System.Text.Json.JsonSerializer.Serialize(new PermissionResponse
            {
                RequestId = requestId,
                Granted = grant,
                Reason = reason
            })
        });
    }

    /// <summary>
    /// 处理权限响应
    /// </summary>
    public void HandlePermissionResponse(PermissionResponse response)
    {
        if (_pendingRequests.TryRemove(response.RequestId, out var request))
        {
            if (response.Granted)
            {
                // 记录已获得的控制权限
                var controlled = _controlPermissions.GetOrAdd(request.TargetId, _ => new HashSet<string>());
                controlled.Add(RoomService.Instance.UserId!);
                
                PermissionGranted?.Invoke(this, new PermissionGrantedEventArgs(RoomService.Instance.UserId!, request.TargetId));
            }
        }
    }

    /// <summary>
    /// 撤销控制权限
    /// </summary>
    public async Task RevokeControlAsync(string controllerId)
    {
        var roomService = RoomService.Instance;
        if (roomService.CurrentRoom == null)
            return;

        if (_forcedControlLocks.TryGetValue(roomService.UserId!, out var lockOwner) &&
            string.Equals(lockOwner, controllerId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_controlPermissions.TryGetValue(roomService.UserId!, out var controllers))
        {
            controllers.Remove(controllerId);
        }

        // 通知对方
        await roomService.SendControlCommandAsync(controllerId, new ControlCommand
        {
            Action = "permission_revoked",
            WaveformData = roomService.UserId
        });

        PermissionRevoked?.Invoke(this, new PermissionRevokedEventArgs(controllerId, roomService.UserId!));
    }

    /// <summary>
    /// 处理权限撤销
    /// </summary>
    public void HandlePermissionRevoked(string targetUserId)
    {
        if (_controlPermissions.TryGetValue(targetUserId, out var controllers))
        {
            controllers.Remove(RoomService.Instance.UserId!);
        }

        if (_forcedControlPermissions.TryGetValue(targetUserId, out var forcedControllers))
        {
            forcedControllers.TryRemove(RoomService.Instance.UserId!, out _);
            if (forcedControllers.IsEmpty)
            {
                _forcedControlPermissions.TryRemove(targetUserId, out _);
            }
        }

        if (_forcedControlLocks.TryGetValue(targetUserId, out var lockOwner) &&
            string.Equals(lockOwner, RoomService.Instance.UserId, StringComparison.OrdinalIgnoreCase))
        {
            _forcedControlLocks.TryRemove(targetUserId, out _);
        }

        PermissionRevoked?.Invoke(this, new PermissionRevokedEventArgs(RoomService.Instance.UserId!, targetUserId));
    }

    /// <summary>
    /// 检查是否有权限控制某用户
    /// </summary>
    public bool HasControlPermission(string targetUserId)
    {
        var myId = RoomService.Instance.UserId;
        if (string.IsNullOrWhiteSpace(myId))
        {
            return false;
        }

        if (IsForcedController(myId, targetUserId))
        {
            return true;
        }

        if (!CanControlOthers)
            return false;

        if (_controlPermissions.TryGetValue(targetUserId, out var controllers))
        {
            return controllers.Contains(myId);
        }

        return false;
    }

    /// <summary>
    /// 获取我授权的控制者列表
    /// </summary>
    public IEnumerable<string> GetMyControllers()
    {
        var myId = RoomService.Instance.UserId;
        if (string.IsNullOrWhiteSpace(myId))
        {
            return Enumerable.Empty<string>();
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_controlPermissions.TryGetValue(myId, out var controllers))
        {
            foreach (var controllerId in controllers)
            {
                result.Add(controllerId);
            }
        }

        if (_forcedControlPermissions.TryGetValue(myId, out var forcedControllers))
        {
            foreach (var controllerId in forcedControllers.Keys)
            {
                result.Add(controllerId);
            }
        }

        return result;
    }

    /// <summary>
    /// 获取我可以控制的用户列表
    /// </summary>
    public IEnumerable<string> GetMyControlledUsers()
    {
        var myId = RoomService.Instance.UserId;
        if (string.IsNullOrWhiteSpace(myId))
        {
            return Enumerable.Empty<string>();
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetId in _controlPermissions
            .Where(kvp => kvp.Value.Contains(myId!))
            .Select(kvp => kvp.Key))
        {
            result.Add(targetId);
        }

        foreach (var kvp in _forcedControlPermissions)
        {
            if (kvp.Value.ContainsKey(myId))
            {
                result.Add(kvp.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// 清除所有权限（离开房间时）
    /// </summary>
    public void ClearAll()
    {
        _controlPermissions.Clear();
        _forcedControlPermissions.Clear();
        _forcedControlLocks.Clear();
        _pendingRequests.Clear();
        _myRole = UserPermissionRole.Controller;
    }

    private bool HasForcedController(string? controlledId)
    {
        if (string.IsNullOrWhiteSpace(controlledId))
        {
            return false;
        }

        return _forcedControlPermissions.TryGetValue(controlledId, out var controllers) &&
               !controllers.IsEmpty;
    }

    private bool IsForcedController(string controllerId, string controlledId)
    {
        return _forcedControlPermissions.TryGetValue(controlledId, out var controllers) &&
               controllers.ContainsKey(controllerId);
    }
}

#region Enums and Models

/// <summary>
/// 用户权限角色
/// </summary>
public enum UserPermissionRole
{
    /// <summary>控制者 - 可以发送控制指令</summary>
    Controller,
    /// <summary>被控者 - 接收并执行控制指令</summary>
    Controlled,
    /// <summary>观察者 - 只能观看状态</summary>
    Observer
}

/// <summary>
/// 权限类型
/// </summary>
public enum PermissionType
{
    Control,    // 控制权限
    View        // 查看权限
}

/// <summary>
/// 权限请求状态
/// </summary>
public enum PermissionRequestStatus
{
    Pending,
    Granted,
    Denied,
    Expired
}

/// <summary>
/// 权限请求
/// </summary>
public class PermissionRequest
{
    public string Id { get; set; } = "";
    public string RequesterId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public PermissionType Type { get; set; }
    public PermissionRequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}

/// <summary>
/// 权限响应
/// </summary>
public class PermissionResponse
{
    public string RequestId { get; set; } = "";
    public bool Granted { get; set; }
    public string? Reason { get; set; }
}

#endregion

#region Event Args

public class PermissionRequestEventArgs : EventArgs
{
    public PermissionRequest Request { get; }
    public PermissionRequestEventArgs(PermissionRequest request) => Request = request;
}

public class PermissionGrantedEventArgs : EventArgs
{
    public string ControllerId { get; }
    public string ControlledId { get; }
    public PermissionGrantedEventArgs(string controllerId, string controlledId)
    {
        ControllerId = controllerId;
        ControlledId = controlledId;
    }
}

public class PermissionRevokedEventArgs : EventArgs
{
    public string ControllerId { get; }
    public string ControlledId { get; }
    public PermissionRevokedEventArgs(string controllerId, string controlledId)
    {
        ControllerId = controllerId;
        ControlledId = controlledId;
    }
}

#endregion

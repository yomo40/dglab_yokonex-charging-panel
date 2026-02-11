using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using ChargingPanel.Core.Events;
using ChargingPanel.Core.Network;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class RoomPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<RoomPage>();
    private readonly Dictionary<string, PermissionRequest> _pendingRequests = new();
    private IDisposable? _gameEventSubscription;
    private readonly SemaphoreSlim _pvpStateGate = new(1, 1);
    private bool _isPvpActive = false;
    private string? _pvpOpponentId = null;
    private bool _pvpResultCommitted = false;
    private DateTime _lastPvpDamagePunishAtUtc = DateTime.MinValue;
    private bool _isRefreshingDiscovery = false;

    public RoomPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 订阅房间服务事件
        var roomService = RoomService.Instance;
        roomService.RoomCreated += OnRoomCreated;
        roomService.RoomJoined += OnRoomJoined;
        roomService.RoomLeft += OnRoomLeft;
        roomService.MemberJoined += OnMemberJoined;
        roomService.MemberLeft += OnMemberLeft;
        roomService.StatusChanged += OnStatusChanged;
        roomService.ControlCommandReceived += OnControlCommandReceived;
        roomService.RoomsDiscovered += OnRoomsDiscovered;
        
        // 订阅权限服务事件
        var permService = PermissionService.Instance;
        permService.PermissionRequested += OnPermissionRequested;
        permService.PermissionGranted += OnPermissionGranted;
        permService.PermissionRevoked += OnPermissionRevoked;
        permService.RoleChanged += OnMyRoleChanged;

        _gameEventSubscription = EventBus.Instance.Subscribe<GameEvent>(OnGameEventReceived);

        RefreshDiscoveredRoomsList(roomService.LastDiscoveredRooms);
        _ = RefreshDiscoveredRoomsAsync();

        UpdateUI();
        UpdatePermissionUI();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        var roomService = RoomService.Instance;
        roomService.RoomCreated -= OnRoomCreated;
        roomService.RoomJoined -= OnRoomJoined;
        roomService.RoomLeft -= OnRoomLeft;
        roomService.MemberJoined -= OnMemberJoined;
        roomService.MemberLeft -= OnMemberLeft;
        roomService.StatusChanged -= OnStatusChanged;
        roomService.ControlCommandReceived -= OnControlCommandReceived;
        roomService.RoomsDiscovered -= OnRoomsDiscovered;
        
        var permService = PermissionService.Instance;
        permService.PermissionRequested -= OnPermissionRequested;
        permService.PermissionGranted -= OnPermissionGranted;
        permService.PermissionRevoked -= OnPermissionRevoked;
        permService.RoleChanged -= OnMyRoleChanged;

        _gameEventSubscription?.Dispose();
        _gameEventSubscription = null;
    }

    private void UpdateUI()
    {
        var room = RoomService.Instance.CurrentRoom;
        var inRoom = room != null;
        
        NoRoomPanel.IsVisible = !inRoom;
        InRoomPanel.IsVisible = inRoom;
        MembersCard.IsVisible = inRoom;
        RemoteControlCard.IsVisible = inRoom && PermissionService.Instance.CanControlOthers;
        PermissionCard.IsVisible = inRoom;
        PvpCard.IsVisible = inRoom;
        RoomCodeBadge.IsVisible = inRoom;
        
        if (inRoom)
        {
            RoomTitle.Text = room!.Name;
            RoomSubtitle.Text = RoomService.Instance.IsHost ? "你是房主" : "已加入房间";
            RoomCodeText.Text = room.Code;
            RoomNameText.Text = room.Name;
            RoomHostText.Text = $"主机: {room.HostAddress}:{room.HostPort}";
            
            RefreshMembersList();
            RefreshPvpOpponentSelector();
            UpdatePermissionUI();
        }
        else
        {
            RoomTitle.Text = "多人房间";
            RoomSubtitle.Text = "创建或加入房间与他人互动";
            
            // 重置 PVP 状态
            _isPvpActive = false;
            _pvpOpponentId = null;
        }
    }

    private void UpdatePermissionUI()
    {
        var permService = PermissionService.Instance;
        var myRole = permService.MyRole;
        
        // 更新角色选择
        RoleController.IsChecked = myRole == UserPermissionRole.Controller;
        RoleControlled.IsChecked = myRole == UserPermissionRole.Controlled;
        RoleObserver.IsChecked = myRole == UserPermissionRole.Observer;
        
        // 更新角色说明
        RoleDescriptionText.Text = myRole switch
        {
            UserPermissionRole.Controller => "控制者可以向被控者发送控制指令，包括设置强度和发送波形。需要先请求并获得被控者授权。",
            UserPermissionRole.Controlled => "被控者可以接收来自控制者的指令。你可以同意或拒绝控制请求，也可以随时撤销授权。",
            UserPermissionRole.Observer => "观察者仅能查看房间成员状态，不能发送或接收控制指令。",
            _ => ""
        };
        
        // 更新权限状态徽章
        PermissionStatusText.Text = myRole switch
        {
            UserPermissionRole.Controller => "控制者模式",
            UserPermissionRole.Controlled => "被控者模式",
            UserPermissionRole.Observer => "观察者模式",
            _ => ""
        };
        PermissionStatusBadge.Background = new SolidColorBrush(Color.Parse(myRole switch
        {
            UserPermissionRole.Controller => "#10B981",
            UserPermissionRole.Controlled => "#F59E0B",
            UserPermissionRole.Observer => "#8091A8",
            _ => "#8091A8"
        }));
        
        // 切换面板显示
        ControllerPermissionPanel.IsVisible = myRole == UserPermissionRole.Controller;
        ControlledPermissionPanel.IsVisible = myRole == UserPermissionRole.Controlled;
        RemoteControlCard.IsVisible = RoomService.Instance.CurrentRoom != null && myRole == UserPermissionRole.Controller;
        
        // 刷新相关列表
        RefreshControlledUsersList();
        RefreshControllersList();
        RefreshControlTargetForPermission();
        RefreshPendingRequestsList();
    }

    private void RefreshControlledUsersList()
    {
        MyControlledUsersList.Children.Clear();
        
        var controlledUsers = PermissionService.Instance.GetMyControlledUsers().ToList();
        if (controlledUsers.Count == 0)
        {
            MyControlledUsersList.Children.Add(new TextBlock
            {
                Text = "暂无",
                Foreground = new SolidColorBrush(Color.Parse("#8091A8")),
                FontStyle = FontStyle.Italic,
                FontSize = 12
            });
            return;
        }
        
        foreach (var userId in controlledUsers)
        {
            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == userId);
            var card = CreatePermissionUserCard(member?.Nickname ?? userId, userId, true);
            MyControlledUsersList.Children.Add(card);
        }
    }

    private void RefreshControllersList()
    {
        MyControllersList.Children.Clear();
        
        var controllers = PermissionService.Instance.GetMyControllers().ToList();
        if (controllers.Count == 0)
        {
            MyControllersList.Children.Add(new TextBlock
            {
                Text = "暂无",
                Foreground = new SolidColorBrush(Color.Parse("#8091A8")),
                FontStyle = FontStyle.Italic,
                FontSize = 12
            });
            return;
        }
        
        foreach (var controllerId in controllers)
        {
            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == controllerId);
            var card = CreatePermissionUserCard(member?.Nickname ?? controllerId, controllerId, false);
            MyControllersList.Children.Add(card);
        }
    }

    private void RefreshControlTargetForPermission()
    {
        ControlTargetForPermission.Items.Clear();
        ControlTargetForPermission.Items.Add(new ComboBoxItem { Content = "-- 选择被控者 --", Tag = null });
        
        // 只显示角色为被控者的成员
        foreach (var member in RoomService.Instance.Members.Where(m => 
            m.Id != RoomService.Instance.UserId && 
            m.PermissionRole == UserPermissionRole.Controlled))
        {
            ControlTargetForPermission.Items.Add(new ComboBoxItem
            {
                Content = $"{member.Nickname} ({(member.AcceptsControl ? "接受控制" : "未开放")})",
                Tag = member.Id
            });
        }
        
        ControlTargetForPermission.SelectedIndex = 0;
    }

    private void RefreshPendingRequestsList()
    {
        PendingRequestsList.Children.Clear();
        
        if (_pendingRequests.Count == 0)
        {
            PendingRequestsCard.IsVisible = false;
            return;
        }
        
        PendingRequestsCard.IsVisible = true;
        
        foreach (var request in _pendingRequests.Values)
        {
            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == request.RequesterId);
            var card = CreatePendingRequestCard(request, member?.Nickname ?? request.RequesterId);
            PendingRequestsList.Children.Add(card);
        }
    }

    private async Task RefreshDiscoveredRoomsAsync()
    {
        if (_isRefreshingDiscovery)
        {
            return;
        }

        _isRefreshingDiscovery = true;
        DiscoveryStatusText.Text = "正在扫描局域网房间...";
        try
        {
            var rooms = await RoomService.Instance.ScanRoomsAndPublishAsync();
            RefreshDiscoveredRoomsList(rooms);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh discovered rooms");
            DiscoveryStatusText.Text = $"扫描失败: {ex.Message}";
        }
        finally
        {
            _isRefreshingDiscovery = false;
        }
    }

    private void RefreshDiscoveredRoomsList(IReadOnlyList<RoomDiscoveryResult> rooms)
    {
        DiscoveredRoomsList.Children.Clear();

        if (rooms.Count == 0)
        {
            DiscoveredRoomsList.Children.Add(new TextBlock
            {
                Text = "未发现可加入房间",
                Foreground = new SolidColorBrush(Color.Parse("#8091A8")),
                FontStyle = FontStyle.Italic,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });
            DiscoveryStatusText.Text = "未发现可加入房间，稍后会自动重试扫描";
            return;
        }

        DiscoveryStatusText.Text = $"已发现 {rooms.Count} 个房间（固定端口 {RoomService.DefaultRoomPort}）";

        foreach (var room in rooms)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#172033")),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(10)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,Auto")
            };

            grid.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(room.RoomName) ? "未命名房间" : room.RoomName,
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13
            });

            var detail = new TextBlock
            {
                Text = $"{room.Address}:{room.Port} · 成员 {room.MemberCount} · 延迟 {Math.Max(room.LatencyMs, 1)}ms",
                Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
                FontSize = 11,
                Margin = new Avalonia.Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(detail, 1);
            grid.Children.Add(detail);

            var joinButton = new Button
            {
                Content = "加入",
                Background = new SolidColorBrush(Color.Parse("#0EA5E9")),
                Foreground = Brushes.White,
                Padding = new Avalonia.Thickness(12, 6),
                CornerRadius = new Avalonia.CornerRadius(6),
                Tag = room,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            joinButton.Click += OnJoinDiscoveredRoomClick;
            Grid.SetColumn(joinButton, 1);
            Grid.SetRowSpan(joinButton, 2);
            grid.Children.Add(joinButton);

            card.Child = grid;
            DiscoveredRoomsList.Children.Add(card);
        }
    }

    private Border CreatePermissionUserCard(string nickname, string userId, bool canRevoke)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1F2B3E")),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(12, 8)
        };
        
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        
        grid.Children.Add(new TextBlock
        {
            Text = nickname,
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        
        if (canRevoke)
        {
            var revokeBtn = new Button
            {
                Content = "撤销",
                Background = new SolidColorBrush(Color.Parse("#EF4444")),
                Foreground = Brushes.White,
                Padding = new Avalonia.Thickness(10, 4),
                CornerRadius = new Avalonia.CornerRadius(4),
                Tag = userId,
                FontSize = 11
            };
            revokeBtn.Click += OnRevokePermissionClick;
            Grid.SetColumn(revokeBtn, 1);
            grid.Children.Add(revokeBtn);
        }
        
        card.Child = grid;
        return card;
    }

    private Border CreatePendingRequestCard(PermissionRequest request, string nickname)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1F2B3E")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(16, 12)
        };
        
        var stack = new StackPanel { Spacing = 10 };
        
        stack.Children.Add(new TextBlock
        {
            Text = $"{nickname} 请求控制你的设备",
            Foreground = Brushes.White,
            FontSize = 13
        });
        
        var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        
        var acceptBtn = new Button
        {
            Content = "同意",
            Background = new SolidColorBrush(Color.Parse("#10B981")),
            Foreground = Brushes.White,
            Padding = new Avalonia.Thickness(16, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            Tag = request.Id
        };
        acceptBtn.Click += OnAcceptPermissionClick;
        
        var rejectBtn = new Button
        {
            Content = "拒绝",
            Background = new SolidColorBrush(Color.Parse("#EF4444")),
            Foreground = Brushes.White,
            Padding = new Avalonia.Thickness(16, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            Tag = request.Id
        };
        rejectBtn.Click += OnRejectPermissionClick;
        
        btnPanel.Children.Add(acceptBtn);
        btnPanel.Children.Add(rejectBtn);
        stack.Children.Add(btnPanel);
        
        card.Child = stack;
        return card;
    }

    private void RefreshMembersList()
    {
        MembersList.Children.Clear();
        
        var members = RoomService.Instance.Members.ToList();
        MemberCountText.Text = members.Count.ToString();
        
        if (members.Count == 0)
        {
            MembersList.Children.Add(new TextBlock
            {
                Text = "暂无成员",
                Foreground = new SolidColorBrush(Color.Parse("#8091A8")),
                FontStyle = FontStyle.Italic,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });
            return;
        }
        
        foreach (var member in members)
        {
            var card = CreateMemberCard(member);
            MembersList.Children.Add(card);
        }
        
        // 更新控制目标选择器
        ControlTargetSelector.Items.Clear();
        ControlTargetSelector.Items.Add(new ComboBoxItem { Content = "-- 选择成员 --", Tag = null });
        
        foreach (var member in members.Where(m => m.Id != RoomService.Instance.UserId && m.HasDevice))
        {
            ControlTargetSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{member.Nickname} ({(member.IsOnline ? "在线" : "离线")})",
                Tag = member.Id
            });
        }
        
        ControlTargetSelector.SelectedIndex = 0;
    }

    private Border CreateMemberCard(RoomMember member)
    {
        var isMe = member.Id == RoomService.Instance.UserId;
        var roleColor = member.Role switch
        {
            MemberRole.Owner => "#F59E0B",
            MemberRole.Admin => "#0EA5E9",
            _ => "#10B981"
        };
        
        var permissionRoleText = member.PermissionRole switch
        {
            UserPermissionRole.Controller => "控制者",
            UserPermissionRole.Controlled => "被控者",
            UserPermissionRole.Observer => "观察者",
            _ => ""
        };
        
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#172033")),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(16, 12),
            BorderBrush = isMe ? new SolidColorBrush(Color.Parse("#0EA5E9")) : null,
            BorderThickness = isMe ? new Avalonia.Thickness(1) : new Avalonia.Thickness(0)
        };
        
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        
        // 头像
        var avatar = new Border
        {
            Background = new SolidColorBrush(Color.Parse(roleColor + "30")),
            CornerRadius = new Avalonia.CornerRadius(20),
            Width = 40,
            Height = 40,
            Margin = new Avalonia.Thickness(0, 0, 12, 0)
        };
        var avatarText = new TextBlock
        {
            Text = member.Nickname.Length > 0 ? member.Nickname[0].ToString().ToUpper() : "?",
            Foreground = new SolidColorBrush(Color.Parse(roleColor)),
            FontWeight = FontWeight.Bold,
            FontSize = 16,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        avatar.Child = avatarText;
        Grid.SetColumn(avatar, 0);
        
        // 信息
        var info = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var namePanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        namePanel.Children.Add(new TextBlock
        {
            Text = member.Nickname + (isMe ? " (我)" : ""),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold
        });
        
        if (member.Role == MemberRole.Owner)
        {
            namePanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F59E0B")),
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(6, 2),
                Child = new TextBlock
                {
                    Text = "房主",
                    Foreground = Brushes.White,
                    FontSize = 10
                }
            });
        }
        info.Children.Add(namePanel);
        
        // 权限角色和状态
        var statusText = new TextBlock
        {
            Text = $"{permissionRoleText} - {(member.HasDevice ? "有设备" : "无设备")} - {(member.IsOnline ? "在线" : "离线")}",
            Foreground = new SolidColorBrush(Color.Parse("#9AB0C8")),
            FontSize = 12,
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        };
        info.Children.Add(statusText);
        Grid.SetColumn(info, 1);
        
        // 状态指示器
        var statusIndicator = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = member.IsOnline 
                ? new SolidColorBrush(Color.Parse("#10B981")) 
                : new SolidColorBrush(Color.Parse("#8091A8"))
        };
        Grid.SetColumn(statusIndicator, 2);
        
        grid.Children.Add(avatar);
        grid.Children.Add(info);
        grid.Children.Add(statusIndicator);
        
        card.Child = grid;
        return card;
    }

    #region Permission Event Handlers

    private async void OnRoleChanged(object? sender, RoutedEventArgs e)
    {
        UserPermissionRole role;
        if (RoleController.IsChecked == true)
            role = UserPermissionRole.Controller;
        else if (RoleControlled.IsChecked == true)
            role = UserPermissionRole.Controlled;
        else
            role = UserPermissionRole.Observer;
        
        if (!PermissionService.Instance.TrySetMyRole(role))
        {
            ShowStatus("当前处于 PVP 被控锁定状态，不能切换到非被控角色");
            UpdatePermissionUI();
            return;
        }
        
        // 广播角色变更
        if (RoomService.Instance.CurrentRoom != null)
        {
            await RoomService.Instance.BroadcastPermissionRoleAsync(role, role == UserPermissionRole.Controlled);
        }
        
        UpdatePermissionUI();
    }

    private async void OnRequestPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (ControlTargetForPermission.SelectedItem is not ComboBoxItem item || item.Tag is not string targetId)
        {
            ShowStatus("请先选择要控制的被控者");
            return;
        }
        
        var success = await PermissionService.Instance.RequestControlAsync(targetId);
        if (success)
        {
            ShowStatus("控制请求已发送，等待对方响应...");
        }
        else
        {
            ShowStatus("无法发送控制请求");
        }
    }

    private async void OnAcceptPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string requestId)
        {
            await PermissionService.Instance.RespondToRequestAsync(requestId, true);
            _pendingRequests.Remove(requestId);
            RefreshPendingRequestsList();
            ShowStatus("已同意控制请求");
        }
    }

    private async void OnRejectPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string requestId)
        {
            await PermissionService.Instance.RespondToRequestAsync(requestId, false, "用户拒绝了请求");
            _pendingRequests.Remove(requestId);
            RefreshPendingRequestsList();
            ShowStatus("已拒绝控制请求");
        }
    }

    private async void OnRevokePermissionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string userId)
        {
            await PermissionService.Instance.RevokeControlAsync(userId);
            RefreshControllersList();
            ShowStatus("已撤销控制权限");
        }
    }

    #endregion

    #region Event Handlers

    private async void OnCreateRoomClick(object? sender, RoutedEventArgs e)
    {
        var roomName = CreateRoomName.Text?.Trim();
        if (string.IsNullOrEmpty(roomName))
        {
            ShowStatus("请输入房间名称");
            return;
        }
        
        try
        {
            var room = await RoomService.Instance.CreateRoomAsync(roomName);
            ShowStatus($"房间已创建！房间码: {room.Code}，地址: {room.HostAddress}:{room.HostPort}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to create room");
            ShowStatus($"创建房间失败: {ex.Message}");
        }
    }

    private async void OnJoinRoomClick(object? sender, RoutedEventArgs e)
    {
        var host = JoinHostAddress.Text?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            ShowStatus("请输入主机地址");
            return;
        }

        try
        {
            var success = await RoomService.Instance.JoinRoomAsync(host, RoomService.DefaultRoomPort);
            if (!success)
            {
                ShowStatus("加入房间失败，请检查地址和端口是否正确，以及防火墙设置");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to join room");
            ShowStatus($"加入房间失败: {ex.Message}。请检查 Windows Defender 防火墙设置。");
        }
    }

    private async void OnRefreshDiscoveredRoomsClick(object? sender, RoutedEventArgs e)
    {
        await RefreshDiscoveredRoomsAsync();
    }

    private async void OnJoinDiscoveredRoomClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RoomDiscoveryResult room })
        {
            return;
        }

        JoinHostAddress.Text = room.Address;

        try
        {
            var success = await RoomService.Instance.JoinRoomAsync(room.Address, room.Port);
            if (!success)
            {
                ShowStatus($"加入房间失败：{room.RoomName} ({room.Address}:{room.Port})");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to join discovered room {Address}:{Port}", room.Address, room.Port);
            ShowStatus($"加入失败: {ex.Message}");
        }
    }

    private async void OnLeaveRoomClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await RoomService.Instance.LeaveRoomAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to leave room");
        }
    }

    private async void OnSendRemoteControlClick(object? sender, RoutedEventArgs e)
    {
        if (ControlTargetSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string targetId)
        {
            ShowStatus("请先选择控制目标");
            return;
        }
        
        // 检查是否有权限
        if (!PermissionService.Instance.HasControlPermission(targetId))
        {
            ShowStatus("你没有控制该用户的权限，请先发送控制请求");
            return;
        }
        
        if (!int.TryParse(RemoteStrengthA.Text, out var strengthA))
            strengthA = 50;
        if (!int.TryParse(RemoteStrengthB.Text, out var strengthB))
            strengthB = 50;
        
        try
        {
            await RoomService.Instance.SendControlCommandAsync(targetId, new ControlCommand
            {
                Action = "set_strength",
                Channel = "AB",
                Value = strengthA, // A 通道
                RequireAck = true
            });
            
            await RoomService.Instance.SendControlCommandAsync(targetId, new ControlCommand
            {
                Action = "set_strength",
                Channel = "B",
                Value = strengthB,
                RequireAck = true
            });
            
            ShowStatus("控制指令已发送");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to send control command");
            ShowStatus($"发送失败: {ex.Message}");
        }
    }

    #endregion

    #region Room Service Events

    private void OnRoomCreated(object? sender, RoomEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUI);
    }

    private void OnRoomJoined(object? sender, RoomEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUI);
    }

    private void OnRoomLeft(object? sender, RoomEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUI);
    }

    private void OnMemberJoined(object? sender, MemberEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshMembersList();
            ShowStatus($"{e.Member.Nickname} 加入了房间");
        });
    }

    private void OnMemberLeft(object? sender, MemberEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshMembersList();
            ShowStatus($"{e.Member.Nickname} 离开了房间");
        });
    }

    private void OnStatusChanged(object? sender, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowStatus(message));
    }

    private void OnRoomsDiscovered(object? sender, IReadOnlyList<RoomDiscoveryResult> rooms)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshDiscoveredRoomsList(rooms));
    }

    private void OnPermissionRequested(object? sender, PermissionRequestEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _pendingRequests[e.Request.Id] = e.Request;
            RefreshPendingRequestsList();
            
            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.Request.RequesterId);
            ShowStatus($"{member?.Nickname ?? e.Request.RequesterId} 请求控制你的设备");
        });
    }

    private void OnPermissionGranted(object? sender, PermissionGrantedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshControlledUsersList();
            RefreshControllersList();
            RefreshMembersList();
            
            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.ControlledId);
            ShowStatus($"已获得对 {member?.Nickname ?? e.ControlledId} 的控制权限");
        });
    }

    private void OnPermissionRevoked(object? sender, PermissionRevokedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshControlledUsersList();
            RefreshControllersList();
            
            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.ControlledId);
            ShowStatus($"控制权限已撤销: {member?.Nickname ?? e.ControlledId}");
        });
    }

    private void OnMyRoleChanged(object? sender, UserPermissionRole role)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdatePermissionUI();
        });
    }

    private async void OnControlCommandReceived(object? sender, ControlEventArgs e)
    {
        Logger.Information("Received control command from {Sender}: {Action}", e.SenderId, e.Command.Action);

        if (e.Command.Action.StartsWith("permission_"))
        {
            return;
        }

        if (e.Command.Action.Equals("control_ack", StringComparison.OrdinalIgnoreCase) ||
            e.Command.Action.Equals("control_grant", StringComparison.OrdinalIgnoreCase))
        {
            var detail = TryParseControlResult(e.Command);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowStatus(string.IsNullOrWhiteSpace(detail?.Message)
                    ? "控制指令已通过主机仲裁"
                    : $"控制指令已通过：{detail!.Message}"));
            return;
        }

        if (e.Command.Action.Equals("control_deny", StringComparison.OrdinalIgnoreCase))
        {
            var detail = TryParseControlResult(e.Command);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowStatus(string.IsNullOrWhiteSpace(detail?.Message)
                    ? "控制指令被主机拒绝"
                    : $"控制指令被拒绝：{detail!.Message}"));
            return;
        }

        if (e.Command.Action == "pvp_start")
        {
            _pvpResultCommitted = false;
            _lastPvpDamagePunishAtUtc = DateTime.MinValue;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _isPvpActive = true;
                _pvpOpponentId = e.SenderId;
                var opponent = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.SenderId);
                ShowStatus($"PVP 对战开始！对手: {opponent?.Nickname ?? e.SenderId}");

                PvpStatusBadge.Background = new SolidColorBrush(Color.Parse("#10B981"));
                PvpStatusText.Text = "对战中";
                PvpStatusPanel.IsVisible = true;
                PvpPlayer1Name.Text = "我";
                PvpPlayer2Name.Text = opponent?.Nickname ?? "对手";
                PvpPlayer1Status.Text = "游戏中";
                PvpPlayer1Status.Foreground = new SolidColorBrush(Color.Parse("#10B981"));
                PvpPlayer2Status.Text = "游戏中";
                PvpPlayer2Status.Foreground = new SolidColorBrush(Color.Parse("#10B981"));
                BtnStartPvp.IsEnabled = false;
                BtnStopPvp.IsEnabled = true;
            });
            return;
        }

        if (e.Command.Action == "pvp_stop")
        {
            EndPvp();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowStatus("对手结束了 PVP 对战"));
            return;
        }

        if (e.Command.Action == "pvp_death")
        {
            var payload = TryParsePvpDeathPayload(e.Command);
            var loserId = payload?.LoserId ?? e.SenderId;
            var winnerId = payload?.WinnerId ?? RoomService.Instance.UserId;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PvpPlayer2Status.Text = "死亡!";
                PvpPlayer2Status.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                ShowStatus("对手死亡，你获得胜利并接管对方设备控制权");
            });

            var myId = RoomService.Instance.UserId;
            if (!string.IsNullOrWhiteSpace(myId) &&
                !string.IsNullOrWhiteSpace(loserId) &&
                string.Equals(winnerId, myId, StringComparison.OrdinalIgnoreCase))
            {
                PermissionService.Instance.ForceGrantControl(myId, loserId, lockControlled: true);
                UpdatePermissionUI();

                await RoomService.Instance.SendControlCommandAsync(loserId, new ControlCommand
                {
                    Action = "pvp_control_lock",
                    RequireAck = true,
                    WaveformData = System.Text.Json.JsonSerializer.Serialize(new PvpControlLockPayload
                    {
                        ControllerId = myId,
                        ControlledId = loserId,
                        LockControl = true,
                        Reason = "pvp_defeat"
                    })
                });
            }

            EndPvp();
            return;
        }

        if (e.Command.Action == "pvp_control_lock")
        {
            var payload = TryParsePvpControlLockPayload(e.Command);
            if (payload != null && payload.LockControl)
            {
                PermissionService.Instance.ApplyPvpControlLock(
                    payload.ControllerId ?? e.SenderId,
                    payload.ControlledId ?? RoomService.Instance.UserId ?? string.Empty,
                    lockControlled: true);

                if (RoomService.Instance.CurrentRoom != null)
                {
                    await RoomService.Instance.BroadcastPermissionRoleAsync(UserPermissionRole.Controlled, true);
                }

                UpdatePermissionUI();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    ShowStatus("你已在 PVP 中判负，设备控制权已移交给胜利者"));
            }

            EndPvp();
            return;
        }

        var permService = PermissionService.Instance;
        if (!permService.CanReceiveControlFrom(e.SenderId))
        {
            Logger.Warning("Rejected control command from {Sender}: no permission", e.SenderId);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowStatus($"收到未授权的控制请求 (来自: {e.SenderId})，已忽略"));
            return;
        }

        if (Core.AppServices.IsInitialized)
        {
            var deviceManager = Core.AppServices.Instance.DeviceManager;
            var devices = deviceManager.GetAllDevices().Where(d => d.Status == Core.Devices.DeviceStatus.Connected).ToList();
            var channel = e.Command.Channel switch
            {
                "A" => Core.Devices.Channel.A,
                "B" => Core.Devices.Channel.B,
                _ => Core.Devices.Channel.AB
            };

            Core.Devices.DGLab.WaveformData? waveData = null;
            if (e.Command.Action == "send_waveform" && !string.IsNullOrWhiteSpace(e.Command.WaveformData))
            {
                try
                {
                    waveData = System.Text.Json.JsonSerializer.Deserialize<Core.Devices.DGLab.WaveformData>(e.Command.WaveformData);
                }
                catch (Exception wex)
                {
                    Logger.Warning(wex, "Failed to parse waveform data");
                }
            }

            if (e.Command.Action == "emergency_stop")
            {
                try
                {
                    await deviceManager.EmergencyStopAllAsync();
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to execute emergency stop");
                }
            }
            else
            {
                var successCount = 0;
                var failedCount = 0;

                foreach (var device in devices)
                {
                    try
                    {
                        switch (e.Command.Action)
                        {
                            case "set_strength":
                                await deviceManager.SetStrengthAsync(device.Id, channel, e.Command.Value ?? 0, Core.Devices.StrengthMode.Set);
                                successCount++;
                                break;

                            case "increase_strength":
                                await deviceManager.SetStrengthAsync(device.Id, channel, e.Command.Value ?? 1, Core.Devices.StrengthMode.Increase);
                                successCount++;
                                break;

                            case "decrease_strength":
                                await deviceManager.SetStrengthAsync(device.Id, channel, e.Command.Value ?? 1, Core.Devices.StrengthMode.Decrease);
                                successCount++;
                                break;

                            case "send_waveform":
                                if (waveData != null)
                                {
                                    await deviceManager.SendWaveformAsync(device.Id, channel, waveData);
                                }
                                successCount++;
                                break;

                            case "clear_queue":
                                await deviceManager.ClearWaveformQueueAsync(device.Id, channel);
                                successCount++;
                                break;

                            case "trigger_event":
                                if (!string.IsNullOrEmpty(e.Command.WaveformData))
                                {
                                    var eventService = Core.AppServices.Instance.EventService;
                                    await eventService.TriggerEventAsync(e.Command.WaveformData, device.Id);
                                }
                                successCount++;
                                break;
                        }
                    }
                    catch (Exception dex)
                    {
                        failedCount++;
                        Logger.Warning(dex,
                            "Failed to execute remote command on device {DeviceId}: {Action}",
                            device.Id,
                            e.Command.Action);
                    }
                }

                if (failedCount > 0)
                {
                    ShowStatus($"远控执行完成：成功 {successCount} 台，失败 {failedCount} 台");
                }
            }

            var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.SenderId);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ShowStatus($"收到 {member?.Nickname ?? e.SenderId} 的控制: {e.Command.Action} = {e.Command.Value}"));
        }
    }

    private static ControlCommandResult? TryParseControlResult(ControlCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.WaveformData))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ControlCommandResult>(command.WaveformData);
        }
        catch
        {
            return null;
        }
    }

    private static PvpDeathPayload? TryParsePvpDeathPayload(ControlCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.WaveformData))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PvpDeathPayload>(command.WaveformData);
        }
        catch
        {
            return null;
        }
    }

    private static PvpControlLockPayload? TryParsePvpControlLockPayload(ControlCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.WaveformData))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PvpControlLockPayload>(command.WaveformData);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region PVP Functions

    private void RefreshPvpOpponentSelector()
    {
        PvpOpponentSelector.Items.Clear();
        PvpOpponentSelector.Items.Add(new ComboBoxItem { Content = "-- 选择对手 --", Tag = null });
        
        foreach (var member in RoomService.Instance.Members.Where(m => m.Id != RoomService.Instance.UserId))
        {
            PvpOpponentSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{member.Nickname} ({(member.HasDevice ? "有设备" : "无设备")})",
                Tag = member.Id
            });
        }
        
        PvpOpponentSelector.SelectedIndex = 0;
    }

    private async void OnStartPvpClick(object? sender, RoutedEventArgs e)
    {
        if (PermissionService.Instance.IsControlLockedForCurrentUser())
        {
            ShowStatus("当前处于被控锁定状态，无法发起 PVP");
            return;
        }

        if (PvpOpponentSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string opponentId)
        {
            ShowStatus("请先选择对手");
            return;
        }
        
        _pvpOpponentId = opponentId;
        _isPvpActive = true;
        _pvpResultCommitted = false;
        _lastPvpDamagePunishAtUtc = DateTime.MinValue;
        
        // 更新 UI
        BtnStartPvp.IsEnabled = false;
        BtnStopPvp.IsEnabled = true;
        PvpStatusBadge.Background = new SolidColorBrush(Color.Parse("#10B981"));
        PvpStatusText.Text = "对战中";
        PvpStatusPanel.IsVisible = true;
        
        var opponent = RoomService.Instance.Members.FirstOrDefault(m => m.Id == opponentId);
        PvpPlayer1Name.Text = "我";
        PvpPlayer2Name.Text = opponent?.Nickname ?? "对手";
        PvpPlayer1Status.Text = "游戏中";
        PvpPlayer2Status.Text = "游戏中";
        
        // 通知对手开始 PVP
        await RoomService.Instance.SendControlCommandAsync(opponentId, new ControlCommand
        {
            Action = "pvp_start",
            RequireAck = true,
            WaveformData = System.Text.Json.JsonSerializer.Serialize(new
            {
                Strength = int.TryParse(PvpPunishStrength.Text, out var s) ? s : 80,
                Duration = int.TryParse(PvpPunishDuration.Text, out var d) ? d : 3
            })
        });
        
        ShowStatus($"PVP 对战已开始！对手: {opponent?.Nickname}");
        Logger.Information("PVP started with opponent: {OpponentId}", opponentId);
    }

    private async void OnStopPvpClick(object? sender, RoutedEventArgs e)
    {
        if (_pvpOpponentId != null)
        {
            // 通知对手结束 PVP
            await RoomService.Instance.SendControlCommandAsync(_pvpOpponentId, new ControlCommand
            {
                Action = "pvp_stop",
                RequireAck = true
            });
        }
        
        EndPvp();
        ShowStatus("PVP 对战已结束");
    }

    private void EndPvp()
    {
        _isPvpActive = false;
        _pvpOpponentId = null;
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BtnStartPvp.IsEnabled = true;
            BtnStopPvp.IsEnabled = false;
            PvpStatusBadge.Background = new SolidColorBrush(Color.Parse("#8091A8"));
            PvpStatusText.Text = "未开始";
            PvpStatusPanel.IsVisible = false;
        });
    }

    private void OnGameEventReceived(GameEvent evt)
    {
        _ = HandlePvpGameEventAsync(evt);
    }

    private async Task HandlePvpGameEventAsync(GameEvent evt)
    {
        if (!_isPvpActive || _pvpOpponentId == null || evt.IsRemote)
        {
            return;
        }

        var isModDriven = evt.Data.ContainsKey("modSessionId") ||
                          evt.Data.ContainsKey("modScriptId") ||
                          evt.Source.Contains("mod", StringComparison.OrdinalIgnoreCase);
        if (!isModDriven)
        {
            return;
        }

        if (IsPvpDeathEvent(evt))
        {
            await ResolveLocalPvpDefeatAsync("game_event_death");
            return;
        }

        if (!IsPvpDamageEvent(evt))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastPvpDamagePunishAtUtc).TotalMilliseconds < 400)
        {
            return;
        }

        _lastPvpDamagePunishAtUtc = now;
        var baseStrength = int.TryParse(PvpPunishStrength.Text, out var parsed) ? parsed : 80;
        var shortStrength = Math.Clamp((int)Math.Round(baseStrength * 0.3), 1, 200);
        await ApplyShortPvpPunishmentAsync(shortStrength, 450);
    }

    private static bool IsPvpDamageEvent(GameEvent evt)
    {
        if (evt.Type == GameEventType.HealthLost)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(evt.EventId))
        {
            return evt.EventId.Equals("lost-hp", StringComparison.OrdinalIgnoreCase) ||
                   evt.EventId.Equals("health_lost", StringComparison.OrdinalIgnoreCase) ||
                   evt.EventId.Equals("damage", StringComparison.OrdinalIgnoreCase);
        }

        return evt.Delta < 0;
    }

    private static bool IsPvpDeathEvent(GameEvent evt)
    {
        if (evt.Type is GameEventType.Death or GameEventType.Knocked)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(evt.EventId))
        {
            return false;
        }

        return evt.EventId.Equals("dead", StringComparison.OrdinalIgnoreCase) ||
               evt.EventId.Equals("death", StringComparison.OrdinalIgnoreCase) ||
               evt.EventId.Equals("knocked", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ResolveLocalPvpDefeatAsync(string reason)
    {
        if (!_isPvpActive || _pvpOpponentId == null)
        {
            return;
        }

        await _pvpStateGate.WaitAsync();
        try
        {
            if (_pvpResultCommitted)
            {
                return;
            }

            _pvpResultCommitted = true;
        }
        finally
        {
            _pvpStateGate.Release();
        }

        var loserId = RoomService.Instance.UserId;
        var winnerId = _pvpOpponentId;
        if (string.IsNullOrWhiteSpace(loserId) || string.IsNullOrWhiteSpace(winnerId))
        {
            return;
        }

        await ExecuteDeathPunishmentAsync();

        await RoomService.Instance.SendControlCommandAsync(winnerId, new ControlCommand
        {
            Action = "pvp_death",
            RequireAck = true,
            WaveformData = System.Text.Json.JsonSerializer.Serialize(new PvpDeathPayload
            {
                LoserId = loserId,
                WinnerId = winnerId,
                Reason = reason,
                AtUtc = DateTime.UtcNow
            })
        });

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PvpPlayer1Status.Text = "死亡!";
            PvpPlayer1Status.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            ShowStatus("你在 PVP 中判负，设备控制权将移交给胜利者");
        });

        EndPvp();
    }

    private async Task ExecuteDeathPunishmentAsync()
    {
        if (!Core.AppServices.IsInitialized)
        {
            return;
        }

        try
        {
            var strength = int.TryParse(PvpPunishStrength.Text, out var s) ? s : 80;
            var duration = int.TryParse(PvpPunishDuration.Text, out var d) ? d : 3;
            var durationMs = Math.Clamp(duration * 1000, 300, 15000);

            var deviceManager = Core.AppServices.Instance.DeviceManager;
            var devices = deviceManager.GetAllDevices()
                .Where(dev => dev.Status == Core.Devices.DeviceStatus.Connected)
                .ToList();

            foreach (var device in devices)
            {
                await deviceManager.SetStrengthAsync(device.Id, Core.Devices.Channel.AB, strength, Core.Devices.StrengthMode.Set);
            }

            await Task.Delay(durationMs);

            foreach (var device in devices)
            {
                await deviceManager.SetStrengthAsync(device.Id, Core.Devices.Channel.AB, 0, Core.Devices.StrengthMode.Set);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to execute PVP death punishment");
        }
    }

    private async Task ApplyShortPvpPunishmentAsync(int deltaStrength, int durationMs)
    {
        if (!Core.AppServices.IsInitialized)
        {
            return;
        }

        try
        {
            var deviceManager = Core.AppServices.Instance.DeviceManager;
            var devices = deviceManager.GetAllDevices()
                .Where(dev => dev.Status == Core.Devices.DeviceStatus.Connected)
                .ToList();

            foreach (var device in devices)
            {
                await deviceManager.SetStrengthAsync(device.Id, Core.Devices.Channel.AB, deltaStrength, Core.Devices.StrengthMode.Increase);
            }

            await Task.Delay(Math.Clamp(durationMs, 100, 5000));

            foreach (var device in devices)
            {
                await deviceManager.SetStrengthAsync(device.Id, Core.Devices.Channel.AB, deltaStrength, Core.Devices.StrengthMode.Decrease);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to execute short PVP punishment");
        }
    }

    /// <summary>
    /// 处理 PVP 死亡事件 - 由 OCR 服务调用
    /// </summary>
    public async void OnPlayerDeath()
    {
        if (!_isPvpActive || _pvpOpponentId == null)
        {
            return;
        }

        await ResolveLocalPvpDefeatAsync("manual_or_ocr");
    }

    #endregion

    private sealed class PvpDeathPayload
    {
        public string? LoserId { get; set; }
        public string? WinnerId { get; set; }
        public string? Reason { get; set; }
        public DateTime AtUtc { get; set; }
    }

    private sealed class PvpControlLockPayload
    {
        public string? ControllerId { get; set; }
        public string? ControlledId { get; set; }
        public bool LockControl { get; set; }
        public string? Reason { get; set; }
    }

    private void ShowStatus(string message)
    {
        var parent = this.Parent;
        while (parent != null && parent is not MainWindow)
            parent = (parent as Control)?.Parent;
        
        if (parent is MainWindow mainWindow)
        {
            mainWindow.ShowStatus(message);
        }
    }
}



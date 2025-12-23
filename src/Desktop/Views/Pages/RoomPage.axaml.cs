using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using ChargingPanel.Core.Network;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ChargingPanel.Desktop.Views.Pages;

public partial class RoomPage : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<RoomPage>();
    private readonly Dictionary<string, PermissionRequest> _pendingRequests = new();
    private bool _isPvpActive = false;
    private string? _pvpOpponentId = null;

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
        
        // 订阅权限服务事件
        var permService = PermissionService.Instance;
        permService.PermissionRequested += OnPermissionRequested;
        permService.PermissionGranted += OnPermissionGranted;
        permService.PermissionRevoked += OnPermissionRevoked;
        permService.RoleChanged += OnMyRoleChanged;
        
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
        
        var permService = PermissionService.Instance;
        permService.PermissionRequested -= OnPermissionRequested;
        permService.PermissionGranted -= OnPermissionGranted;
        permService.PermissionRevoked -= OnPermissionRevoked;
        permService.RoleChanged -= OnMyRoleChanged;
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
            UserPermissionRole.Controller => "控制者可以向被控者发送控制指令，包括设置强度和发送波形。需要先请求并获得被控者的授权。",
            UserPermissionRole.Controlled => "被控者可以接收来自控制者的指令。你可以选择同意或拒绝控制请求，也可以随时撤销授权。",
            UserPermissionRole.Observer => "观察者只能查看房间成员状态，不能发送或接收控制指令。",
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
            UserPermissionRole.Observer => "#6B7280",
            _ => "#6B7280"
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
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
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
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
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

    private Border CreatePermissionUserCard(string nickname, string userId, bool canRevoke)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#313244")),
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
            Background = new SolidColorBrush(Color.Parse("#313244")),
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
            Content = "✓ 同意",
            Background = new SolidColorBrush(Color.Parse("#10B981")),
            Foreground = Brushes.White,
            Padding = new Avalonia.Thickness(16, 8),
            CornerRadius = new Avalonia.CornerRadius(6),
            Tag = request.Id
        };
        acceptBtn.Click += OnAcceptPermissionClick;
        
        var rejectBtn = new Button
        {
            Content = "✗ 拒绝",
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
                Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
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
            MemberRole.Admin => "#8b5cf6",
            _ => "#10B981"
        };
        
        var permissionRoleText = member.PermissionRole switch
        {
            UserPermissionRole.Controller => "🎮 控制者",
            UserPermissionRole.Controlled => "🎯 被控者",
            UserPermissionRole.Observer => "👁 观察者",
            _ => ""
        };
        
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1E1E2E")),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(16, 12),
            BorderBrush = isMe ? new SolidColorBrush(Color.Parse("#8b5cf6")) : null,
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
            Text = $"{permissionRoleText} • {(member.HasDevice ? "有设备" : "无设备")} • {(member.IsOnline ? "在线" : "离线")}",
            Foreground = new SolidColorBrush(Color.Parse("#A6ADC8")),
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
                : new SolidColorBrush(Color.Parse("#6B7280"))
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
        
        PermissionService.Instance.MyRole = role;
        
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
        
        var password = CreateRoomPassword.Text?.Trim();
        if (string.IsNullOrEmpty(password)) password = null;
        
        try
        {
            var room = await RoomService.Instance.CreateRoomAsync(roomName, 0, password);
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
        
        var portText = JoinHostPort.Text?.Trim();
        if (string.IsNullOrEmpty(portText))
        {
            ShowStatus("请输入端口号（必填）");
            return;
        }
        
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
        {
            ShowStatus("请输入有效的端口号 (1-65535)");
            return;
        }
        
        var password = JoinRoomPassword.Text?.Trim();
        if (string.IsNullOrEmpty(password)) password = null;
        
        try
        {
            var success = await RoomService.Instance.JoinRoomAsync(host, port, password);
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
                Value = strengthA // A 通道
            });
            
            await RoomService.Instance.SendControlCommandAsync(targetId, new ControlCommand
            {
                Action = "set_strength",
                Channel = "B",
                Value = strengthB
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
        
        // 权限相关命令不需要验证权限
        if (e.Command.Action.StartsWith("permission_"))
        {
            return; // 权限命令已经在 RoomService 中处理
        }
        
        // PVP 相关命令
        if (e.Command.Action == "pvp_start")
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _isPvpActive = true;
                _pvpOpponentId = e.SenderId;
                var opponent = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.SenderId);
                ShowStatus($"PVP 对战开始！对手: {opponent?.Nickname}");
                
                PvpStatusBadge.Background = new SolidColorBrush(Color.Parse("#10B981"));
                PvpStatusText.Text = "对战中";
                PvpStatusPanel.IsVisible = true;
                PvpPlayer1Name.Text = "我";
                PvpPlayer2Name.Text = opponent?.Nickname ?? "对手";
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
            // 对手死亡通知
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                PvpPlayer2Status.Text = "死亡!";
                PvpPlayer2Status.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                ShowStatus("对手死亡了！");
            });
            return;
        }
        
        // 检查发送者是否有权限控制我
        var permService = PermissionService.Instance;
        if (!permService.CanBeControlled)
        {
            Logger.Warning("Rejected control command from {Sender}: not in controlled role", e.SenderId);
            return;
        }
        
        var myControllers = permService.GetMyControllers().ToList();
        if (!myControllers.Contains(e.SenderId))
        {
            Logger.Warning("Rejected control command from {Sender}: no permission", e.SenderId);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                ShowStatus($"收到未授权的控制请求 (来自: {e.SenderId})，已忽略"));
            return;
        }
        
        // 执行控制命令
        if (Core.AppServices.IsInitialized)
        {
            try
            {
                var deviceManager = Core.AppServices.Instance.DeviceManager;
                var devices = deviceManager.GetAllDevices().Where(d => d.Status == Core.Devices.DeviceStatus.Connected).ToList();
                
                foreach (var device in devices)
                {
                    var channel = e.Command.Channel switch
                    {
                        "A" => Core.Devices.Channel.A,
                        "B" => Core.Devices.Channel.B,
                        _ => Core.Devices.Channel.AB
                    };
                    
                    switch (e.Command.Action)
                    {
                        case "set_strength":
                            await deviceManager.SetStrengthAsync(device.Id, channel, e.Command.Value ?? 0, Core.Devices.StrengthMode.Set);
                            break;
                            
                        case "increase_strength":
                            await deviceManager.SetStrengthAsync(device.Id, channel, e.Command.Value ?? 1, Core.Devices.StrengthMode.Increase);
                            break;
                            
                        case "decrease_strength":
                            await deviceManager.SetStrengthAsync(device.Id, channel, e.Command.Value ?? 1, Core.Devices.StrengthMode.Decrease);
                            break;
                            
                        case "send_waveform":
                            if (!string.IsNullOrEmpty(e.Command.WaveformData))
                            {
                                try
                                {
                                    var waveData = System.Text.Json.JsonSerializer.Deserialize<Core.Devices.DGLab.WaveformData>(e.Command.WaveformData);
                                    if (waveData != null)
                                    {
                                        await deviceManager.SendWaveformAsync(device.Id, channel, waveData);
                                    }
                                }
                                catch (Exception wex)
                                {
                                    Logger.Warning(wex, "Failed to parse waveform data");
                                }
                            }
                            break;
                            
                        case "clear_queue":
                            await deviceManager.ClearWaveformQueueAsync(device.Id, channel);
                            break;
                            
                        case "trigger_event":
                            // 触发事件
                            if (!string.IsNullOrEmpty(e.Command.WaveformData))
                            {
                                var eventService = Core.AppServices.Instance.EventService;
                                await eventService.TriggerEventAsync(e.Command.WaveformData, device.Id);
                            }
                            break;
                            
                        case "emergency_stop":
                            await deviceManager.EmergencyStopAllAsync();
                            break;
                    }
                }
                
                var member = RoomService.Instance.Members.FirstOrDefault(m => m.Id == e.SenderId);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    ShowStatus($"收到 {member?.Nickname ?? e.SenderId} 的控制: {e.Command.Action} = {e.Command.Value}"));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to execute remote control command");
            }
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
        if (PvpOpponentSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string opponentId)
        {
            ShowStatus("请先选择对手");
            return;
        }
        
        _pvpOpponentId = opponentId;
        _isPvpActive = true;
        
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
                Action = "pvp_stop"
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
            PvpStatusBadge.Background = new SolidColorBrush(Color.Parse("#6B7280"));
            PvpStatusText.Text = "未开始";
            PvpStatusPanel.IsVisible = false;
        });
    }

    /// <summary>
    /// 处理 PVP 死亡事件 - 由 OCR 服务调用
    /// </summary>
    public async void OnPlayerDeath()
    {
        if (!_isPvpActive || _pvpOpponentId == null) return;
        
        Logger.Information("PVP: Player death detected, sending punishment");
        
        // 自己死亡，执行本地惩罚
        var strength = int.TryParse(PvpPunishStrength.Text, out var s) ? s : 80;
        var duration = int.TryParse(PvpPunishDuration.Text, out var d) ? d : 3;
        
        if (Core.AppServices.IsInitialized)
        {
            try
            {
                var deviceManager = Core.AppServices.Instance.DeviceManager;
                var devices = deviceManager.GetAllDevices()
                    .Where(dev => dev.Status == Core.Devices.DeviceStatus.Connected).ToList();
                
                foreach (var device in devices)
                {
                    await deviceManager.SetStrengthAsync(device.Id, Core.Devices.Channel.AB, strength);
                }
                
                // 延迟后恢复
                await System.Threading.Tasks.Task.Delay(duration * 1000);
                
                foreach (var device in devices)
                {
                    await deviceManager.SetStrengthAsync(device.Id, Core.Devices.Channel.AB, 0);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to execute PVP punishment");
            }
        }
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PvpPlayer1Status.Text = "死亡!";
            PvpPlayer1Status.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
            ShowStatus("你死亡了！接受电击惩罚！");
        });
    }

    #endregion

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

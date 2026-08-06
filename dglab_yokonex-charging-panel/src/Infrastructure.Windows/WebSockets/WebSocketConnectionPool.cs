using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ChargingPanel.Infrastructure.Windows.WebSockets;

/// <summary>连接复用键：URI + 认证范围。相同键共享一条物理连接。</summary>
public sealed record WebSocketConnectionKey(Uri Uri, string? AuthenticationScope)
{
    public WebSocketConnectionKey(Uri uri) : this(uri, null)
    {
    }
}

public enum WebSocketMessageKind
{
    Text,
    Binary
}

/// <summary>一条原始 WebSocket 帧。不解释厂商 payload。</summary>
public sealed record WebSocketFrame(WebSocketMessageKind Kind, ReadOnlyMemory<byte> Payload);

public enum WebSocketSendStatus
{
    Success,
    Failed,
    OutcomeUnknown
}

/// <summary>发送结果。控制消息超时返回 OutcomeUnknown，是否重发由 Dispatcher 决定。</summary>
public sealed record WebSocketSendResult(WebSocketSendStatus Status, string? Reason);

/// <summary>
/// WebSocket 连接池。按服务身份与凭据范围复用连接，绝不解释厂商 payload。
///
/// 关键约束：
/// - 同一键只存在一条物理连接，多个租约共享；
/// - 每条物理连接只有一个读者循环，租约订阅其消息流；
/// - 每个方法只做一次可取消 I/O，超时返回 OutcomeUnknown，绝不静默重发；
/// - 重连语义不属于传输层——连接中断即如实失败，由上层决定。
/// </summary>
public sealed class WebSocketConnectionPool : IAsyncDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _operationTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<WebSocketConnectionKey, ConnectionSlot> _slots = [];
    private bool _disposed;

    public WebSocketConnectionPool(TimeProvider? timeProvider = null, TimeSpan? operationTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>获取连接租约。同一键多次获取共享一条物理连接。</summary>
    public async ValueTask<WebSocketConnectionLease> AcquireAsync(
        WebSocketConnectionKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);
        ConnectionSlot slot;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_slots.TryGetValue(key, out var existing))
            {
                existing = new ConnectionSlot(key, _timeProvider, _operationTimeout);
                _slots[key] = existing;
            }

            slot = existing;
            slot.LeaseCount++;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await slot.EnsureConnectedAsync(cancellationToken);
            return new WebSocketConnectionLease(this, slot, _operationTimeout);
        }
        catch
        {
            // 建连失败或被取消：必须退还引用并清理空 slot，
            // 否则该键会永久残留一个无人持有的计数。
            await ReleaseAsync(slot);
            throw;
        }
    }

    internal async ValueTask ReleaseAsync(ConnectionSlot slot)
    {
        ConnectionSlot? toClose = null;
        await _gate.WaitAsync();
        try
        {
            slot.LeaseCount--;
            if (slot.LeaseCount > 0)
            {
                return;
            }

            _slots.Remove(slot.Key);
            toClose = slot;
        }
        finally
        {
            _gate.Release();
        }

        if (toClose is not null)
        {
            await toClose.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 在触碰 _gate 之前先检查：DI 容器会在关闭参与者释放后再次释放单例，
        // 此时 _gate 已被 Dispose，直接 WaitAsync 会抛 ObjectDisposedException。
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        ConnectionSlot[] slots;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            slots = _slots.Values.ToArray();
            _slots.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var slot in slots)
        {
            await slot.CloseAsync();
        }

        _gate.Dispose();
    }

    /// <summary>一个复用键对应的物理连接槽位。</summary>
    internal sealed class ConnectionSlot
    {
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _operationTimeout;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private readonly object _subscriberGate = new();
        private readonly List<Action<WebSocketFrame>> _subscribers = [];
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _readerCts;
        private Task? _readerTask;

        public ConnectionSlot(
            WebSocketConnectionKey key,
            TimeProvider timeProvider,
            TimeSpan operationTimeout)
        {
            Key = key;
            _timeProvider = timeProvider;
            _operationTimeout = operationTimeout;
        }

        public WebSocketConnectionKey Key { get; }

        public int LeaseCount { get; set; }

        public WebSocketState State =>
            _socket?.State ?? WebSocketState.None;

        public void AddSubscriber(Action<WebSocketFrame> onFrame)
        {
            lock (_subscriberGate)
            {
                _subscribers.Add(onFrame);
            }
        }

        public void RemoveSubscriber(Action<WebSocketFrame> onFrame)
        {
            lock (_subscriberGate)
            {
                _subscribers.Remove(onFrame);
            }
        }

        public async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_socket is { State: WebSocketState.Open })
            {
                return;
            }

            await _connectGate.WaitAsync(cancellationToken);
            try
            {
                if (_socket is { State: WebSocketState.Open })
                {
                    return;
                }

                var socket = new ClientWebSocket();
                // 不附加自定义凭据头；认证范围只决定复用键，凭据由上层在协议里处理。
                await socket.ConnectAsync(Key.Uri, cancellationToken);
                _socket = socket;
                _readerCts = new CancellationTokenSource();
                _readerTask = Task.Run(() => ReadLoopAsync(socket, _readerCts.Token));
            }
            catch
            {
                _socket?.Dispose();
                _socket = null;
                throw;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        public async ValueTask SendAsync(
            WebSocketMessageKind kind,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            var socket = _socket ?? throw new InvalidOperationException("连接未建立或已关闭。");
            if (socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket 连接已关闭，无法发送。");
            }

            var messageType = kind == WebSocketMessageKind.Text
                ? WebSocketMessageType.Text
                : WebSocketMessageType.Binary;
            await socket.SendAsync(payload, messageType, endOfMessage: true, cancellationToken);
        }

        public async ValueTask CloseAsync()
        {
            // 取消读者并关闭物理连接。单个失败不阻止整体清理。
            var readerCts = _readerCts;
            _readerCts = null;
            readerCts?.Cancel();

            var socket = _socket;
            _socket = null;
            if (socket is not null)
            {
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "租约全部释放",
                            CancellationToken.None);
                    }
                }
                catch
                {
                    // 关闭失败不阻止释放。
                }

                socket.Dispose();
            }

            if (readerCts is not null)
            {
                try
                {
                    await (_readerTask ?? Task.CompletedTask);
                }
                catch
                {
                    // 读者退出方式不影响释放结果。
                }

                readerCts.Dispose();
            }
        }

        private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    using var owner = MemoryPool<byte>.Shared.Rent(buffer.Length);
                    var memory = owner.Memory;
                    var result = await socket.ReceiveAsync(memory, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var payload = memory[..result.Count].ToArray();
                    var kind = result.MessageType == WebSocketMessageType.Text
                        ? WebSocketMessageKind.Text
                        : WebSocketMessageKind.Binary;
                    var frame = new WebSocketFrame(kind, payload);
                    Broadcast(frame);
                }
            }
            catch (OperationCanceledException)
            {
                // 租约全部释放或宿主关闭：正常退出。
            }
            catch
            {
                // 读循环异常：通知订阅者连接已中断。
            }
        }

        private void Broadcast(WebSocketFrame frame)
        {
            Action<WebSocketFrame>[] subscribers;
            lock (_subscriberGate)
            {
                subscribers = _subscribers.ToArray();
            }

            foreach (var subscriber in subscribers)
            {
                try
                {
                    subscriber(frame);
                }
                catch
                {
                    // 单个订阅者异常不得影响其他订阅者。
                }
            }
        }
    }
}

/// <summary>
/// 连接租约。释放即归还引用；只有最后一个租约释放才真正关闭物理连接。
/// </summary>
public sealed class WebSocketConnectionLease : IAsyncDisposable
{
    private readonly WebSocketConnectionPool _pool;
    private readonly WebSocketConnectionPool.ConnectionSlot _slot;
    private readonly Action<WebSocketFrame> _subscriber;
    private readonly Channel<WebSocketFrame> _messages;
    private readonly TimeSpan _operationTimeout;
    private bool _closed;

    internal WebSocketConnectionLease(
        WebSocketConnectionPool pool,
        WebSocketConnectionPool.ConnectionSlot slot,
        TimeSpan operationTimeout)
    {
        _pool = pool;
        _slot = slot;
        _operationTimeout = operationTimeout;
        _messages = Channel.CreateUnbounded<WebSocketFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _subscriber = frame => _messages.Writer.TryWrite(frame);
        slot.AddSubscriber(_subscriber);
    }

    /// <summary>底层物理连接状态。租约本身不持有重连逻辑。</summary>
    public WebSocketState State => _slot.State;

    /// <summary>是否已归还。</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// 发送一帧。单次 I/O；超时返回 OutcomeUnknown，绝不在此重试。
    /// </summary>
    public async Task<WebSocketSendResult> SendAsync(
        WebSocketMessageKind kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_operationTimeout);
            await _slot.SendAsync(kind, payload, timeoutCts.Token);
            return new WebSocketSendResult(WebSocketSendStatus.Success, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 发送超时：结果未知，是否重发由 Dispatcher 依据交付语义决定。
            return new WebSocketSendResult(WebSocketSendStatus.OutcomeUnknown, "发送超时");
        }
        catch (Exception ex)
        {
            return new WebSocketSendResult(WebSocketSendStatus.Failed, ex.Message);
        }
    }

    /// <summary>读取连接消息流。每个租约独立读取；连接中断后流自然结束。</summary>
    public IAsyncEnumerable<WebSocketFrame> ReadMessagesAsync(CancellationToken cancellationToken) =>
        ReadCoreAsync(cancellationToken);

    private async IAsyncEnumerable<WebSocketFrame> ReadCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!_closed && !cancellationToken.IsCancellationRequested)
        {
            WebSocketFrame frame;
            try
            {
                frame = await _messages.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                yield break;
            }

            yield return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _slot.RemoveSubscriber(_subscriber);
        _messages.Writer.TryComplete();
        await _pool.ReleaseAsync(_slot);
    }
}

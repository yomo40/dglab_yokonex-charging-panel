using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChargingPanel.Core.Data;
using ChargingPanel.Core.Devices;
using ChargingPanel.Core.Events;
using Serilog;

namespace ChargingPanel.Core.Network;

/// <summary>
/// MOD 桥接服务
/// 脚本通过 bridge API 声明后，按需拉起 WebSocket/HTTP/UDP 入口。
/// </summary>
public sealed class ModBridgeService : IDisposable
{
    public const int FixedWebSocketPort = 39001;
    public const int FixedHttpPort = 39002;
    private const int DefaultMaxMessagesPerSecond = 50;
    private const int MaxRulesPerSession = 20;
    private const int UdpSessionIdleSeconds = 90;

    private readonly EventBus _eventBus;
    private readonly EventService _eventService;
    private readonly DeviceManager _deviceManager;
    private readonly ILogger _logger = Log.ForContext<ModBridgeService>();

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ScriptBridgeRegistration> _scriptRegistrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<JsonElement, ModBridgeMappedEvent?>> _scriptMappers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModBridgeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UdpBridgeSession> _udpSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _udpSessionByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RateLimitWindow> _rateLimitWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, UdpChannelWorker> _udpWorkers = new();
    private readonly ConcurrentDictionary<int, int> _udpRefCounts = new();

    private HttpListener? _wsListener;
    private HttpListener? _httpListener;
    private Task? _wsLoopTask;
    private Task? _httpLoopTask;
    private CancellationTokenSource? _serviceCts;
    private bool _started;
    private int _wsRefCount;
    private int _httpRefCount;

    public bool IsStarted => _started;
    public bool IsWebSocketRunning => _wsListener?.IsListening == true;
    public bool IsHttpRunning => _httpListener?.IsListening == true;
    public IReadOnlyCollection<int> ActiveUdpPorts => _udpWorkers.Keys.OrderBy(x => x).ToArray();

    public ModBridgeService(EventBus eventBus, EventService eventService, DeviceManager deviceManager)
    {
        _eventBus = eventBus;
        _eventService = eventService;
        _deviceManager = deviceManager;
    }

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            _serviceCts = new CancellationTokenSource();
            _started = true;
            _logger.Information("ModBridgeService started (deferred listeners)");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _serviceCts?.Cancel();
            _serviceCts?.Dispose();
            _serviceCts = null;

            StopWebSocketListenerUnsafe();
            StopHttpListenerUnsafe();
            await StopAllUdpListenersUnsafe();

            foreach (var session in _sessions.Values.ToArray())
            {
                _eventService.UnregisterModRulesForSession(session.SessionId);
                await CloseSessionSocketAsync(session.Socket, WebSocketCloseStatus.NormalClosure, "service-stop");
            }

            foreach (var udpSession in _udpSessions.Values.ToArray())
            {
                RemoveUdpSession(udpSession);
            }

            _sessions.Clear();
            _udpSessions.Clear();
            _udpSessionByEndpoint.Clear();
            _scriptRegistrations.Clear();
            _scriptMappers.Clear();
            _udpRefCounts.Clear();
            _wsRefCount = 0;
            _httpRefCount = 0;
            _logger.Information("ModBridgeService stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RegisterScriptAsync(string scriptId, string? scriptName = null, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            return;
        }

        await EnsureStartedAsync();
        var registration = _scriptRegistrations.GetOrAdd(scriptId, id => new ScriptBridgeRegistration(id));
        lock (registration.SyncRoot)
        {
            registration.ScriptName = string.IsNullOrWhiteSpace(scriptName) ? registration.ScriptName : scriptName;
            registration.Version = string.IsNullOrWhiteSpace(version) ? registration.Version : version;
            registration.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    public void SetScriptEventMapper(string scriptId, Func<JsonElement, ModBridgeMappedEvent?> mapper)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            return;
        }

        _scriptMappers[scriptId] = mapper;
    }

    public void ClearScriptEventMapper(string scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            return;
        }

        _scriptMappers.TryRemove(scriptId, out _);
    }

    public async Task StartWebSocketForScriptAsync(string scriptId)
    {
        await EnsureStartedAsync();
        var reg = _scriptRegistrations.GetOrAdd(scriptId, id => new ScriptBridgeRegistration(id));
        var needsStart = false;
        lock (reg.SyncRoot)
        {
            if (!reg.WebSocketEnabled)
            {
                reg.WebSocketEnabled = true;
                _wsRefCount++;
                needsStart = _wsRefCount == 1;
            }
        }

        if (needsStart)
        {
            await StartWebSocketListenerAsync();
        }
    }

    public async Task StartHttpForScriptAsync(string scriptId)
    {
        await EnsureStartedAsync();
        var reg = _scriptRegistrations.GetOrAdd(scriptId, id => new ScriptBridgeRegistration(id));
        var needsStart = false;
        lock (reg.SyncRoot)
        {
            if (!reg.HttpEnabled)
            {
                reg.HttpEnabled = true;
                _httpRefCount++;
                needsStart = _httpRefCount == 1;
            }
        }

        if (needsStart)
        {
            await StartHttpListenerAsync();
        }
    }

    public async Task StartUdpForScriptAsync(string scriptId, int port)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "UDP 端口范围必须在 1-65535");
        }

        await EnsureStartedAsync();
        var reg = _scriptRegistrations.GetOrAdd(scriptId, id => new ScriptBridgeRegistration(id));
        var needsStart = false;
        lock (reg.SyncRoot)
        {
            if (reg.UdpPorts.Add(port))
            {
                _udpRefCounts.AddOrUpdate(port, _ => 1, (_, current) => current + 1);
                needsStart = !_udpWorkers.ContainsKey(port);
            }
        }

        if (needsStart)
        {
            await StartUdpWorkerAsync(port);
        }
    }

    public async Task UnregisterScriptAsync(string scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            return;
        }

        if (_scriptRegistrations.TryRemove(scriptId, out var reg))
        {
            var stopWs = false;
            var stopHttp = false;
            List<int> udpPorts;
            lock (reg.SyncRoot)
            {
                if (reg.WebSocketEnabled && _wsRefCount > 0)
                {
                    _wsRefCount--;
                    stopWs = _wsRefCount == 0;
                }

                if (reg.HttpEnabled && _httpRefCount > 0)
                {
                    _httpRefCount--;
                    stopHttp = _httpRefCount == 0;
                }

                udpPorts = reg.UdpPorts.ToList();
            }

            if (stopWs)
            {
                StopWebSocketListenerUnsafe();
            }

            if (stopHttp)
            {
                StopHttpListenerUnsafe();
            }

            foreach (var port in udpPorts)
            {
                if (_udpRefCounts.AddOrUpdate(port, _ => 0, (_, current) => Math.Max(0, current - 1)) == 0)
                {
                    _udpRefCounts.TryRemove(port, out _);
                    await StopUdpWorkerAsync(port);
                }
            }
        }

        _scriptMappers.TryRemove(scriptId, out _);

        var ownedUdpSessions = _udpSessions.Values
            .Where(s => string.Equals(s.ScriptId, scriptId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var udpSession in ownedUdpSessions)
        {
            RemoveUdpSession(udpSession);
        }

        var ownedSessions = _sessions.Values.Where(s => string.Equals(s.ScriptId, scriptId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var session in ownedSessions)
        {
            await CloseSessionAsync(session, WebSocketCloseStatus.NormalClosure, "script-unloaded");
        }
    }

    private async Task EnsureStartedAsync()
    {
        if (_started)
        {
            return;
        }

        await StartAsync();
    }

    private async Task StartWebSocketListenerAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_wsListener?.IsListening == true)
            {
                return;
            }

            _wsListener = new HttpListener();
            _wsListener.Prefixes.Add($"http://127.0.0.1:{FixedWebSocketPort}/");
            _wsListener.Start();
            _wsLoopTask = Task.Run(() => WebSocketAcceptLoopAsync(_wsListener, _serviceCts?.Token ?? CancellationToken.None));
            _logger.Information("MOD WebSocket listener started at ws://127.0.0.1:{Port}", FixedWebSocketPort);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartHttpListenerAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_httpListener?.IsListening == true)
            {
                return;
            }

            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{FixedHttpPort}/");
            _httpListener.Start();
            _httpLoopTask = Task.Run(() => HttpAcceptLoopAsync(_httpListener, _serviceCts?.Token ?? CancellationToken.None));
            _logger.Information("MOD HTTP listener started at http://127.0.0.1:{Port}/api/event", FixedHttpPort);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartUdpWorkerAsync(int port)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts?.Token ?? CancellationToken.None);
        var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var worker = new UdpChannelWorker(port, udpClient, cts);
        if (!_udpWorkers.TryAdd(port, worker))
        {
            udpClient.Dispose();
            cts.Dispose();
            return;
        }

        worker.WorkerTask = Task.Run(() => UdpReceiveLoopAsync(worker), cts.Token);
        await Task.CompletedTask;
        _logger.Information("MOD UDP listener started at udp://127.0.0.1:{Port}", port);
    }

    private async Task WebSocketAcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Accept WebSocket context failed");
            }

            if (context == null)
            {
                continue;
            }

            _ = Task.Run(() => HandleWebSocketContextAsync(context, ct), ct);
        }
    }

    private async Task HandleWebSocketContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteHttpResponseAsync(context.Response, new { type = "error", code = "ws_upgrade_required", message = "WebSocket upgrade required" });
            return;
        }

        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Accept WebSocket request failed");
            return;
        }

        var socket = wsContext.WebSocket;
        var session = new ModBridgeSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Socket = socket,
            LastSeenAtUtc = DateTime.UtcNow,
            ConnectedAtUtc = DateTime.UtcNow
        };
        _sessions[session.SessionId] = session;

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveWebSocketMessageAsync(socket, ct);
                if (message == null)
                {
                    break;
                }

                var keepAlive = await HandleSocketMessageAsync(session, message, ct);
                if (!keepAlive)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (WebSocketException ex)
        {
            _logger.Debug(ex, "WebSocket connection closed abruptly");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Handle WebSocket session failed");
        }
        finally
        {
            await CloseSessionAsync(session, WebSocketCloseStatus.NormalClosure, "session-end");
        }
    }

    private async Task<string?> ReceiveWebSocketMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }

            if (ms.Length > 128 * 1024)
            {
                _logger.Warning("MOD websocket message too large, closing");
                return null;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task<bool> HandleSocketMessageAsync(ModBridgeSession session, string message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            await SendWebSocketJsonAsync(session.Socket, new { type = "error", code = "invalid_payload", message = "payload must be object" }, ct);
            return true;
        }

        if (IsRateLimited(session.SessionId))
        {
            await SendWebSocketJsonAsync(session.Socket, new { type = "error", code = "rate_limited", message = "too many messages" }, ct);
            return true;
        }

        var type = GetString(root, "type")?.Trim().ToLowerInvariant();
        switch (type)
        {
            case "hello":
                await HandleHelloMessageAsync(session, root, ct);
                return true;
            case "event":
                if (TryPublishEvent(root, session.SessionId, session.ScriptId, ModBridgeTransport.WebSocket, out var eventId, out var error))
                {
                    await SendWebSocketJsonAsync(session.Socket, new
                    {
                        type = "ack",
                        eventId,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }, ct);
                }
                else
                {
                    await SendWebSocketJsonAsync(session.Socket, new { type = "error", code = "invalid_event", message = error }, ct);
                }
                return true;
            case "heartbeat":
                session.LastSeenAtUtc = DateTime.UtcNow;
                await SendWebSocketJsonAsync(session.Socket, new { type = "heartbeat_ack", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
                return true;
            case "goodbye":
                await SendWebSocketJsonAsync(session.Socket, new { type = "bye", sessionId = session.SessionId }, ct);
                return false;
            default:
                await SendWebSocketJsonAsync(session.Socket, new { type = "error", code = "unknown_message_type", message = "supported: hello/event/heartbeat/goodbye" }, ct);
                return true;
        }
    }

    private async Task HandleHelloMessageAsync(ModBridgeSession session, JsonElement root, CancellationToken ct)
    {
        var scriptId = ResolveScriptId(root, session.ScriptId, ModBridgeTransport.WebSocket);
        session.ScriptId = scriptId;
        session.ModName = GetString(root, "name");
        session.GameName = GetString(root, "game");
        session.Version = GetString(root, "version");
        session.LastSeenAtUtc = DateTime.UtcNow;

        var rules = ParseRulesFromHello(root);
        var registerResult = _eventService.RegisterModRulesForSession(session.SessionId, rules, MaxRulesPerSession);

        var connectedDevices = _deviceManager
            .GetConnectedDevices()
            .Select(d => new { id = d.Id, name = d.Name, type = d.Type.ToString() })
            .ToArray();

        await SendWebSocketJsonAsync(session.Socket, new
        {
            type = "welcome",
            sessionId = session.SessionId,
            rulesAccepted = registerResult.AcceptedCount,
            rulesRejected = registerResult.RejectedCount,
            rejectedEventIds = registerResult.RejectedEventIds,
            connectedDevices
        }, ct);
    }

    private async Task HttpAcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Accept HTTP context failed");
            }

            if (context == null)
            {
                continue;
            }

            _ = Task.Run(() => HandleHttpContextAsync(context, ct), ct);
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        response.ContentType = "application/json; charset=utf-8";

        try
        {
            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }

            var absolutePath = request.Url?.AbsolutePath ?? string.Empty;
            if (!absolutePath.Equals("/api/event", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteHttpResponseAsync(response, new { type = "error", code = "not_found", message = "POST /api/event" });
                return;
            }

            if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                await WriteHttpResponseAsync(response, new { type = "error", code = "method_not_allowed", message = "POST only" });
                return;
            }

            if (IsRateLimited("http::loopback"))
            {
                response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                await WriteHttpResponseAsync(response, new { type = "error", code = "rate_limited", message = "too many messages" });
                return;
            }

            var body = await ReadBodyAsync(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteHttpResponseAsync(response, new { type = "error", code = "invalid_payload", message = "payload must be object" });
                return;
            }

            if (TryPublishEvent(root, sessionId: null, preferredScriptId: null, ModBridgeTransport.Http, out var eventId, out var error))
            {
                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteHttpResponseAsync(response, new
                {
                    type = "ack",
                    eventId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteHttpResponseAsync(response, new { type = "error", code = "invalid_event", message = error });
            }
        }
        catch (JsonException ex)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteHttpResponseAsync(response, new { type = "error", code = "invalid_json", message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Handle HTTP event failed");
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteHttpResponseAsync(response, new { type = "error", code = "internal_error", message = ex.Message });
        }
    }

    private async Task UdpReceiveLoopAsync(UdpChannelWorker worker)
    {
        _logger.Information("MOD UDP loop started at {Port}", worker.Port);
        while (!worker.Cts.IsCancellationRequested)
        {
            try
            {
                var result = await worker.Client.ReceiveAsync(worker.Cts.Token);
                if (result.Buffer.Length == 0)
                {
                    continue;
                }

                if (result.Buffer.Length > 4096)
                {
                    _logger.Warning("Discard UDP message over 4096 bytes from {Remote}", result.RemoteEndPoint);
                    continue;
                }

                if (IsRateLimited($"udp::{worker.Port}"))
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(result.Buffer);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await SendUdpJsonAsync(worker.Client, result.RemoteEndPoint, new
                    {
                        type = "error",
                        code = "invalid_payload",
                        message = "payload must be object"
                    });
                    continue;
                }

                await HandleUdpMessageAsync(worker, result.RemoteEndPoint, root);
                CleanupExpiredUdpSessions();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "MOD UDP loop error at {Port}", worker.Port);
            }
        }

        _logger.Information("MOD UDP loop stopped at {Port}", worker.Port);
    }

    private async Task HandleUdpMessageAsync(UdpChannelWorker worker, IPEndPoint remote, JsonElement root)
    {
        var type = GetString(root, "type")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(type))
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "error",
                code = "missing_type",
                message = "supported: hello/event/heartbeat/goodbye/template"
            });
            return;
        }

        switch (type)
        {
            case "hello":
                await HandleUdpHelloAsync(worker, remote, root);
                return;
            case "event":
                await HandleUdpEventAsync(worker, remote, root);
                return;
            case "heartbeat":
                await HandleUdpHeartbeatAsync(worker, remote, root);
                return;
            case "goodbye":
                await HandleUdpGoodbyeAsync(worker, remote, root);
                return;
            case "template":
                await SendUdpJsonAsync(worker.Client, remote, ModBridgeUdpProtocol.BuildTemplateResponse(worker.Port));
                return;
            default:
                await SendUdpJsonAsync(worker.Client, remote, new
                {
                    type = "error",
                    code = "unknown_message_type",
                    message = "supported: hello/event/heartbeat/goodbye/template"
                });
                return;
        }
    }

    private async Task HandleUdpHelloAsync(UdpChannelWorker worker, IPEndPoint remote, JsonElement root)
    {
        var remoteKey = BuildUdpRemoteKey(worker.Port, remote);
        var scriptId = ResolveScriptId(root, preferredScriptId: null, ModBridgeTransport.Udp);
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "error",
                code = "script_id_required",
                message = "hello must include scriptId when multiple scripts are active"
            });
            return;
        }

        if (_udpSessionByEndpoint.TryGetValue(remoteKey, out var existingSessionId) &&
            _udpSessions.TryGetValue(existingSessionId, out var existingSession))
        {
            RemoveUdpSession(existingSession);
        }

        var session = new UdpBridgeSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ScriptId = scriptId,
            ModName = GetString(root, "name"),
            GameName = GetString(root, "game"),
            Version = GetString(root, "version"),
            Port = worker.Port,
            RemoteKey = remoteKey,
            LastSeenAtUtc = DateTime.UtcNow,
            ConnectedAtUtc = DateTime.UtcNow
        };
        _udpSessions[session.SessionId] = session;
        _udpSessionByEndpoint[remoteKey] = session.SessionId;

        var rules = ParseRulesFromHello(root);
        var registerResult = _eventService.RegisterModRulesForSession(session.SessionId, rules, MaxRulesPerSession);

        await SendUdpJsonAsync(worker.Client, remote, new
        {
            type = "welcome",
            transport = "udp",
            sessionId = session.SessionId,
            scriptId = session.ScriptId,
            rulesAccepted = registerResult.AcceptedCount,
            rulesRejected = registerResult.RejectedCount,
            rejectedEventIds = registerResult.RejectedEventIds,
            heartbeatSeconds = UdpSessionIdleSeconds
        });
    }

    private async Task HandleUdpEventAsync(UdpChannelWorker worker, IPEndPoint remote, JsonElement root)
    {
        var session = ResolveUdpSession(worker.Port, remote, root);
        if (session == null)
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "error",
                code = "session_required",
                message = "send hello first to create udp session"
            });
            return;
        }

        session.LastSeenAtUtc = DateTime.UtcNow;
        if (IsRateLimited($"udp::{worker.Port}::{session.SessionId}"))
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "error",
                code = "rate_limited",
                message = "too many messages"
            });
            return;
        }

        if (TryPublishEvent(root, session.SessionId, session.ScriptId, ModBridgeTransport.Udp, out var eventId, out var error))
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "ack",
                transport = "udp",
                sessionId = session.SessionId,
                eventId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        else
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "error",
                code = "invalid_event",
                message = error
            });
        }
    }

    private async Task HandleUdpHeartbeatAsync(UdpChannelWorker worker, IPEndPoint remote, JsonElement root)
    {
        var session = ResolveUdpSession(worker.Port, remote, root);
        if (session == null)
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "error",
                code = "session_required",
                message = "send hello first to create udp session"
            });
            return;
        }

        session.LastSeenAtUtc = DateTime.UtcNow;
        await SendUdpJsonAsync(worker.Client, remote, new
        {
            type = "heartbeat_ack",
            transport = "udp",
            sessionId = session.SessionId,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private async Task HandleUdpGoodbyeAsync(UdpChannelWorker worker, IPEndPoint remote, JsonElement root)
    {
        var session = ResolveUdpSession(worker.Port, remote, root);
        if (session == null)
        {
            await SendUdpJsonAsync(worker.Client, remote, new
            {
                type = "bye",
                transport = "udp",
                sessionId = string.Empty
            });
            return;
        }

        var sessionId = session.SessionId;
        RemoveUdpSession(session);
        await SendUdpJsonAsync(worker.Client, remote, new
        {
            type = "bye",
            transport = "udp",
            sessionId
        });
    }

    private UdpBridgeSession? ResolveUdpSession(int port, IPEndPoint remote, JsonElement root)
    {
        var explicitSessionId = GetString(root, "sessionId");
        if (!string.IsNullOrWhiteSpace(explicitSessionId) &&
            _udpSessions.TryGetValue(explicitSessionId, out var explicitSession) &&
            explicitSession.Port == port)
        {
            return explicitSession;
        }

        var remoteKey = BuildUdpRemoteKey(port, remote);
        if (_udpSessionByEndpoint.TryGetValue(remoteKey, out var remoteSessionId) &&
            _udpSessions.TryGetValue(remoteSessionId, out var remoteSession))
        {
            return remoteSession;
        }

        return null;
    }

    private void CleanupExpiredUdpSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _udpSessions.Values.ToArray())
        {
            if ((now - session.LastSeenAtUtc).TotalSeconds <= UdpSessionIdleSeconds)
            {
                continue;
            }

            _logger.Debug("Expired UDP MOD session removed: {SessionId}", session.SessionId);
            RemoveUdpSession(session);
        }
    }

    private void RemoveUdpSession(UdpBridgeSession session)
    {
        _udpSessions.TryRemove(session.SessionId, out _);

        if (_udpSessionByEndpoint.TryGetValue(session.RemoteKey, out var mappedSessionId) &&
            string.Equals(mappedSessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            _udpSessionByEndpoint.TryRemove(session.RemoteKey, out _);
        }

        _eventService.UnregisterModRulesForSession(session.SessionId);
    }

    private static string BuildUdpRemoteKey(int port, IPEndPoint remote)
    {
        return $"{port}::{remote}";
    }

    private bool TryPublishEvent(
        JsonElement root,
        string? sessionId,
        string? preferredScriptId,
        ModBridgeTransport transport,
        out string eventId,
        out string? error)
    {
        eventId = string.Empty;
        error = null;

        var scriptId = ResolveScriptId(root, preferredScriptId, transport);
        ModBridgeMappedEvent? mapped = null;
        if (!string.IsNullOrWhiteSpace(scriptId) && _scriptMappers.TryGetValue(scriptId, out var mapper))
        {
            try
            {
                mapped = mapper(root);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Script mapper failed: {ScriptId}", scriptId);
            }
        }

        var evt = mapped != null
            ? BuildGameEventFromMapped(mapped, sessionId, scriptId, transport)
            : BuildGameEventFromPayload(root, sessionId, scriptId, transport);

        if (evt == null || string.IsNullOrWhiteSpace(evt.EventId))
        {
            error = "eventId is required";
            return false;
        }

        eventId = evt.EventId;
        _eventBus.PublishGameEvent(evt);
        return true;
    }

    private GameEvent? BuildGameEventFromPayload(
        JsonElement root,
        string? sessionId,
        string? scriptId,
        ModBridgeTransport transport)
    {
        var eventId = GetString(root, "eventId")?.Trim();
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        var source = GetString(root, "source")?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            source = BuildDefaultSource(scriptId, transport);
        }

        var gameEventType = ParseGameEventType(GetString(root, "gameEventType"));
        var oldValue = GetInt(root, "oldValue");
        var newValue = GetInt(root, "newValue");
        var targetDeviceId = GetString(root, "targetDeviceId");
        var data = root.TryGetProperty("data", out var dataElement)
            ? ConvertJsonObject(dataElement)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            data["modSessionId"] = sessionId;
        }

        if (!string.IsNullOrWhiteSpace(scriptId))
        {
            data["modScriptId"] = scriptId;
        }

        data["modTransport"] = transport.ToString().ToLowerInvariant();

        return new GameEvent
        {
            Type = gameEventType,
            EventId = eventId,
            Source = source,
            OldValue = oldValue,
            NewValue = newValue,
            TargetDeviceId = targetDeviceId,
            Data = data
        };
    }

    private static GameEvent BuildGameEventFromMapped(
        ModBridgeMappedEvent mapped,
        string? sessionId,
        string? scriptId,
        ModBridgeTransport transport)
    {
        var source = string.IsNullOrWhiteSpace(mapped.Source)
            ? BuildDefaultSource(scriptId, transport)
            : mapped.Source!;

        var data = mapped.Data != null
            ? new Dictionary<string, object>(mapped.Data, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            data["modSessionId"] = sessionId;
        }

        if (!string.IsNullOrWhiteSpace(scriptId))
        {
            data["modScriptId"] = scriptId;
        }

        data["modTransport"] = transport.ToString().ToLowerInvariant();
        if (mapped.Multiplier > 0)
        {
            data["multiplier"] = mapped.Multiplier.Value;
        }

        return new GameEvent
        {
            Type = mapped.EventType,
            EventId = mapped.EventId,
            Source = source,
            OldValue = mapped.OldValue,
            NewValue = mapped.NewValue,
            TargetDeviceId = mapped.TargetDeviceId,
            Data = data
        };
    }

    private List<EventRecord> ParseRulesFromHello(JsonElement root)
    {
        var rules = new List<EventRecord>();
        if (!root.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
        {
            return rules;
        }

        foreach (var ruleElement in rulesElement.EnumerateArray().Take(MaxRulesPerSession))
        {
            if (ruleElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var eventId = GetString(ruleElement, "eventId")?.Trim();
            if (string.IsNullOrWhiteSpace(eventId))
            {
                continue;
            }

            var action = GetString(ruleElement, "action")?.Trim().ToLowerInvariant();
            action = string.IsNullOrWhiteSpace(action) ? "set" : action;

            var channel = NormalizeChannel(GetString(ruleElement, "channel"));
            var value = GetInt(ruleElement, "value", GetInt(ruleElement, "strength", 10));
            var duration = GetInt(ruleElement, "duration", 0);
            var priority = GetInt(ruleElement, "priority", 20);
            var name = GetString(ruleElement, "name");
            var description = GetString(ruleElement, "description");
            var targetDeviceType = GetString(ruleElement, "targetDeviceType") ?? "All";
            var waveformData = GetString(ruleElement, "waveformData");
            var triggerType = GetString(ruleElement, "triggerType")
                ?? GetString(ruleElement, "conditionType")
                ?? "always";
            var minChange = GetInt(ruleElement, "minChange", GetInt(ruleElement, "min", 0));
            var maxChange = GetInt(ruleElement, "maxChange", GetInt(ruleElement, "max", 0));

            var conditionField = GetString(ruleElement, "conditionField");
            var conditionOperator = GetString(ruleElement, "conditionOperator");
            var conditionValue = GetDouble(ruleElement, "conditionValue");
            var conditionMaxValue = GetDouble(ruleElement, "conditionMaxValue")
                ?? GetDouble(ruleElement, "conditionValueMax");

            if (ruleElement.TryGetProperty("condition", out var conditionElement) &&
                conditionElement.ValueKind == JsonValueKind.Object)
            {
                triggerType = GetString(conditionElement, "type") ?? triggerType;
                conditionField = GetString(conditionElement, "field") ?? conditionField;
                conditionOperator = GetString(conditionElement, "operator") ?? conditionOperator;
                conditionValue = GetDouble(conditionElement, "value") ?? conditionValue;
                conditionMaxValue = GetDouble(conditionElement, "max")
                    ?? GetDouble(conditionElement, "valueMax")
                    ?? conditionMaxValue;
            }

            rules.Add(new EventRecord
            {
                EventId = eventId,
                Name = string.IsNullOrWhiteSpace(name) ? eventId : name!,
                Description = description,
                Category = "mod",
                Channel = channel,
                Action = action,
                ActionType = action,
                Value = value,
                Strength = value,
                Duration = duration,
                Priority = priority,
                TargetDeviceType = targetDeviceType,
                WaveformData = waveformData,
                TriggerType = triggerType,
                MinChange = Math.Max(0, minChange),
                MaxChange = Math.Max(0, maxChange),
                ConditionField = conditionField,
                ConditionOperator = conditionOperator,
                ConditionValue = conditionValue,
                ConditionMaxValue = conditionMaxValue,
                Enabled = true,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o")
            });
        }

        return rules;
    }

    private static string NormalizeChannel(string? channel)
    {
        return channel?.Trim().ToUpperInvariant() switch
        {
            "A" => "A",
            "B" => "B",
            "AB" => "AB",
            _ => "A"
        };
    }

    private string? ResolveScriptId(JsonElement payload, string? preferredScriptId, ModBridgeTransport transport)
    {
        if (!string.IsNullOrWhiteSpace(preferredScriptId))
        {
            return preferredScriptId;
        }

        var explicitScriptId = GetString(payload, "scriptId");
        if (!string.IsNullOrWhiteSpace(explicitScriptId))
        {
            return explicitScriptId;
        }

        var candidates = _scriptRegistrations.Values.Where(r => IsTransportEnabled(r, transport)).Select(r => r.ScriptId).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsTransportEnabled(ScriptBridgeRegistration registration, ModBridgeTransport transport)
    {
        lock (registration.SyncRoot)
        {
            return transport switch
            {
                ModBridgeTransport.WebSocket => registration.WebSocketEnabled,
                ModBridgeTransport.Http => registration.HttpEnabled,
                ModBridgeTransport.Udp => registration.UdpPorts.Count > 0,
                _ => false
            };
        }
    }

    private bool IsRateLimited(string key)
    {
        var now = DateTime.UtcNow;
        var window = _rateLimitWindows.GetOrAdd(key, _ => new RateLimitWindow());
        lock (window.SyncRoot)
        {
            if ((now - window.WindowStartUtc).TotalSeconds >= 1)
            {
                window.WindowStartUtc = now;
                window.Count = 0;
            }

            window.Count++;
            return window.Count > DefaultMaxMessagesPerSecond;
        }
    }

    private static string BuildDefaultSource(string? scriptId, ModBridgeTransport transport)
    {
        return string.IsNullOrWhiteSpace(scriptId)
            ? $"ModBridge.{transport}"
            : $"ModBridge.{transport}.{scriptId}";
    }

    private static GameEventType ParseGameEventType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return GameEventType.Custom;
        }

        return Enum.TryParse<GameEventType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : GameEventType.Custom;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int GetInt(JsonElement element, string propertyName, int defaultValue = 0)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static Dictionary<string, object> ConvertJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonValue(prop.Value) ?? string.Empty;
        }

        return dict;
    }

    private static object? ConvertJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => ConvertJsonObject(value),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToList(),
            _ => value.GetRawText()
        };
    }

    private async Task WriteHttpResponseAsync(HttpListenerResponse response, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    private static async Task<string> ReadBodyAsync(Stream stream, Encoding encoding, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task SendWebSocketJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task SendUdpJsonAsync(UdpClient client, IPEndPoint remote, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await client.SendAsync(bytes, bytes.Length, remote);
    }

    private async Task CloseSessionAsync(ModBridgeSession session, WebSocketCloseStatus status, string reason)
    {
        if (_sessions.TryRemove(session.SessionId, out _))
        {
            _eventService.UnregisterModRulesForSession(session.SessionId);
        }

        await CloseSessionSocketAsync(session.Socket, status, reason);
    }

    private static async Task CloseSessionSocketAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, reason, CancellationToken.None);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void StopWebSocketListenerUnsafe()
    {
        try
        {
            _wsListener?.Stop();
            _wsListener?.Close();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _wsListener = null;
            _wsLoopTask = null;
            _logger.Information("MOD WebSocket listener stopped");
        }
    }

    private void StopHttpListenerUnsafe()
    {
        try
        {
            _httpListener?.Stop();
            _httpListener?.Close();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _httpListener = null;
            _httpLoopTask = null;
            _logger.Information("MOD HTTP listener stopped");
        }
    }

    private async Task StopUdpWorkerAsync(int port)
    {
        if (!_udpWorkers.TryRemove(port, out var worker))
        {
            return;
        }

        try
        {
            worker.Cts.Cancel();
            worker.Client.Dispose();
            if (worker.WorkerTask != null)
            {
                await worker.WorkerTask;
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            worker.Cts.Dispose();
            _logger.Information("MOD UDP listener stopped at {Port}", port);
        }
    }

    private async Task StopAllUdpListenersUnsafe()
    {
        var ports = _udpWorkers.Keys.ToArray();
        foreach (var port in ports)
        {
            await StopUdpWorkerAsync(port);
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _lifecycleLock.Dispose();
    }

    private sealed class ScriptBridgeRegistration
    {
        public ScriptBridgeRegistration(string scriptId)
        {
            ScriptId = scriptId;
            ScriptName = scriptId;
        }

        public string ScriptId { get; }
        public string ScriptName { get; set; }
        public string? Version { get; set; }
        public bool WebSocketEnabled { get; set; }
        public bool HttpEnabled { get; set; }
        public HashSet<int> UdpPorts { get; } = new();
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
        public object SyncRoot { get; } = new();
    }

    private sealed class ModBridgeSession
    {
        public required string SessionId { get; init; }
        public required WebSocket Socket { get; init; }
        public string? ScriptId { get; set; }
        public string? ModName { get; set; }
        public string? GameName { get; set; }
        public string? Version { get; set; }
        public DateTime ConnectedAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; }
    }

    private sealed class UdpBridgeSession
    {
        public required string SessionId { get; init; }
        public required string ScriptId { get; init; }
        public string? ModName { get; init; }
        public string? GameName { get; init; }
        public string? Version { get; init; }
        public required int Port { get; init; }
        public required string RemoteKey { get; init; }
        public DateTime ConnectedAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; }
    }

    private sealed class RateLimitWindow
    {
        public DateTime WindowStartUtc { get; set; } = DateTime.UtcNow;
        public int Count { get; set; }
        public object SyncRoot { get; } = new();
    }

    private sealed class UdpChannelWorker
    {
        public UdpChannelWorker(int port, UdpClient client, CancellationTokenSource cts)
        {
            Port = port;
            Client = client;
            Cts = cts;
        }

        public int Port { get; }
        public UdpClient Client { get; }
        public CancellationTokenSource Cts { get; }
        public Task? WorkerTask { get; set; }
    }
}

public enum ModBridgeTransport
{
    WebSocket,
    Http,
    Udp
}

public sealed class ModBridgeMappedEvent
{
    public string EventId { get; set; } = string.Empty;
    public GameEventType EventType { get; set; } = GameEventType.Custom;
    public string? Source { get; set; }
    public int OldValue { get; set; }
    public int NewValue { get; set; }
    public double? Multiplier { get; set; }
    public string? TargetDeviceId { get; set; }
    public Dictionary<string, object>? Data { get; set; }
}

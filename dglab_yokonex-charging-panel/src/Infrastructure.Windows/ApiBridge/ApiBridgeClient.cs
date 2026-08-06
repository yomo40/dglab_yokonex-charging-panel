using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChargingPanel.Infrastructure.Windows.ApiBridge;

public sealed record ApiBridgeHealth(string Status, bool ImReady, string? Uid, string? UserId);

public sealed record ApiBridgeStatus(bool IsReady, string? Uid, string? UserId, string? AppId, bool HasToken, bool HasSign);

public sealed record ApiBridgeLoginResult(bool Success, string? Message, string? Uid, string? UserId, string? AppId);

public sealed record ApiBridgeSendResult(bool Success, string? Message);

/// <summary>
/// 官方 YOKONEX API Bridge 的 HTTP 面。覆盖 health/status/login/send-command/reinit。
/// 发送控制消息超时返回 OutcomeUnknown，绝不静默重发（重发由 Dispatcher 决定）。
/// </summary>
public sealed class ApiBridgeHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ApiBridgeHttpClient(Uri baseUri, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUri;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<ApiBridgeHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/health", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        return new ApiBridgeHealth(
            GetString(root, "status") ?? "unknown",
            root.TryGetProperty("imReady", out var ready) && ready.GetBoolean(),
            GetString(root, "uid"),
            GetString(root, "userId"));
    }

    public async Task<ApiBridgeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/api/status", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        var isReady = root.TryGetProperty("isReady", out var ready) && ready.GetBoolean();
        string? uid = null, userId = null, appId = null;
        var hasToken = false;
        var hasSign = false;
        if (root.TryGetProperty("config", out var config))
        {
            uid = GetString(config, "uid");
            userId = GetString(config, "userId");
            appId = GetString(config, "appId");
            hasToken = config.TryGetProperty("hasToken", out var token) && token.GetBoolean();
            hasSign = config.TryGetProperty("hasSign", out var sign) && sign.GetBoolean();
        }

        return new ApiBridgeStatus(isReady, uid, userId, appId, hasToken, hasSign);
    }

    public async Task<ApiBridgeLoginResult> LoginAsync(string uid, string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var payload = JsonSerializer.Serialize(new { uid, token });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var success = root.TryGetProperty("success", out var ok) && ok.GetBoolean();
        var message = GetString(root, "message");
        string? resultUid = null, resultUserId = null, resultAppId = null;
        if (root.TryGetProperty("data", out var data))
        {
            resultUid = GetString(data, "uid");
            resultUserId = GetString(data, "userId");
            resultAppId = GetString(data, "appId");
        }

        return new ApiBridgeLoginResult(success, message, resultUid, resultUserId, resultAppId);
    }

    /// <summary>
    /// 发送指令。HTTP 返回 success 只是 Bridge 已接受并转发，不等于物理设备已执行。
    /// </summary>
    public async Task<ApiBridgeSendResult> SendCommandAsync(string commandId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        var payload = JsonSerializer.Serialize(new { commandId });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/send-command")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new ApiBridgeSendResult(
            root.TryGetProperty("success", out var ok) && ok.GetBoolean(),
            GetString(root, "message"));
    }

    public async Task<ApiBridgeSendResult> ReinitAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reinit");
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new ApiBridgeSendResult(
            root.TryGetProperty("success", out var ok) && ok.GetBoolean(),
            GetString(root, "message"));
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() => _http.Dispose();
}

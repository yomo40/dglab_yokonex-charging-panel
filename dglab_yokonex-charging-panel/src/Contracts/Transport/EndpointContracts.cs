using System.Collections.ObjectModel;
using System.Text.Json;
using ChargingPanel.Contracts.Identifiers;

namespace ChargingPanel.Contracts.Transport;

public enum TransportKind
{
    BluetoothLowEnergy,
    WebSocket,
    Http,
    InstantMessaging,
    Virtual
}

public sealed record ConnectionEndpointDescriptor
{
    public ConnectionEndpointDescriptor(
        EndpointId endpointId,
        ProtocolProfileId profileId,
        TransportKind transportKind,
        string displayName,
        IReadOnlyDictionary<string, string> properties)
    {
        endpointId.ThrowIfInvalid(nameof(endpointId));
        profileId.ThrowIfInvalid(nameof(profileId));
        ContractGuards.ThrowIfUndefined(transportKind, nameof(transportKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(properties);

        EndpointId = endpointId;
        ProfileId = profileId;
        TransportKind = transportKind;
        DisplayName = displayName.Trim();
        Properties = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(properties, StringComparer.Ordinal));
    }

    public EndpointId EndpointId { get; }

    public ProtocolProfileId ProfileId { get; }

    public TransportKind TransportKind { get; }

    public string DisplayName { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }
}

public enum EndpointPayloadKind
{
    Binary,
    Json
}

public sealed record EndpointPayload
{
    private EndpointPayload(
        EndpointPayloadKind kind,
        ReadOnlyMemory<byte> binary,
        JsonElement? json)
    {
        Kind = kind;
        Binary = binary;
        Json = json;
    }

    public EndpointPayloadKind Kind { get; }

    public ReadOnlyMemory<byte> Binary { get; }

    public JsonElement? Json { get; }

    public static EndpointPayload FromBinary(ReadOnlyMemory<byte> value) =>
        new(EndpointPayloadKind.Binary, value.ToArray(), null);

    public static EndpointPayload FromJson(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Endpoint JSON 载荷不能为 Undefined 或 null。", nameof(value));
        }

        return new EndpointPayload(EndpointPayloadKind.Json, ReadOnlyMemory<byte>.Empty, value.Clone());
    }
}

public enum EndpointResponseMode
{
    WithResponse,
    WithoutResponse
}

public enum EndpointOperationStatus
{
    Completed,
    Rejected,
    Failed,
    OutcomeUnknown
}

public sealed record EndpointLeaseRequest
{
    public EndpointLeaseRequest(EndpointId endpointId, ProtocolProfileId profileId)
    {
        endpointId.ThrowIfInvalid(nameof(endpointId));
        profileId.ThrowIfInvalid(nameof(profileId));
        EndpointId = endpointId;
        ProfileId = profileId;
    }

    public EndpointId EndpointId { get; }

    public ProtocolProfileId ProfileId { get; }
}

public sealed record EndpointRequest
{
    public EndpointRequest(
        string route,
        EndpointPayload payload,
        EndpointResponseMode responseMode)
    {
        Route = EndpointContractGuards.ValidateRoute(route, nameof(route));
        ArgumentNullException.ThrowIfNull(payload);
        ContractGuards.ThrowIfUndefined(responseMode, nameof(responseMode));
        Payload = payload;
        ResponseMode = responseMode;
    }

    public string Route { get; }

    public EndpointPayload Payload { get; }

    public EndpointResponseMode ResponseMode { get; }
}

public sealed record EndpointReadRequest
{
    public EndpointReadRequest(string route) =>
        Route = EndpointContractGuards.ValidateRoute(route, nameof(route));

    public string Route { get; }
}

public sealed record EndpointSendResult
{
    public EndpointSendResult(
        EndpointOperationStatus status,
        EndpointPayload? response = null,
        string? errorCode = null)
    {
        Status = status;
        Response = response;
        ErrorCode = EndpointContractGuards.ValidateResult(status, errorCode);
    }

    public EndpointOperationStatus Status { get; }

    public EndpointPayload? Response { get; }

    public string? ErrorCode { get; }
}

public sealed record EndpointReadResult
{
    public EndpointReadResult(
        EndpointOperationStatus status,
        EndpointPayload? payload = null,
        string? errorCode = null)
    {
        Status = status;
        Payload = payload;
        ErrorCode = EndpointContractGuards.ValidateResult(status, errorCode);
    }

    public EndpointOperationStatus Status { get; }

    public EndpointPayload? Payload { get; }

    public string? ErrorCode { get; }
}

public sealed record EndpointMessage
{
    public EndpointMessage(
        string route,
        EndpointPayload payload,
        DateTimeOffset timestamp,
        long? sequenceNumber = null)
    {
        Route = EndpointContractGuards.ValidateRoute(route, nameof(route));
        ArgumentNullException.ThrowIfNull(payload);
        if (sequenceNumber is { } value)
        {
            ContractGuards.ThrowIfNegative(value, nameof(sequenceNumber));
        }

        Payload = payload;
        Timestamp = timestamp;
        SequenceNumber = sequenceNumber;
    }

    public string Route { get; }

    public EndpointPayload Payload { get; }

    public DateTimeOffset Timestamp { get; }

    public long? SequenceNumber { get; }
}

public interface IEndpointLeaseProvider
{
    ValueTask<IEndpointLease> AcquireAsync(
        EndpointLeaseRequest request,
        CancellationToken cancellationToken);
}

public interface IEndpointLease : IAsyncDisposable
{
    EndpointId EndpointId { get; }

    ProtocolProfileId ProfileId { get; }

    TransportKind TransportKind { get; }

    ValueTask<EndpointSendResult> SendAsync(
        EndpointRequest request,
        CancellationToken cancellationToken);

    ValueTask<EndpointReadResult> ReadAsync(
        EndpointReadRequest request,
        CancellationToken cancellationToken);
}

public interface IEndpointMessageSource
{
    IAsyncEnumerable<EndpointMessage> ReadMessagesAsync(CancellationToken cancellationToken);
}

internal static class EndpointContractGuards
{
    public static string ValidateRoute(string route, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route, parameterName);
        if (!string.Equals(route, route.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Endpoint 路由不能包含边缘空白。", parameterName);
        }

        return route;
    }

    public static string? ValidateResult(EndpointOperationStatus status, string? errorCode)
    {
        ContractGuards.ThrowIfUndefined(status, nameof(status));
        var normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
        if (normalizedErrorCode is not null)
        {
            normalizedErrorCode = StableContractToken.Validate(normalizedErrorCode, nameof(errorCode));
        }

        if (status == EndpointOperationStatus.Completed && normalizedErrorCode is not null)
        {
            throw new ArgumentException("成功的 Endpoint 操作不能携带错误代码。", nameof(errorCode));
        }

        if (status != EndpointOperationStatus.Completed && normalizedErrorCode is null)
        {
            throw new ArgumentException("非成功 Endpoint 操作必须携带错误代码。", nameof(errorCode));
        }

        return normalizedErrorCode;
    }
}

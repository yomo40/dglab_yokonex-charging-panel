namespace ChargingPanel.Core.Security;

public interface ISecretStore
{
    ValueTask<string> SaveAsync(ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>?> ReadAsync(string secretReference, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string secretReference, CancellationToken cancellationToken = default);
}

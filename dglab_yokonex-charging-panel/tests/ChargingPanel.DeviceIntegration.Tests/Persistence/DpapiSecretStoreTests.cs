using System.Text;
using ChargingPanel.Infrastructure.Windows.Security;
using Xunit;

namespace ChargingPanel.DeviceIntegration.Tests.Persistence;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ChargingPanel.DpapiSecretStoreTests",
        Guid.NewGuid().ToString("N"));

    public DpapiSecretStoreTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task SaveReadDeleteAsync_SyntheticSecret_RoundTripsAcrossStoreInstances()
    {
        var source = Encoding.UTF8.GetBytes("synthetic-secret-value");
        var firstStore = new DpapiSecretStore(_temporaryDirectory);

        var reference = await firstStore.SaveAsync(source, CancellationToken.None);
        source.AsSpan().Fill(0);
        var secondStore = new DpapiSecretStore(_temporaryDirectory);
        var restored = await secondStore.ReadAsync(reference, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("synthetic-secret-value", Encoding.UTF8.GetString(restored!.Value.Span));
        Assert.Matches("^secret-[0-9a-f]{32}$", reference);
        Assert.Single(Directory.GetFiles(_temporaryDirectory, "*.bin"));
        Assert.Empty(Directory.GetFiles(_temporaryDirectory, "*.tmp-*"));

        await secondStore.DeleteAsync(reference, CancellationToken.None);
        Assert.Null(await firstStore.ReadAsync(reference, CancellationToken.None));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("secret-00000000000000000000000000000000.bin")]
    [InlineData("")]
    public async Task ReadAsync_InvalidReference_RejectsPathTraversal(string reference)
    {
        var store = new DpapiSecretStore(_temporaryDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(reference, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(reference, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task OperationsAsync_PreCanceledToken_PropagatesCancellationWithoutFiles()
    {
        var store = new DpapiSecretStore(_temporaryDirectory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(Encoding.UTF8.GetBytes("synthetic"), cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReadAsync("secret-00000000000000000000000000000000", cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeleteAsync("secret-00000000000000000000000000000000", cancellation.Token).AsTask());
        Assert.Empty(Directory.GetFiles(_temporaryDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

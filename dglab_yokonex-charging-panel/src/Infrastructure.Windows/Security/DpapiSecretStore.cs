using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ChargingPanel.Core.Security;

namespace ChargingPanel.Infrastructure.Windows.Security;

public sealed partial class DpapiSecretStore : ISecretStore
{
    private readonly string _storageDirectory;

    public DpapiSecretStore(string storageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        _storageDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(_storageDirectory);
    }

    public async ValueTask<string> SaveAsync(
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (secret.IsEmpty)
        {
            throw new ArgumentException("密钥内容不能为空。", nameof(secret));
        }

        var secretReference = $"secret-{Guid.NewGuid():N}";
        var destinationPath = GetSecretPath(secretReference);
        var temporaryPath = destinationPath + $".tmp-{Guid.NewGuid():N}";
        var plaintext = secret.ToArray();
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath);
            return secretReference;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSecretPath(secretReference);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public ValueTask DeleteAsync(
        string secretReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSecretPath(secretReference);
        File.Delete(path);
        return ValueTask.CompletedTask;
    }

    private string GetSecretPath(string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        if (!SecretReferencePattern().IsMatch(secretReference))
        {
            throw new ArgumentException("密钥引用格式无效。", nameof(secretReference));
        }

        var path = Path.GetFullPath(Path.Combine(_storageDirectory, secretReference + ".bin"));
        if (!string.Equals(Path.GetDirectoryName(path), _storageDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("密钥引用不能越过存储目录。", nameof(secretReference));
        }

        return path;
    }

    [GeneratedRegex("^secret-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed class DpapiSecretStore(string path) : ISecretStore, IDisposable
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("ZVT2SumUp|Secrets|v1|LocalMachine"));
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Path { get; } = path;

    public async Task<GatewaySecrets> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Der Secret Store benötigt Windows DPAPI.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path)) return new();
            byte[] encrypted = await File.ReadAllBytesAsync(Path, cancellationToken).ConfigureAwait(false);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
            try { return JsonSerializer.Deserialize<GatewaySecrets>(clear) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (CryptographicException exception) { throw new InvalidDataException("Secrets konnten mit Windows DPAPI nicht entschlüsselt werden.", exception); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(GatewaySecrets secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Der Secret Store benötigt Windows DPAPI.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidDataException("Secret-Dateipfad ohne Verzeichnis.");
            Directory.CreateDirectory(directory);
            // Die ACL muss vor dem ersten Schreiben stehen. Andernfalls könnte
            // selbst eine verschlüsselte LocalMachine-DPAPI-Datei kurzzeitig
            // von weiteren lokalen Konten gelesen werden.
            AtomicFile.HardenProgramDataAcl(directory);
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(secrets);
            try
            {
                byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
                await AtomicFile.WriteAllBytesAsync(Path, encrypted, cancellationToken).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}

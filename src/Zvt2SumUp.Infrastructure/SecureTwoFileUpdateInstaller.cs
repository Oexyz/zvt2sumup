using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed class SecureTwoFileUpdateInstaller
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] ExecutableNames =
    [
        SecureReleaseUpdateService.GatewayExecutableName,
        SecureReleaseUpdateService.ToolsExecutableName
    ];

    private readonly string allowedUpdateRoot;
    private readonly long minimumExecutableBytes;

    public SecureTwoFileUpdateInstaller(string? allowedUpdateRoot = null, long minimumExecutableBytes = 1024 * 1024)
    {
        this.allowedUpdateRoot = Path.GetFullPath(allowedUpdateRoot ?? AppPaths.Updates);
        this.minimumExecutableBytes = minimumExecutableBytes;
    }

    public UpdateTransaction Apply(UpdateApplyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string target = ValidateTarget(plan.TargetDirectory);
        string staging = ValidateUpdateDirectory(plan.StagingDirectory, "Staging");
        string payload = ValidateUpdateDirectory(plan.PayloadDirectory, "Payload");
        string stagingPrefix = staging.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!payload.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update-Payload liegt nicht innerhalb des geschützten Stagings.");
        if (!Version.TryParse(plan.Version, out _)) throw new InvalidDataException("Updateplan enthält keine gültige Version.");

        Dictionary<string, string> expectedHashes = new(StringComparer.Ordinal)
        {
            [SecureReleaseUpdateService.GatewayExecutableName] = NormalizeHash(plan.GatewaySha256),
            [SecureReleaseUpdateService.ToolsExecutableName] = NormalizeHash(plan.ToolsSha256)
        };
        ValidateExactFiles(payload, expectedHashes);
        ValidateInstalledPackage(target);

        string operationId = Guid.NewGuid().ToString("N");
        string backup = Path.Combine(target, $".zvt2sumup-update-rollback-{operationId}");
        Directory.CreateDirectory(backup);
        Dictionary<string, string> temporary = [];
        List<string> movedOriginals = [];
        List<string> installed = [];

        try
        {
            foreach (string name in ExecutableNames)
            {
                string destination = Path.Combine(target, $".{name}.{operationId}.new");
                CopyVerified(Path.Combine(payload, name), destination, expectedHashes[name]);
                temporary[name] = destination;
            }

            foreach (string name in ExecutableNames)
            {
                File.Move(Path.Combine(target, name), Path.Combine(backup, name));
                movedOriginals.Add(name);
            }
            foreach (string name in ExecutableNames)
            {
                File.Move(temporary[name], Path.Combine(target, name));
                installed.Add(name);
            }
            foreach (string name in ExecutableNames)
                VerifyFile(Path.Combine(target, name), expectedHashes[name]);

            return new UpdateTransaction(target, backup, ExecutableNames);
        }
        catch
        {
            foreach (string name in installed.AsEnumerable().Reverse())
            {
                string current = Path.Combine(target, name);
                if (File.Exists(current)) File.Delete(current);
            }
            foreach (string name in movedOriginals.AsEnumerable().Reverse())
            {
                string original = Path.Combine(backup, name);
                if (File.Exists(original)) File.Move(original, Path.Combine(target, name));
            }
            foreach (string path in temporary.Values)
                if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            throw;
        }
    }

    public static async Task WritePlanAsync(string path, UpdateApplyPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(plan, PlanJsonOptions);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new InvalidDataException("Updateplan besitzt keinen Ordner.");
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Der Plan muss vor dem Start des privilegierten Runners dauerhaft geschrieben sein.
            output.Flush(flushToDisk: true);
#pragma warning restore CA1849
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<UpdateApplyPlan> ReadPlanAsync(string path, CancellationToken cancellationToken = default)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("Updateplan fehlt oder ist unerwartet groß.");
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<UpdateApplyPlan>(input, PlanJsonOptions, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("Updateplan ist ungültig.");
    }

    private static string ValidateTarget(string directory)
    {
        string full = Path.GetFullPath(directory);
        DirectoryInfo info = new(full);
        UpdateDirectorySecurity.EnsureNoReparsePoints(full);
        if (!info.Exists)
            throw new InvalidDataException("Installationsordner fehlt oder ist ein Umleitungspunkt.");
        return full;
    }

    private string ValidateUpdateDirectory(string directory, string label)
    {
        string root = allowedUpdateRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(directory);
        DirectoryInfo info = new(full);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !info.Exists)
            throw new InvalidDataException($"{label}-Ordner ist nicht vertrauenswürdig.");
        UpdateDirectorySecurity.EnsureNoReparsePoints(full);
        return full;
    }

    private void ValidateExactFiles(string payload, Dictionary<string, string> expectedHashes)
    {
        FileInfo[] files = new DirectoryInfo(payload).GetFiles();
        if (files.Length != 2 || !files.Select(x => x.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(ExecutableNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Update-Payload muss exakt die beiden freigegebenen EXE-Dateien enthalten.");
        foreach (FileInfo file in files)
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Update-Payload enthält einen Umleitungspunkt.");
            VerifyFile(file.FullName, expectedHashes[file.Name]);
        }
    }

    private static void ValidateInstalledPackage(string target)
    {
        FileInfo[] files = new DirectoryInfo(target).GetFiles();
        if (files.Length != 2 || !files.Select(x => x.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(ExecutableNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Automatische Installation ist nur im unveränderten Zwei-EXE-Paket zulässig.");
        if (files.Any(x => x.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException("Installationspaket enthält einen Umleitungspunkt.");
    }

    private void CopyVerified(string source, string destination, string expectedHash)
    {
        using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        VerifyFile(destination, expectedHash);
    }

    private void VerifyFile(string path, string expectedHash)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length < minimumExecutableBytes) throw new InvalidDataException("Update-EXE fehlt oder ist unerwartet klein.");
        using (FileStream input = File.OpenRead(path))
        using (PEReader pe = new(input))
        {
            if (pe.PEHeaders.PEHeader is null || pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
                throw new InvalidDataException("Update-Datei ist keine ausführbare Windows-PE-Datei.");
        }
        using FileStream hashInput = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(hashInput));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(actual)))
            throw new CryptographicException("Update-EXE wurde nach der Prüfung verändert.");
    }

    private static string NormalizeHash(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("Updateplan enthält eine ungültige SHA-256-Prüfsumme.");
        return value.ToUpperInvariant();
    }
}

public sealed class UpdateTransaction : IDisposable
{
    private readonly string target;
    private readonly string backup;
    private readonly string[] executableNames;
    private bool finished;

    internal UpdateTransaction(string target, string backup, string[] executableNames)
    {
        this.target = target;
        this.backup = backup;
        this.executableNames = executableNames;
    }

    public void Commit()
    {
        if (finished) return;
        finished = true;
        try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Rollback()
    {
        if (finished) return;
        foreach (string name in executableNames)
        {
            string current = Path.Combine(target, name);
            string original = Path.Combine(backup, name);
            if (!File.Exists(original)) continue;
            if (File.Exists(current)) File.Delete(current);
            File.Move(original, current);
        }
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        finished = true;
    }

    public void Dispose()
    {
        if (!finished) Rollback();
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed partial class SecureReleaseUpdateService : IUpdateService
{
    public const string PackageAssetName = "ZVT2SumUp-win-x64.zip";
    public const string ChecksumsAssetName = "checksums.sha256";
    public const string GatewayExecutableName = "ZVT2SumUpGateway.exe";
    public const string ToolsExecutableName = "ZVT2SumUp.Tools.exe";

    private const long MaximumReleaseJsonBytes = 4 * 1024 * 1024;
    private const long MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"
    };

    private readonly HttpClient http;
    private readonly GatewayOptions options;
    private readonly ILogger<SecureReleaseUpdateService> logger;
    private readonly string updateRoot;
    private readonly bool hardenStaging;
    private readonly long minimumExecutableBytes;

#pragma warning disable CA1812 // Diese DTOs werden ausschließlich durch System.Text.Json instanziiert.
    private sealed record GitHubAsset([property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri Url);

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("body")] string Notes,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);
#pragma warning restore CA1812

    public SecureReleaseUpdateService(HttpClient http, GatewayOptions options, ILogger<SecureReleaseUpdateService> logger)
        : this(http, options, logger, AppPaths.Updates, hardenStaging: true, minimumExecutableBytes: 1024 * 1024) { }

    public SecureReleaseUpdateService(HttpClient http, GatewayOptions options, ILogger<SecureReleaseUpdateService> logger,
        string updateRoot, bool hardenStaging, long minimumExecutableBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(updateRoot);
        this.http = http;
        this.options = options;
        this.logger = logger;
        this.updateRoot = Path.GetFullPath(updateRoot);
        this.hardenStaging = hardenStaging;
        this.minimumExecutableBytes = minimumExecutableBytes;
    }

    public async Task<UpdateInformation> CheckAsync(CancellationToken cancellationToken = default)
    {
        Version local = typeof(SecureReleaseUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
        if (!IsRepository(options.UpdateRepository))
            return new(false, local, null, string.Empty, null, null, "Ungültiges GitHub-Repository.");

        try
        {
            Uri releaseUri = new(http.BaseAddress ?? new Uri("https://api.github.com/"),
                $"repos/{options.UpdateRepository}/releases/latest");
            ValidateDownloadUri(releaseUri);
            byte[] json = await DownloadBytesAsync(releaseUri, MaximumReleaseJsonBytes, cancellationToken).ConfigureAwait(false);
            GitHubRelease? release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release is null || release.Draft || release.Prerelease)
                return new(false, local, null, string.Empty, null, null, "Kein stabiles Release verfügbar.");
            if (!TryParseVersion(release.Tag, out Version? remote))
                return new(false, local, null, release.Notes, null, null, "Release-Version ist nicht semantisch lesbar.");

            GitHubAsset[] assets = release.Assets ?? [];
            GitHubAsset? package = assets.SingleOrDefault(x => x.Name.Equals(PackageAssetName, StringComparison.Ordinal));
            GitHubAsset? checksums = assets.SingleOrDefault(x => x.Name.Equals(ChecksumsAssetName, StringComparison.Ordinal));
            bool available = remote > local;
            string error = string.Empty;
            if (package is null || checksums is null)
                error = "Release abgelehnt: Exaktes Paket oder SHA-256-Manifest fehlt.";
            else
            {
                try
                {
                    ValidateReleaseAssetUri(package!.Url, options.UpdateRepository, remote!, PackageAssetName);
                    ValidateReleaseAssetUri(checksums!.Url, options.UpdateRepository, remote!, ChecksumsAssetName);
                }
                catch (InvalidDataException exception) { error = exception.Message; }
            }

            return new(available, local, remote, release.Notes ?? string.Empty, package?.Url, checksums?.Url, error);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or InvalidDataException or InvalidOperationException)
        {
            return new(false, local, null, string.Empty, null, null,
                "Updateprüfung fehlgeschlagen: " + SensitiveDataRedactor.Redact(exception.Message));
        }
    }

    public async Task<PreparedUpdate> PrepareAsync(UpdateInformation information, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);
        if (!information.Available || information.RemoteVersion is null || information.PackageUrl is null ||
            information.ChecksumsUrl is null || !string.IsNullOrEmpty(information.Error))
            throw new InvalidOperationException("Kein verifizierbares Update verfügbar.");

        ValidateReleaseAssetUri(information.PackageUrl, options.UpdateRepository, information.RemoteVersion, PackageAssetName);
        ValidateReleaseAssetUri(information.ChecksumsUrl, options.UpdateRepository, information.RemoteVersion, ChecksumsAssetName);

        Directory.CreateDirectory(updateRoot);
        UpdateDirectorySecurity.EnsureNoReparsePoints(updateRoot);
        string staging = Path.Combine(updateRoot, $"v{NormalizeVersion(information.RemoteVersion)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            if (hardenStaging) UpdateDirectorySecurity.HardenAdminOnly(staging, updateRoot);

            byte[] manifestBytes = await DownloadBytesAsync(information.ChecksumsUrl, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
            string manifest = new UTF8Encoding(false, true).GetString(manifestBytes);
            string expectedPackageHash = ParseChecksum(manifest, PackageAssetName) ??
                throw new InvalidDataException("SHA-256-Manifest enthält keinen eindeutigen Eintrag für das erwartete Paket.");

            string temporaryPackage = Path.Combine(staging, PackageAssetName + ".download");
            await DownloadFileAsync(information.PackageUrl, temporaryPackage, MaximumPackageBytes, cancellationToken).ConfigureAwait(false);
            string actualPackageHash = await HashFileAsync(temporaryPackage, cancellationToken).ConfigureAwait(false);
            if (!FixedTimeHashEquals(expectedPackageHash, actualPackageHash))
                throw new CryptographicException("SHA-256-Prüfsumme des Updatepakets stimmt nicht überein.");

            string package = Path.Combine(staging, PackageAssetName);
            File.Move(temporaryPackage, package);
            string payload = Path.Combine(staging, "payload");
            Directory.CreateDirectory(payload);
            await ExtractExactPayloadAsync(package, payload, cancellationToken).ConfigureAwait(false);

            string gateway = Path.Combine(payload, GatewayExecutableName);
            string tools = Path.Combine(payload, ToolsExecutableName);
            ValidateExecutable(gateway, information.RemoteVersion);
            ValidateExecutable(tools, information.RemoteVersion);
            string gatewayHash = await HashFileAsync(gateway, cancellationToken).ConfigureAwait(false);
            string toolsHash = await HashFileAsync(tools, cancellationToken).ConfigureAwait(false);

            PreparedUpdate prepared = new(information.RemoteVersion, staging, payload,
                actualPackageHash, gatewayHash, toolsHash);
            LogUpdatePrepared(logger, information.RemoteVersion);
            return prepared;
        }
        catch
        {
            DeleteStagingDirectory(staging);
            throw;
        }
    }

    private async Task<byte[]> DownloadBytesAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        ValidateResponse(response, maximumBytes);
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        await CopyLimitedAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task DownloadFileAsync(Uri uri, string path, long maximumBytes, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        ValidateResponse(response, maximumBytes);
        using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await CopyLimitedAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Flush(true) erzwingt die für Updateintegrität erforderliche Datenträgerpersistenz.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("Downloaddaten überschreiten die zulässige Größe.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExtractExactPayloadAsync(string package, string payload, CancellationToken cancellationToken)
    {
        using ZipArchive archive = await ZipFile.OpenReadAsync(package, cancellationToken).ConfigureAwait(false);
        ZipArchiveEntry[] entries = archive.Entries.ToArray();
        string[] expected = [GatewayExecutableName, ToolsExecutableName];
        if (entries.Length != 2 || entries.Any(x => string.IsNullOrEmpty(x.Name)) ||
            !entries.Select(x => x.FullName).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Update-ZIP muss exakt die beiden freigegebenen EXE-Dateien im Stamm enthalten.");
        if (entries.Sum(x => x.Length) > MaximumPackageBytes) throw new InvalidDataException("Entpackte Updatedaten überschreiten die zulässige Gesamtgröße.");

        foreach (ZipArchiveEntry entry in entries)
        {
            if (!entry.FullName.Equals(entry.Name, StringComparison.Ordinal) || entry.Length < minimumExecutableBytes || entry.Length > MaximumPackageBytes)
                throw new InvalidDataException("Unsicherer oder unerwarteter ZIP-Eintrag.");
            string destination = Path.Combine(payload, entry.Name);
            using Stream input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await CopyLimitedAsync(input, output, MaximumPackageBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Flush(true) verhindert ein bestätigtes, aber noch nicht persistiertes Update.
            output.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
    }

    private void ValidateExecutable(string path, Version expectedVersion)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length < minimumExecutableBytes) throw new InvalidDataException("Update enthält keine vollständige self-contained EXE.");
        using (FileStream input = File.OpenRead(path))
        using (PEReader pe = new(input))
        {
            if (pe.PEHeaders.PEHeader is null || pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll))
                throw new InvalidDataException("Update enthält keine gültige ausführbare Windows-PE-Datei.");
        }
        FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
        string? productVersion = versionInfo.ProductVersion?.Split('-', '+', ' ')[0];
        string? fileVersionText = versionInfo.FileVersion?.Split(' ')[0];
        Version? fileVersion = Version.TryParse(productVersion, out Version? productParsed) ? productParsed :
            Version.TryParse(fileVersionText, out Version? fileParsed) ? fileParsed : null;
        if (fileVersion is null || NormalizeVersion(fileVersion) != NormalizeVersion(expectedVersion))
            throw new InvalidDataException($"EXE-Version stimmt nicht mit Release {NormalizeVersion(expectedVersion)} überein.");
    }

    private static void ValidateResponse(HttpResponseMessage response, long maximumBytes)
    {
        response.EnsureSuccessStatusCode();
        Uri finalUri = response.RequestMessage?.RequestUri ?? throw new InvalidDataException("Downloadziel konnte nicht bestätigt werden.");
        ValidateDownloadUri(finalUri);
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new InvalidDataException("Downloaddaten überschreiten die zulässige Größe.");
    }

    private static string? ParseChecksum(string manifest, string expectedFileName)
    {
        List<string> matches = [];
        foreach (string rawLine in manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0) continue;
            string hash = line[..separator];
            string name = line[separator..].Trim().TrimStart('*');
            if (name.Equals(expectedFileName, StringComparison.Ordinal)) matches.Add(hash);
        }
        return matches.Count == 1 && matches[0].Length == 64 && matches[0].All(Uri.IsHexDigit)
            ? matches[0].ToUpperInvariant()
            : null;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
    }

    private static bool FixedTimeHashEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));

    private static bool IsRepository(string value)
    {
        string[] parts = value.Split('/');
        return parts.Length == 2 && parts.All(x => x.Length > 0 && x.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    private static bool TryParseVersion(string value, out Version? version) =>
        Version.TryParse(value.TrimStart('v', 'V').Split('-', '+')[0], out version);

    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedDownloadHosts.Contains(uri.IdnHost))
            throw new InvalidDataException("Update abgelehnt: Download ist nur von fest erlaubten GitHub-HTTPS-Hosts zulässig.");
    }

    private static void ValidateReleaseAssetUri(Uri uri, string repository, Version version, string expectedFileName)
    {
        ValidateDownloadUri(uri);
        string[] repositoryParts = repository.Split('/');
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        bool valid = uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            repositoryParts.Length == 2 && segments.Length == 6 &&
            segments[0].Equals(repositoryParts[0], StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals(repositoryParts[1], StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("releases", StringComparison.Ordinal) &&
            segments[3].Equals("download", StringComparison.Ordinal) &&
            TryParseVersion(segments[4], out Version? assetVersion) &&
            NormalizeVersion(assetVersion!) == NormalizeVersion(version) &&
            segments[5].Equals(expectedFileName, StringComparison.Ordinal);
        if (!valid)
            throw new InvalidDataException("Release-Asset gehört nicht exakt zum konfigurierten GitHub-Repository, zur Version und zum festen Dateinamen.");
    }

    private void DeleteStagingDirectory(string staging)
    {
        string root = updateRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(staging);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "GitHub-Update {Version} vollständig geprüft und bereitgestellt.")]
    private static partial void LogUpdatePrepared(ILogger logger, Version version);
}

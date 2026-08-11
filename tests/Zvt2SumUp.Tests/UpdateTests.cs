using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;

namespace Zvt2SumUp.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "zvt2sumup-update-tests", Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ReleaseWithoutChecksumIsRejected()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package, includeChecksumAsset: false);
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));

        UpdateInformation result = await service.CheckAsync();

        Assert.True(result.Available);
        Assert.Contains("SHA-256", result.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(result));
    }

    [Fact]
    public async Task InvalidRepositoryIsRejectedBeforeNetwork()
    {
        RouteHandler handler = new(_ => throw new InvalidOperationException("Netzwerk darf nicht aufgerufen werden."));
        SecureReleaseUpdateService service = new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            new GatewayOptions { UpdateRepository = "https://evil.invalid/x" }, NullLogger<SecureReleaseUpdateService>.Instance,
            Path.Combine(root, "updates"), hardenStaging: false, minimumExecutableBytes: 1);

        UpdateInformation result = await service.CheckAsync();

        Assert.Contains("Ungültiges", result.Error);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task PrepareVerifiesChecksumVersionAndExactPayload()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package);
        string updates = Path.Combine(root, "updates");
        SecureReleaseUpdateService service = CreateService(handler, updates);

        UpdateInformation information = await service.CheckAsync();
        PreparedUpdate prepared = await service.PrepareAsync(information);

        Assert.Equal(Normalize(version), Normalize(prepared.Version));
        Assert.Equal(SHA256Hex(package), prepared.PackageSha256);
        string[] files = Directory.GetFiles(prepared.PayloadDirectory).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal([SecureReleaseUpdateService.ToolsExecutableName, SecureReleaseUpdateService.GatewayExecutableName], files);
        Assert.Equal(SHA256Hex(File.ReadAllBytes(Path.Combine(prepared.PayloadDirectory, SecureReleaseUpdateService.GatewayExecutableName))), prepared.GatewaySha256);
    }

    [Fact]
    public async Task ChecksumMismatchIsRejectedAndStagingIsRemoved()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package, manifestOverride: new string('0', 64) + "  " + SecureReleaseUpdateService.PackageAssetName);
        string updates = Path.Combine(root, "updates");
        SecureReleaseUpdateService service = CreateService(handler, updates);
        UpdateInformation information = await service.CheckAsync();

        await Assert.ThrowsAsync<CryptographicException>(() => service.PrepareAsync(information));

        Assert.Empty(Directory.GetDirectories(updates));
    }

    [Fact]
    public async Task AmbiguousChecksumManifestIsRejected()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        string line = SHA256Hex(package) + "  " + SecureReleaseUpdateService.PackageAssetName;
        RouteHandler handler = CreateHandler(version, package, manifestOverride: line + "\n" + line);
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));
        UpdateInformation information = await service.CheckAsync();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(information));

        Assert.Contains("eindeutigen", error.Message);
    }

    [Fact]
    public async Task PackageWithAdditionalEntryIsRejected()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: true);
        RouteHandler handler = CreateHandler(version, package);
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));
        UpdateInformation information = await service.CheckAsync();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(information));

        Assert.Contains("exakt", error.Message);
    }

    [Fact]
    public async Task NonGithubAssetHostIsRejectedBeforeAssetDownload()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package, assetHost: "evil.github.com");
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));

        UpdateInformation information = await service.CheckAsync();

        Assert.Contains("GitHub-HTTPS-Hosts", information.Error);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task AssetFromDifferentRepositoryIsRejectedBeforeAssetDownload()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package, assetRepository: "other/repo");
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));

        UpdateInformation information = await service.CheckAsync();

        Assert.Contains("konfigurierten GitHub-Repository", information.Error);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task AssetWithMismatchedVersionIsRejectedBeforeAssetDownload()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package, assetVersion: new Version(version.Major + 1, 0, 0));
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));

        UpdateInformation information = await service.CheckAsync();

        Assert.Contains("zur Version", information.Error);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RedirectAwayFromGithubIsRejected()
    {
        Version version = GetExecutableVersion();
        byte[] package = CreatePackage(File.ReadAllBytes(TestExecutablePath()), extraEntry: false);
        RouteHandler handler = CreateHandler(version, package, finalManifestHost: "downloads.invalid");
        SecureReleaseUpdateService service = CreateService(handler, Path.Combine(root, "updates"));
        UpdateInformation information = await service.CheckAsync();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(information));

        Assert.Contains("GitHub-HTTPS-Hosts", error.Message);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public void TwoFileInstallerSupportsCommitAndRollback()
    {
        byte[] executable = File.ReadAllBytes(TestExecutablePath());
        string allowed = Path.Combine(root, "allowed");
        string staging = Path.Combine(allowed, "stage");
        string payload = Path.Combine(staging, "payload");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(target);

        byte[] oldGateway = [.. executable, 0x11];
        byte[] oldTools = [.. executable, 0x12];
        byte[] newGateway = [.. executable, 0x21];
        byte[] newTools = [.. executable, 0x22];
        File.WriteAllBytes(Path.Combine(target, SecureReleaseUpdateService.GatewayExecutableName), oldGateway);
        File.WriteAllBytes(Path.Combine(target, SecureReleaseUpdateService.ToolsExecutableName), oldTools);
        File.WriteAllBytes(Path.Combine(payload, SecureReleaseUpdateService.GatewayExecutableName), newGateway);
        File.WriteAllBytes(Path.Combine(payload, SecureReleaseUpdateService.ToolsExecutableName), newTools);

        UpdateApplyPlan plan = new(target, payload, staging, "2.0.0", SHA256Hex(newGateway), SHA256Hex(newTools), 0, false, false);
        SecureTwoFileUpdateInstaller installer = new(allowed, minimumExecutableBytes: 1);

        using (UpdateTransaction transaction = installer.Apply(plan))
        {
            Assert.Equal(SHA256Hex(newGateway), SHA256Hex(File.ReadAllBytes(Path.Combine(target, SecureReleaseUpdateService.GatewayExecutableName))));
            transaction.Rollback();
        }
        Assert.Equal(SHA256Hex(oldGateway), SHA256Hex(File.ReadAllBytes(Path.Combine(target, SecureReleaseUpdateService.GatewayExecutableName))));

        using (UpdateTransaction transaction = installer.Apply(plan)) transaction.Commit();
        Assert.Equal(SHA256Hex(newTools), SHA256Hex(File.ReadAllBytes(Path.Combine(target, SecureReleaseUpdateService.ToolsExecutableName))));
        Assert.Equal(2, Directory.GetFiles(target).Length);
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Fact]
    public void InstallerRejectsPayloadOutsideProtectedUpdateRoot()
    {
        string allowed = Path.Combine(root, "allowed");
        string staging = Path.Combine(allowed, "stage");
        string outside = Path.Combine(root, "outside");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(target);
        UpdateApplyPlan plan = new(target, outside, staging, "2.0.0", new string('0', 64), new string('1', 64), 0, false, false);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new SecureTwoFileUpdateInstaller(allowed, 1).Apply(plan));

        Assert.Contains("vertrauenswürdig", error.Message);
    }

    private static SecureReleaseUpdateService CreateService(RouteHandler handler, string updateRoot) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            new GatewayOptions { UpdateRepository = "owner/repo" }, NullLogger<SecureReleaseUpdateService>.Instance,
            updateRoot, hardenStaging: false, minimumExecutableBytes: 1);

    private static RouteHandler CreateHandler(Version version, byte[] package, bool includeChecksumAsset = true,
        string? manifestOverride = null, string assetHost = "github.com", string assetRepository = "owner/repo",
        Version? assetVersion = null, string? finalManifestHost = null)
    {
        string normalized = Normalize(version);
        string normalizedAssetVersion = Normalize(assetVersion ?? version);
        string baseUrl = $"https://{assetHost}/{assetRepository}/releases/download/v{normalizedAssetVersion}";
        List<object> assets =
        [
            new { name = SecureReleaseUpdateService.PackageAssetName, browser_download_url = $"{baseUrl}/{SecureReleaseUpdateService.PackageAssetName}" }
        ];
        if (includeChecksumAsset)
            assets.Add(new { name = SecureReleaseUpdateService.ChecksumsAssetName, browser_download_url = $"{baseUrl}/{SecureReleaseUpdateService.ChecksumsAssetName}" });
        string release = JsonSerializer.Serialize(new { tag_name = $"v{normalized}", body = "Sicherheitsupdate", draft = false, prerelease = false, assets });
        string manifest = manifestOverride ?? SHA256Hex(package) + "  " + SecureReleaseUpdateService.PackageAssetName;

        return new RouteHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal)) return Response(request, Encoding.UTF8.GetBytes(release), "application/json");
            if (path.EndsWith('/' + SecureReleaseUpdateService.ChecksumsAssetName, StringComparison.Ordinal))
            {
                HttpResponseMessage response = Response(request, Encoding.UTF8.GetBytes(manifest), "text/plain");
                if (finalManifestHost is not null)
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        $"https://{finalManifestHost}/{SecureReleaseUpdateService.ChecksumsAssetName}");
                return response;
            }
            if (path.EndsWith('/' + SecureReleaseUpdateService.PackageAssetName, StringComparison.Ordinal)) return Response(request, package, "application/zip");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        });
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] content, string mediaType) =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(content) { Headers = { ContentType = new(mediaType) } } };

    private static byte[] CreatePackage(byte[] executable, bool extraEntry)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, SecureReleaseUpdateService.GatewayExecutableName, executable);
            WriteEntry(archive, SecureReleaseUpdateService.ToolsExecutableName, executable);
            if (extraEntry) WriteEntry(archive, "unexpected.txt", [0x01]);
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using Stream output = entry.Open();
        output.Write(content);
    }

    private static Version GetExecutableVersion()
    {
        string path = TestExecutablePath();
        string? value = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('-', '+', ' ')[0];
        if (!Version.TryParse(value, out Version? version)) throw new InvalidOperationException("Test-EXE besitzt keine lesbare Dateiversion.");
        return new(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private static string TestExecutablePath() => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string Normalize(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    private static string SHA256Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(route(request));
        }
    }
}

using System.Diagnostics;
using System.Security.Principal;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

internal static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("Dateipfad ohne Verzeichnis.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null, true); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken) =>
        WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);

    public static void HardenProgramDataAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.GetFullPath(Zvt2SumUp.Core.AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("ACL-Härtung außerhalb des ZVT2SumUp-Datenordners wurde abgelehnt.");

        string currentUser = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Benutzer-SID nicht verfügbar.");
        ProcessStartInfo start = new("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            path, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F",
            $"*{currentUser}:(OI)(CI)M", "/T", "/Q"
        }) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("ACL-Härtung konnte nicht gestartet werden.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw new TimeoutException("ACL-Härtung wurde nach zehn Sekunden abgebrochen.");
        }
        string diagnostics = SensitiveDataRedactor.Redact(output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult()).Trim();
        if (process.ExitCode != 0)
            throw new UnauthorizedAccessException("Der ZVT2SumUp-Datenordner konnte nicht sicher geschützt werden. " + diagnostics);
    }
}

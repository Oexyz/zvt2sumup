using System.Diagnostics;

namespace Zvt2SumUp.Infrastructure;

public static class UpdateDirectorySecurity
{
    public static void HardenAdminOnly(string directory, string allowedRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sichere Updates werden nur unter Windows unterstützt.");
        string root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update-Staging liegt außerhalb des erlaubten Datenordners.");
        EnsureNoReparsePoints(full);
        DirectoryInfo info = new(full);
        if (!info.Exists) throw new InvalidDataException("Unsicherer Update-Stagingordner.");

        ProcessStartInfo start = new("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            full, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"
        }) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("ACL-Härtung konnte nicht gestartet werden.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ACL-Härtung hat das Zeitlimit überschritten.");
        }
        if (process.ExitCode != 0)
            throw new UnauthorizedAccessException("Update-Staging konnte nicht sicher geschützt werden: " +
                Zvt2SumUp.Core.SensitiveDataRedactor.Redact(standardOutput + standardError));
    }

    public static void EnsureNoReparsePoints(string directory)
    {
        DirectoryInfo? current = new(Path.GetFullPath(directory));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Updatepfad enthält einen nicht erlaubten Umleitungspunkt.");
            current = current.Parent;
        }
    }
}

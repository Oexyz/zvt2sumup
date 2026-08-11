using System.Diagnostics;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Desktop;

internal static class ServiceActions
{
    public const string Name = "ZVT2SumUpGateway";
    public static async Task<string> StatusAsync()
    {
        ProcessResult result = await RunAsync("sc.exe", ["query", Name], false); if (result.ExitCode == 1060) return "Nicht installiert";
        if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "Läuft";
        if (result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "Gestoppt"; return result.ExitCode == 0 ? "Installiert" : "Nicht installiert";
    }
    public static Task<ProcessResult> InstallAsync() => RunToolAsync("install", Application.ExecutablePath);
    public static Task<ProcessResult> UninstallAsync() => RunToolAsync("uninstall");
    public static Task<ProcessResult> StartAsync() => RunToolAsync("start");
    public static Task<ProcessResult> StopAsync() => RunToolAsync("stop");
    public static Task<ProcessResult> RestartAsync() => RunToolAsync("restart");
    private static Task<ProcessResult> RunToolAsync(params string[] arguments)
    {
        string? tools = FindToolsExecutable();
        if (tools is null) throw new FileNotFoundException("ZVT2SumUp.Tools.exe wurde nicht gefunden.");
        return RunAsync(tools, arguments, true);
    }
    private static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, bool elevate)
    {
        ProcessStartInfo info = new(file) { UseShellExecute = elevate, Verb = elevate ? "runas" : string.Empty, CreateNoWindow = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (!elevate) { info.RedirectStandardOutput = true; info.RedirectStandardError = true; }
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
        string output = elevate ? string.Empty : await process.StandardOutput.ReadToEndAsync(); string error = elevate ? string.Empty : await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); return new(process.ExitCode, SensitiveDataRedactor.Redact(output + error));
    }
    private static string? FindToolsExecutable()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ZVT2SumUp.Tools.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Debug", "net10.0-windows", "win-x64", "ZVT2SumUp.Tools.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Release", "net10.0-windows", "win-x64", "ZVT2SumUp.Tools.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Debug", "net10.0-windows", "ZVT2SumUp.Tools.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Release", "net10.0-windows", "ZVT2SumUp.Tools.exe"))
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
internal sealed record ProcessResult(int ExitCode, string Output);

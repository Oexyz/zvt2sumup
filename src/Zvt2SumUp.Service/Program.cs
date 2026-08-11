using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;

namespace Zvt2SumUp.Service;

public static class GatewayServiceHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Contains("--service-smoke-test", StringComparer.OrdinalIgnoreCase)) return await SmokeTestAsync().ConfigureAwait(false);
            Environment.ExitCode = 0;
            Directory.CreateDirectory(AppPaths.Root); AtomicFileAccess.HardenRoot();
            using IniOptionsStore optionsStore = new(AppPaths.Configuration);
            using DpapiSecretStore secretStore = new(AppPaths.Secrets);
            GatewayOptions options = await optionsStore.LoadAsync().ConfigureAwait(false); GatewaySecrets secrets = await secretStore.LoadAsync().ConfigureAwait(false);
            IReadOnlyList<string> errors = options.Validate(requireSecrets: true, secrets.HasApiKey);
            if (errors.Count > 0) { foreach (string error in errors) Console.Error.WriteLine(error); return 2; }

            string[] hostArgs = args.Where(argument => !argument.Equals("--service", StringComparison.OrdinalIgnoreCase)).ToArray();
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(hostArgs);
            builder.Services.AddWindowsService(service => service.ServiceName = "ZVT2SumUpGateway");
            builder.Logging.ClearProviders(); builder.Logging.SetMinimumLevel(RuntimeLogging.ParseLevel(options.LogLevel)); builder.Logging.AddConsole();
            builder.Logging.AddEventLog(settings => settings.SourceName = "ZVT2SumUpGateway");
            using RollingFileLoggerProvider rollingFileLogger = new(AppPaths.ResolveLogFile(options.LogFile));
            builder.Logging.AddProvider(rollingFileLogger);
            builder.Services.AddZvt2SumUpRuntime(options, secrets); builder.Services.AddHostedService<GatewayWorker>();
            using IHost host = builder.Build(); await host.RunAsync().ConfigureAwait(false); return Environment.ExitCode;
        }
        catch (Exception exception) { Console.Error.WriteLine(SensitiveDataRedactor.Redact(exception.ToString())); return 1; }
    }

    private static async Task<int> SmokeTestAsync()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddZvt2SumUpRuntime(new GatewayOptions(), new GatewaySecrets("smoke-test-not-sent"));
        using IHost host = builder.Build(); await host.StartAsync().ConfigureAwait(false);
        _ = host.Services.GetRequiredService<IGatewayRuntime>(); await host.StopAsync().ConfigureAwait(false); return 0;
    }
}

internal static class AtomicFileAccess
{
    public static void HardenRoot()
    {
        // DpapiSecretStore hardens this directory again after every secret write.
        if (!Directory.Exists(AppPaths.Root)) Directory.CreateDirectory(AppPaths.Root);
    }
}

internal sealed partial class GatewayWorker(IGatewayRuntime runtime, IHostApplicationLifetime lifetime, ILogger<GatewayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await runtime.StartAsync(stoppingToken).ConfigureAwait(false); await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { LogGatewayStartFailure(logger, exception); Environment.ExitCode = 1; lifetime.StopApplication(); }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    { await runtime.StopAsync(cancellationToken).ConfigureAwait(false); await base.StopAsync(cancellationToken).ConfigureAwait(false); }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Critical, Message = "Gateway konnte nicht gestartet werden")]
    private static partial void LogGatewayStartFailure(ILogger logger, Exception exception);
}

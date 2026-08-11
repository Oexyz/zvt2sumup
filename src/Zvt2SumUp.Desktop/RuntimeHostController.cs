using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;

namespace Zvt2SumUp.Desktop;

internal sealed class RuntimeHostController : IAsyncDisposable
{
    private IHost? host; private IGatewayRuntime? runtime;
    public bool IsRunning => runtime?.IsRunning == true;

    public async Task StartAsync()
    {
        if (IsRunning) return;
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions options = await optionsStore.LoadAsync(); GatewaySecrets secrets = await secretStore.LoadAsync();
        IReadOnlyList<string> errors = options.Validate(true, secrets.HasApiKey); if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); builder.Logging.SetMinimumLevel(RuntimeLogging.ParseLevel(options.LogLevel));
        builder.Logging.AddProvider(new RollingFileLoggerProvider(AppPaths.ResolveLogFile(options.LogFile)));
        builder.Services.AddZvt2SumUpRuntime(options, secrets); host = builder.Build(); await host.StartAsync(); runtime = host.Services.GetRequiredService<IGatewayRuntime>(); await runtime.StartAsync();
    }
    public async Task StopAsync()
    {
        if (runtime is not null) await runtime.StopAsync(); if (host is not null) { await host.StopAsync(); host.Dispose(); }
        runtime = null; host = null;
    }
    public static async Task<(IHost Host, ISumUpClient Client)> CreateApiSessionAsync()
    {
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions options = await optionsStore.LoadAsync(); GatewaySecrets secrets = await secretStore.LoadAsync();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); builder.Services.AddZvt2SumUpRuntime(options, secrets);
        IHost result = builder.Build(); await result.StartAsync(); return (result, result.Services.GetRequiredService<ISumUpClient>());
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}

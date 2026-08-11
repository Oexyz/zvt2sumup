using Microsoft.Extensions.DependencyInjection;
using Zvt2SumUp.Core;
using Zvt2SumUp.SumUp;

namespace Zvt2SumUp.Infrastructure;

public static class RuntimeRegistration
{
    public static IServiceCollection AddZvt2SumUpRuntime(this IServiceCollection services, GatewayOptions options, GatewaySecrets secrets)
    {
        services.AddSingleton(options); services.AddSingleton(secrets);
        services.AddHttpClient<ISumUpClient, SumUpApiClient>(client =>
        {
            client.BaseAddress = SumUpApiClient.DefaultBaseAddress;
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient<IUpdateService, SecureReleaseUpdateService>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/"); client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZVT2SumUp/1.0"); client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });
        services.AddSingleton<ITransactionJournal>(_ => new JsonTransactionJournal(AppPaths.Journal));
        services.AddSingleton<IReceiptRenderer>(_ => new ReceiptTemplateRenderer(AppPaths.ReceiptTemplates, AppPaths.ReceiptCounter));
        services.AddSingleton<ZvtGatewayHandler>();
        services.AddSingleton<IGatewayTransport>(provider => options.Transport == GatewayTransport.Tcp
            ? ActivatorUtilities.CreateInstance<TcpZvtServer>(provider)
            : ActivatorUtilities.CreateInstance<SerialZvtServer>(provider));
        services.AddSingleton<IGatewayRuntime, GatewayRuntime>();
        return services;
    }
}

public sealed class GatewayRuntime(IGatewayTransport transport) : IGatewayRuntime
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    public bool IsRunning => transport.IsRunning;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    { await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false); try { if (!transport.IsRunning) await transport.StartAsync(cancellationToken).ConfigureAwait(false); } finally { lifecycle.Release(); } }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    { await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false); try { if (transport.IsRunning) await transport.StopAsync(cancellationToken).ConfigureAwait(false); } finally { lifecycle.Release(); } }
    public async ValueTask DisposeAsync() { await transport.DisposeAsync().ConfigureAwait(false); lifecycle.Dispose(); }
}

namespace Zvt2SumUp.Core;

public interface IOptionsStore
{
    Task<GatewayOptions> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GatewayOptions options, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task<GatewaySecrets> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GatewaySecrets secrets, CancellationToken cancellationToken = default);
}

public interface ISumUpClient
{
    string MerchantCode { get; }
    string TerminalId { get; set; }
    Task<ConnectionResult> TestConnectionAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TerminalDescriptor>> GetTerminalsAsync(CancellationToken cancellationToken);
    Task<TerminalDescriptor> PairReaderAsync(string pairingCode, string name, CancellationToken cancellationToken);
    Task<CheckoutResult> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);
    Task<CheckoutResult> WaitForPaymentAsync(string checkoutId, TimeSpan timeout, CancellationToken cancellationToken);
    Task TerminateCheckoutAsync(CancellationToken cancellationToken);
    Task<CheckoutResult> RefundAsync(string transactionId, long? amountCents, CancellationToken cancellationToken);
    Task<IReadOnlyList<CheckoutResult>> GetTransactionsAsync(int limit, CancellationToken cancellationToken);
}

public interface ITransactionJournal
{
    Task AddPaymentAsync(TransactionRecord record, CancellationToken cancellationToken = default);
    Task AddRefundAsync(TransactionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransactionRecord>> GetOpenAsync(string? terminalId = null, CancellationToken cancellationToken = default);
    Task<int> CloseOpenAsync(string? terminalId = null, CancellationToken cancellationToken = default);
}

public interface IReceiptRenderer
{
    Task<string> NextReceiptNumberAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<string> Render(string section, IReadOnlyDictionary<string, object?> context);
    string RenderValue(string section, string optionName, IReadOnlyDictionary<string, object?> context, string fallback, int maximumLength);
}

public interface IGatewayTransport : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IGatewayRuntime : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IUpdateService
{
    Task<UpdateInformation> CheckAsync(CancellationToken cancellationToken = default);
    Task<PreparedUpdate> PrepareAsync(UpdateInformation information, CancellationToken cancellationToken = default);
}

public sealed record UpdateInformation(bool Available, Version LocalVersion, Version? RemoteVersion, string Notes,
    Uri? PackageUrl, Uri? ChecksumsUrl, string Error = "");

public sealed record PreparedUpdate(Version Version, string StagingDirectory, string PayloadDirectory,
    string PackageSha256, string GatewaySha256, string ToolsSha256);

public sealed record UpdateApplyPlan(string TargetDirectory, string PayloadDirectory, string StagingDirectory,
    string Version, string GatewaySha256, string ToolsSha256, int WaitProcessId, bool RestartService, bool RestartGui);

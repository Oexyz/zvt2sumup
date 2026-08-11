using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed class IniOptionsStore(string path) : IOptionsStore, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Path { get; } = path;

    public async Task<GatewayOptions> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path)) { GatewayOptions defaults = new(); await SaveCoreAsync(defaults, cancellationToken).ConfigureAwait(false); return defaults; }
            string text = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            IniDocument ini = IniDocument.Parse(text);
            return new GatewayOptions
            {
                Transport = ini.Get("gateway", "modus", "tcp").Equals("com", StringComparison.OrdinalIgnoreCase) ? GatewayTransport.Com : GatewayTransport.Tcp,
                TcpHost = ini.Get("gateway", "tcp_host", "127.0.0.1"),
                TcpPort = ini.GetInt("gateway", "tcp_port", 20007),
                TcpIdleTimeoutSeconds = ini.GetInt("gateway", "tcp_idle_timeout", 0),
                ComPort = ini.Get("gateway", "com_port", "COM3"),
                ComBaudRate = ini.GetInt("gateway", "com_baudrate", 9600),
                Currency = ini.Get("gateway", "waehrung", "EUR").ToUpperInvariant(),
                LogLevel = ini.Get("gateway", "log_level", "Information"),
                LogFile = ini.Get("gateway", "log_datei", "logs/zvt2sumup.log"),
                PaymentTimeoutSeconds = ini.GetInt("sumup", "zahlung_timeout", 120),
                MerchantCode = ini.Get("sumup", "merchant_code"),
                TerminalId = ini.Get("sumup", "terminal_id"),
                EndOfDaySource = ini.Get("end_of_day", "source", "local_journal"),
                ResetAfterReconciliation = ini.GetBool("end_of_day", "reset_after_print", true),
                UpdateRepository = ini.Get("updates", "github_repository", "Oexyz/zvt2sumup")
            };
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(GatewayOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = options.Validate(requireSecrets: false); if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(options, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task SaveCoreAsync(GatewayOptions o, CancellationToken cancellationToken)
    {
        IniDocument ini = new();
        ini.Set("gateway", "modus", o.Transport == GatewayTransport.Com ? "com" : "tcp"); ini.Set("gateway", "tcp_host", o.TcpHost);
        ini.Set("gateway", "tcp_port", o.TcpPort); ini.Set("gateway", "tcp_idle_timeout", o.TcpIdleTimeoutSeconds);
        ini.Set("gateway", "com_port", o.ComPort); ini.Set("gateway", "com_baudrate", o.ComBaudRate);
        ini.Set("gateway", "waehrung", o.Currency); ini.Set("gateway", "log_level", o.LogLevel); ini.Set("gateway", "log_datei", o.LogFile);
        ini.Set("sumup", "merchant_code", o.MerchantCode); ini.Set("sumup", "terminal_id", o.TerminalId); ini.Set("sumup", "zahlung_timeout", o.PaymentTimeoutSeconds);
        ini.Set("sumup", "api_key", "{{encrypted:secrets.dat:api_key}}"); ini.Set("sumup", "affiliate_key", "{{encrypted:secrets.dat:affiliate_key}}");
        ini.Set("sumup", "affiliate_app_id", "{{encrypted:secrets.dat:affiliate_app_id}}");
        ini.Set("end_of_day", "source", o.EndOfDaySource); ini.Set("end_of_day", "reset_after_print", o.ResetAfterReconciliation ? "true" : "false");
        ini.Set("updates", "github_repository", o.UpdateRepository);
        string header = "; ZVT2SumUp - keine Secrets in dieser Datei speichern.\n; Geheimnisse liegen DPAPI-verschlüsselt in secrets.dat.\n\n";
        await AtomicFile.WriteAllTextAsync(Path, header + ini, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => gate.Dispose();
}

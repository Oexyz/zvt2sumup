using System.Globalization;

namespace Zvt2SumUp.Core;

public enum GatewayTransport { Tcp, Com }

public sealed record GatewayOptions
{
    public GatewayTransport Transport { get; init; } = GatewayTransport.Tcp;
    public string TcpHost { get; init; } = "127.0.0.1";
    public int TcpPort { get; init; } = 20007;
    public int TcpIdleTimeoutSeconds { get; init; }
    public string ComPort { get; init; } = "COM3";
    public int ComBaudRate { get; init; } = 9600;
    public string Currency { get; init; } = "EUR";
    public string LogLevel { get; init; } = "Information";
    public string LogFile { get; init; } = "logs/zvt2sumup.log";
    public int PaymentTimeoutSeconds { get; init; } = 120;
    public string MerchantCode { get; init; } = string.Empty;
    public string TerminalId { get; init; } = string.Empty;
    public string EndOfDaySource { get; init; } = "local_journal";
    public bool ResetAfterReconciliation { get; init; } = true;
    public string UpdateRepository { get; init; } = "Oexyz/zvt2sumup";

    public IReadOnlyList<string> Validate(bool requireSecrets = true, bool hasApiKey = false)
    {
        List<string> errors = [];
        if (requireSecrets && !hasApiKey) errors.Add("Der SumUp-API-Schlüssel fehlt.");
        if (Transport == GatewayTransport.Tcp)
        {
            if (TcpPort is < 1 or > 65535) errors.Add("Der TCP-Port muss zwischen 1 und 65535 liegen.");
            if (!System.Net.IPAddress.TryParse(TcpHost, out _)) errors.Add("Die TCP-Bind-Adresse ist ungültig.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(ComPort)) errors.Add("Der COM-Port fehlt.");
            if (ComBaudRate is < 300 or > 4_000_000) errors.Add("Die COM-Baudrate ist ungültig.");
        }
        if (TcpIdleTimeoutSeconds < 0) errors.Add("Der Idle-Timeout darf nicht negativ sein.");
        if (PaymentTimeoutSeconds is < 10 or > 3600) errors.Add("Der Zahlungstimeout muss zwischen 10 und 3600 Sekunden liegen.");
        if (Currency.Length != 3 || Currency.Any(c => !char.IsAsciiLetterUpper(c))) errors.Add("Die Währung muss ein dreistelliger ISO-4217-Code sein.");
        string normalizedLogLevel = LogLevel.ToUpperInvariant();
        if (normalizedLogLevel is not ("TRACE" or "DEBUG" or "INFORMATION" or "INFO" or "WARNING" or "WARN" or "ERROR" or "CRITICAL" or "FATAL"))
            errors.Add("Das Log-Level ist ungültig.");
        try { _ = AppPaths.ResolveLogFile(LogFile); } catch (InvalidDataException exception) { errors.Add(exception.Message); }
        if (!EndOfDaySource.Equals("local_journal", StringComparison.OrdinalIgnoreCase)) errors.Add("Als Kassenschnittquelle wird derzeit nur local_journal unterstützt.");
        string[] repositoryParts = UpdateRepository.Split('/');
        if (repositoryParts.Length != 2 || repositoryParts.Any(part => part.Length == 0 || part.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            errors.Add("Das Update-Repository muss im Format Eigentümer/Repository angegeben werden.");
        return errors;
    }

    public bool IsExternallyBound => Transport == GatewayTransport.Tcp && TcpHost is not "127.0.0.1" and not "::1" and not "localhost";
}

public sealed record GatewaySecrets(string ApiKey = "", string AffiliateKey = "", string AffiliateAppId = "")
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record TerminalDescriptor(string Id, string Name, string Status, string SerialNumber = "", bool IsReader = false);

public sealed record ConnectionResult(bool Success, string MerchantCode = "", string BusinessName = "", string Error = "");

public sealed record CheckoutRequest(long AmountCents, string Currency, string Description, string Reference);
public sealed record CheckoutResult(string Id, string Status, string TransactionId = "", string TransactionCode = "",
    string Currency = "", string CardType = "", string AuthorizationCode = "", string Error = "", long AmountCents = 0);

public sealed record TransactionRecord
{
    public string Type { get; init; } = "PAYMENT";
    public string TerminalId { get; init; } = string.Empty;
    public long AmountCents { get; init; }
    public string Currency { get; init; } = "EUR";
    public string TransactionId { get; init; } = string.Empty;
    public string CheckoutId { get; init; } = string.Empty;
    public string AuthorizationCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool Closed { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

public sealed record JournalSummary(int PaymentCount, int RefundCount, long PaymentTotalCents, long RefundTotalCents)
{
    public int TransactionCount => PaymentCount + RefundCount;
    public long NetTotalCents => PaymentTotalCents - RefundTotalCents;
    public static JournalSummary From(IEnumerable<TransactionRecord> records)
    {
        TransactionRecord[] items = records.ToArray();
        TransactionRecord[] refunds = items.Where(x => x.Type.Equals("REFUND", StringComparison.OrdinalIgnoreCase) || x.AmountCents < 0).ToArray();
        TransactionRecord[] payments = items.Except(refunds).ToArray();
        return new(payments.Length, refunds.Length, payments.Sum(x => x.AmountCents), refunds.Sum(x => Math.Abs(x.AmountCents)));
    }
}

public static class Money
{
    public static decimal ToMajor(long cents) => cents / 100m;
    public static long ToMinor(decimal amount) => checked((long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    public static string Format(long cents) => ToMajor(cents).ToString("0.00", CultureInfo.GetCultureInfo("de-DE"));
}

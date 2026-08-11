using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.SumUp;

public sealed class SumUpApiException : Exception
{
    public SumUpApiException()
    {
    }

    public SumUpApiException(string message)
        : base(message)
    {
    }

    public SumUpApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SumUpApiException(string message, string code, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; } = string.Empty;
    public HttpStatusCode? StatusCode { get; }
}

public sealed partial class SumUpApiClient : ISumUpClient
{
    public static readonly Uri DefaultBaseAddress = new("https://api.sumup.com/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly ILogger<SumUpApiClient> logger;
    private readonly string affiliateKey;
    private readonly string affiliateAppId;

    public SumUpApiClient(HttpClient httpClient, GatewaySecrets secrets, GatewayOptions options, ILogger<SumUpApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        http = httpClient;
        this.logger = logger;
        MerchantCode = options.MerchantCode;
        TerminalId = options.TerminalId;
        affiliateKey = secrets.AffiliateKey;
        affiliateAppId = secrets.AffiliateAppId;
        http.BaseAddress ??= DefaultBaseAddress;
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secrets.ApiKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ZVT2SumUp/1.0");
    }

    public string MerchantCode { get; private set; }
    public string TerminalId { get; set; }
    private bool IsReader => TerminalId.StartsWith("rdr_", StringComparison.OrdinalIgnoreCase);

    public async Task<ConnectionResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri("v0.1/me", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, Error: "API-Schlüssel ungültig oder abgelaufen.");
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(false, Error: "API-Schlüssel besitzt nicht die erforderlichen Berechtigungen.");
            await EnsureSuccessAsync(response, "PROFILE_FAILED", cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            JsonElement profile = root.TryGetProperty("merchant_profile", out JsonElement merchantProfile) ? merchantProfile : root;
            string code = GetString(profile, "merchant_code") ?? GetString(root, "merchant_code") ?? string.Empty;
            string name = GetString(profile, "business_name") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(code)) MerchantCode = code;
            return new(true, code, name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, Error: "Zeitüberschreitung bei der SumUp-API."); }
        catch (HttpRequestException exception)
        { return new(false, Error: "SumUp ist nicht erreichbar: " + SensitiveDataRedactor.Redact(exception.Message)); }
        catch (SumUpApiException exception)
        { return new(false, Error: SensitiveDataRedactor.Redact(exception.Message)); }
    }

    public async Task<IReadOnlyList<TerminalDescriptor>> GetTerminalsAsync(CancellationToken cancellationToken)
    {
        EnsureMerchantCode(); List<TerminalDescriptor> result = [];
        await AddItemsAsync($"v0.1/merchants/{Uri.EscapeDataString(MerchantCode)}/terminals", false, result, cancellationToken).ConfigureAwait(false);
        await AddItemsAsync($"v0.1/merchants/{Uri.EscapeDataString(MerchantCode)}/readers", true, result, cancellationToken).ConfigureAwait(false);
        return result.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
    }

    public async Task<TerminalDescriptor> PairReaderAsync(string pairingCode, string name, CancellationToken cancellationToken)
    {
        EnsureMerchantCode();
        string code = new(pairingCode.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (code.Length is < 8 or > 9) throw new ArgumentException("Der Pairing-Code muss 8 oder 9 alphanumerische Zeichen enthalten.", nameof(pairingCode));
        using HttpRequestMessage request = JsonRequest(HttpMethod.Post,
            $"v0.1/merchants/{Uri.EscapeDataString(MerchantCode)}/readers", new { pairing_code = code, name });
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PAIRING_FAILED", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        TerminalDescriptor terminal = ParseTerminal(document.RootElement, true);
        LogReaderPaired(logger, terminal.Id, terminal.Status);
        return terminal;
    }

    public async Task<CheckoutResult> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AmountCents <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Der Betrag muss positiv sein.");
        EnsureMerchantCode();
        if (IsReader) return await CreateReaderCheckoutAsync(request, cancellationToken).ConfigureAwait(false);
        return await CreateClassicCheckoutAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CheckoutResult> WaitForPaymentAsync(string checkoutId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (true)
            {
                CheckoutResult status = IsReader
                    ? await GetReaderTransactionAsync(checkoutId, timeoutSource.Token).ConfigureAwait(false)
                    : await GetClassicCheckoutAsync(checkoutId, timeoutSource.Token).ConfigureAwait(false);
                if (status.Status is "PAID" or "FAILED" or "EXPIRED" or "CANCELLED") return status;
                await Task.Delay(TimeSpan.FromSeconds(2), timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(checkoutId, "TIMEOUT", Error: "Zahlungstimeout"); }
    }

    public async Task TerminateCheckoutAsync(CancellationToken cancellationToken)
    {
        if (!IsReader || string.IsNullOrWhiteSpace(TerminalId)) return;
        using HttpRequestMessage request = new(HttpMethod.Post,
            $"v0.1/merchants/{Uri.EscapeDataString(MerchantCode)}/readers/{Uri.EscapeDataString(TerminalId)}/terminate")
        { Content = JsonContent.Create(new { }) };
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "TERMINATE_FAILED", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CheckoutResult> RefundAsync(string transactionId, long? amountCents, CancellationToken cancellationToken)
    {
        EnsureMerchantCode();
        object body = amountCents.HasValue ? new { amount = Money.ToMajor(amountCents.Value) } : new { };
        string current = $"v1.0/merchants/{Uri.EscapeDataString(MerchantCode)}/payments/{Uri.EscapeDataString(transactionId)}/refunds";
        using HttpRequestMessage request = JsonRequest(HttpMethod.Post, current, body);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            LogRefundFallback(logger);
            using HttpRequestMessage compatibilityRequest = JsonRequest(HttpMethod.Post, $"v0.1/me/refund/{Uri.EscapeDataString(transactionId)}", body);
            using HttpResponseMessage compatibilityResponse = await http.SendAsync(compatibilityRequest, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(compatibilityResponse, "REFUND_FAILED", cancellationToken).ConfigureAwait(false);
            return new(transactionId, "REFUNDED", transactionId);
        }
        await EnsureSuccessAsync(response, "REFUND_FAILED", cancellationToken).ConfigureAwait(false);
        return new(transactionId, "REFUNDED", transactionId);
    }

    public async Task<IReadOnlyList<CheckoutResult>> GetTransactionsAsync(int limit, CancellationToken cancellationToken)
    {
        EnsureMerchantCode(); limit = Math.Clamp(limit, 1, 100);
        string uri = $"v2.1/merchants/{Uri.EscapeDataString(MerchantCode)}/transactions/history?limit={limit}&order=descending";
        using HttpResponseMessage response = await http.GetAsync(new Uri(uri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "TRANSACTIONS_FAILED", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return EnumerateItems(document.RootElement).Select(ParseTransaction).ToArray();
    }

    private async Task<CheckoutResult> CreateReaderCheckoutAsync(CheckoutRequest checkout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TerminalId)) throw new SumUpApiException("Keine Reader-ID konfiguriert.", "NO_TERMINAL");
        Dictionary<string, object> payload = new()
        {
            ["total_amount"] = new { currency = checkout.Currency, minor_unit = 2, value = checkout.AmountCents },
            ["description"] = checkout.Description
        };
        AddAffiliate(payload, checkout.Reference);
        string uri = $"v0.1/merchants/{Uri.EscapeDataString(MerchantCode)}/readers/{Uri.EscapeDataString(TerminalId)}/checkout";
        using HttpRequestMessage request = JsonRequest(HttpMethod.Post, uri, payload);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "CHECKOUT_FAILED", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement nested) ? nested : document.RootElement;
        string id = GetString(data, "client_transaction_id") ?? throw new SumUpApiException("SumUp lieferte keine Client-Transaktions-ID.", "NO_ID");
        return new(id, "PENDING");
    }

    private async Task<CheckoutResult> CreateClassicCheckoutAsync(CheckoutRequest checkout, CancellationToken cancellationToken)
    {
        Dictionary<string, object> payload = new()
        {
            ["checkout_reference"] = checkout.Reference,
            ["amount"] = Money.ToMajor(checkout.AmountCents),
            ["currency"] = checkout.Currency,
            ["merchant_code"] = MerchantCode,
            ["description"] = checkout.Description
        };
        AddAffiliate(payload, null);
        using HttpRequestMessage request = JsonRequest(HttpMethod.Post, "v0.1/checkouts", payload);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "CHECKOUT_FAILED", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        string id = GetString(document.RootElement, "id") ?? throw new SumUpApiException("SumUp lieferte keine Checkout-ID.", "NO_ID");
        if (!string.IsNullOrWhiteSpace(TerminalId))
        {
            using HttpRequestMessage process = JsonRequest(HttpMethod.Put, $"v0.1/checkouts/{Uri.EscapeDataString(id)}", new { terminal_id = TerminalId });
            using HttpResponseMessage processResponse = await http.SendAsync(process, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(processResponse, "TERMINAL_FAILED", cancellationToken).ConfigureAwait(false);
        }
        return new(id, "PENDING");
    }

    private async Task<CheckoutResult> GetClassicCheckoutAsync(string id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(
            new Uri($"v0.1/checkouts/{Uri.EscapeDataString(id)}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "CHECKOUT_STATUS_FAILED", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        return new(id, NormalizeStatus(GetString(root, "status")), GetString(root, "transaction_id") ?? string.Empty,
            GetString(root, "transaction_code") ?? string.Empty, GetString(root, "currency") ?? string.Empty,
            GetString(root, "card_type") ?? string.Empty, GetString(root, "auth_code") ?? string.Empty);
    }

    private async Task<CheckoutResult> GetReaderTransactionAsync(string clientTransactionId, CancellationToken cancellationToken)
    {
        string uri = $"v2.1/merchants/{Uri.EscapeDataString(MerchantCode)}/transactions?client_transaction_id={Uri.EscapeDataString(clientTransactionId)}";
        using HttpResponseMessage response = await http.GetAsync(new Uri(uri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(clientTransactionId, "PENDING");
        await EnsureSuccessAsync(response, "TRANSACTION_STATUS_FAILED", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        JsonElement[] candidates = EnumerateItems(root);
        if (candidates.Length == 0 && root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out JsonElement data))
        {
            candidates = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [data];
        }
        if (candidates.Length == 0 && root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("id", out _) || root.TryGetProperty("transaction_id", out _)))
        {
            candidates = [root];
        }
        if (candidates.Length == 0) return new(clientTransactionId, "PENDING");

        JsonElement transaction = candidates.FirstOrDefault(candidate =>
            string.Equals(GetString(candidate, "client_transaction_id"), clientTransactionId, StringComparison.OrdinalIgnoreCase));
        if (transaction.ValueKind == JsonValueKind.Undefined) transaction = candidates[0];
        return ParseTransaction(transaction) with { Id = clientTransactionId };
    }

    private async Task AddItemsAsync(string uri, bool reader, List<TerminalDescriptor> destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(new Uri(uri, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { LogTerminalEndpointFailure(logger, uri, response.StatusCode); return; }
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        destination.AddRange(EnumerateItems(document.RootElement).Select(x => ParseTerminal(x, reader)));
    }

    private void AddAffiliate(Dictionary<string, object> payload, string? foreignTransactionId)
    {
        if (!string.IsNullOrWhiteSpace(affiliateKey) && !string.IsNullOrWhiteSpace(affiliateAppId))
        {
            Dictionary<string, string> affiliate = new()
            {
                ["key"] = affiliateKey,
                ["app_id"] = affiliateAppId
            };
            if (!string.IsNullOrWhiteSpace(foreignTransactionId))
                affiliate["foreign_transaction_id"] = foreignTransactionId;
            payload["affiliate"] = affiliate;
        }
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string uri, object payload) =>
        new(method, uri) { Content = JsonContent.Create(payload, options: JsonOptions) };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string code, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body = SensitiveDataRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (body.Length > 500) body = body[..500];
        throw new SumUpApiException($"SumUp-API-Fehler {(int)response.StatusCode}: {body}", code, response.StatusCode);
    }

    private static JsonElement[] EnumerateItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array) return items.EnumerateArray().ToArray();
        return [];
    }

    private static TerminalDescriptor ParseTerminal(JsonElement element, bool reader)
    {
        string id = GetString(element, "id") ?? GetString(element, "terminal_id") ?? GetString(element, "identifier") ?? string.Empty;
        string serial = GetString(element, "serial_number") ?? string.Empty;
        if (element.TryGetProperty("device", out JsonElement device)) serial = GetString(device, "identifier") ?? serial;
        return new(id, GetString(element, "name") ?? GetString(element, "model") ?? "Terminal",
            GetString(element, "status") ?? "unknown", serial, reader || id.StartsWith("rdr_", StringComparison.OrdinalIgnoreCase));
    }

    private static CheckoutResult ParseTransaction(JsonElement tx)
    {
        string id = GetString(tx, "id") ?? GetString(tx, "transaction_id") ?? string.Empty;
        long amountCents = ParseAmountCents(tx);
        return new(id, NormalizeStatus(GetString(tx, "status") ?? GetString(tx, "simple_status")), id,
            GetString(tx, "transaction_code") ?? string.Empty, GetString(tx, "currency") ?? string.Empty,
            GetString(tx, "card_type") ?? string.Empty, GetString(tx, "auth_code") ?? string.Empty, AmountCents: amountCents);
    }

    private static long ParseAmountCents(JsonElement tx)
    {
        if (!tx.TryGetProperty("amount", out JsonElement amount)) return 0;
        if (!decimal.TryParse(amount.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal major)) return 0;
        try { return Money.ToMinor(major); }
        catch (OverflowException) { return 0; }
    }

    private static string NormalizeStatus(string? status) => (status ?? "UNKNOWN").ToUpperInvariant() switch
    { "SUCCESSFUL" or "PAID" or "PAID_OUT" => "PAID", "PENDING" => "PENDING", "EXPIRED" => "EXPIRED", "REFUNDED" => "REFUNDED", "CANCELLED" => "CANCELLED", _ => "FAILED" };
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement property) ? property.ToString() : null;
    private void EnsureMerchantCode() { if (string.IsNullOrWhiteSpace(MerchantCode)) throw new SumUpApiException("Merchant Code fehlt.", "NO_MERCHANT"); }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Reader gekoppelt: {ReaderId}, Status {Status}")]
    private static partial void LogReaderPaired(ILogger logger, string readerId, string status);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Aktueller Refund-Endpunkt nicht verfügbar; verwende den dokumentierten kompatiblen Ersatzendpunkt.")]
    private static partial void LogRefundFallback(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Terminal-Endpunkt {Uri} antwortete mit {Status}")]
    private static partial void LogTerminalEndpointFailure(ILogger logger, string uri, HttpStatusCode status);
}

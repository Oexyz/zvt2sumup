using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;
using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Infrastructure;

public sealed partial class ZvtGatewayHandler
{
    private static readonly HashSet<ushort> Supported =
    [
        ZvtCommandIds.StatusEnquiry, ZvtCommandIds.Registration, ZvtCommandIds.Authorization, ZvtCommandIds.LogOff,
        ZvtCommandIds.TurnoverTotals, ZvtCommandIds.PrintTurnoverReceipts, ZvtCommandIds.Reset, ZvtCommandIds.RepeatReceipt,
        ZvtCommandIds.Reversal, ZvtCommandIds.Refund, ZvtCommandIds.Reconciliation, ZvtCommandIds.PartialReconciliation,
        ZvtCommandIds.Diagnosis, ZvtCommandIds.SelfTest, ZvtCommandIds.SetDateTime, ZvtCommandIds.Initialization, ZvtCommandIds.Abort
    ];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TerminalLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISumUpClient sumUp;
    private readonly ITransactionJournal journal;
    private readonly IReceiptRenderer receipts;
    private readonly GatewayOptions options;
    private readonly ILogger<ZvtGatewayHandler> logger;
    private readonly object stateLock = new();
    private string lastTransactionId = string.Empty;
    private long lastAmountCents;
    private IReadOnlyList<string> lastReceipt = [];
    private byte[] currencyCode = [0x09, 0x78];
    private CancellationTokenSource? activePayment;

    public ZvtGatewayHandler(ISumUpClient sumUp, ITransactionJournal journal, IReceiptRenderer receipts,
        GatewayOptions options, ILogger<ZvtGatewayHandler> logger)
    { this.sumUp = sumUp; this.journal = journal; this.receipts = receipts; this.options = options; this.logger = logger; }

    public static bool Supports(ushort commandId) => Supported.Contains(commandId);

    public async Task<IReadOnlyList<byte[]>> HandleAsync(ZvtCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.IsAcknowledgement) return [];
        try
        {
            return command.Id switch
            {
                ZvtCommandIds.Registration => await RegisterAsync(command, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.Authorization => await PayAsync(command, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.Reversal => await RefundAsync(command, false, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.Refund => await RefundAsync(command, true, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.Reconciliation => await ReconcileAsync(command, true, true, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.PartialReconciliation => await ReconcileAsync(command, false, false, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.TurnoverTotals => await ReconcileAsync(command, false, false, cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.StatusEnquiry => await StatusAsync(cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.Abort => await AbortAsync(cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.LogOff => [ZvtResponses.Completion()],
                ZvtCommandIds.Diagnosis => await DiagnosisAsync(cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.Initialization => [ZvtResponses.Completion()],
                ZvtCommandIds.SetDateTime => [ZvtResponses.CompletionResult(ZvtResultCode.Ok)],
                ZvtCommandIds.Reset => Reset(),
                ZvtCommandIds.SelfTest => await SelfTestAsync(cancellationToken).ConfigureAwait(false),
                ZvtCommandIds.PrintTurnoverReceipts or ZvtCommandIds.RepeatReceipt => RepeatReceipt(),
                _ => [ZvtResponses.NegativeAck()]
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return [ZvtResponses.Abort(ZvtResultCode.AbortTimeoutOrKey)]; }
#pragma warning disable CA1031 // Protokollgrenze: Jeder unerwartete Handlerfehler muss als ZVT-Systemfehler beantwortet werden.
        catch (Exception exception)
        {
            LogCommandFailure(logger, exception, command.Name);
            return [ZvtResponses.Abort(ZvtResultCode.SystemError)];
        }
#pragma warning restore CA1031
    }

    private async Task<IReadOnlyList<byte[]>> RegisterAsync(ZvtCommand command, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> data = command.Data.Span;
        if (data.Length >= 6) currencyCode = data[4..6].ToArray();
        ConnectionResult connection = await sumUp.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connection.Success) return [ZvtResponses.IntermediateStatus("SumUp nicht erreichbar"), ZvtResponses.Abort(ZvtResultCode.NoConnection)];
        string digits = new((sumUp.TerminalId.Length == 0 ? "00000001" : sumUp.TerminalId).Where(char.IsDigit).ToArray());
        digits = digits.Length > 8 ? digits[^8..] : digits.PadLeft(8, '0');
        byte[] terminalId = ZvtCodec.IntToBcd(long.Parse(digits, CultureInfo.InvariantCulture), 4);
        byte[] registration = [0x19, 0x00, 0x29, .. terminalId, 0x49, .. currencyCode];
        return [ZvtResponses.IntermediateStatus("SumUp-Verbindung OK"), ZvtResponses.Completion(registration)];
    }

    private async Task<IReadOnlyList<byte[]>> PayAsync(ZvtCommand command, CancellationToken cancellationToken)
    {
        long? amount = ZvtCodec.ExtractAmount(command.Data.Span);
        if (!amount.HasValue) return [ZvtResponses.Abort(ZvtResultCode.ProtocolError)];
        if (amount <= 0) return [ZvtResponses.Abort(ZvtResultCode.AmountTooSmall)];
        string terminalKey = string.IsNullOrWhiteSpace(sumUp.TerminalId) ? "<unconfigured>" : sumUp.TerminalId;
        SemaphoreSlim terminalGate = TerminalLocks.GetOrAdd(terminalKey, _ => new(1, 1));
        await terminalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CancellationTokenSource payment = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (stateLock) activePayment = payment;
            string description = receipts.RenderValue("sumup_display", "checkout_description",
                new Dictionary<string, object?> { ["amount"] = Money.Format(amount.Value), ["currency"] = options.Currency },
                "Kassenzahlung {amount} {currency}", 120);
            string reference = $"ZVT-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            CheckoutResult created = await sumUp.CreateCheckoutAsync(new(amount.Value, options.Currency, description, reference), payment.Token).ConfigureAwait(false);
            CheckoutResult result = await sumUp.WaitForPaymentAsync(created.Id, TimeSpan.FromSeconds(options.PaymentTimeoutSeconds), payment.Token).ConfigureAwait(false);
            if (result.Status != "PAID")
                return [ZvtResponses.IntermediateStatus(result.Status == "TIMEOUT" ? "Zahlung Zeitueberschreitung" : "Zahlung abgelehnt"),
                    ZvtResponses.Abort(result.Status == "TIMEOUT" ? ZvtResultCode.AbortTimeoutOrKey : ZvtResultCode.SystemError)];
            string transactionId = string.IsNullOrWhiteSpace(result.TransactionId) ? created.Id : result.TransactionId;
            await journal.AddPaymentAsync(new TransactionRecord
            {
                TerminalId = sumUp.TerminalId,
                AmountCents = amount.Value,
                Currency = options.Currency,
                TransactionId = transactionId,
                CheckoutId = created.Id,
                AuthorizationCode = result.AuthorizationCode,
                Status = "PAID"
            }, cancellationToken).ConfigureAwait(false);
            lock (stateLock) { lastTransactionId = transactionId; lastAmountCents = amount.Value; }
            IReadOnlyList<string> lines = await PaymentReceiptAsync(result, transactionId, amount.Value, cancellationToken).ConfigureAwait(false);
            lock (stateLock) lastReceipt = lines;
            byte? cardType = CardType(result.CardType);
            List<byte[]> responses =
            [
                ZvtResponses.StatusInfo(ZvtResponses.TransactionStatus(ZvtResultCode.Ok, amount.Value, currencyCode,
                    (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000), cardType))
            ];
            responses.AddRange(ZvtResponses.PrintReceipt(lines)); responses.Add(ZvtResponses.Completion()); return responses;
        }
        finally { lock (stateLock) activePayment = null; terminalGate.Release(); }
    }

    private async Task<IReadOnlyList<byte[]>> RefundAsync(ZvtCommand command, bool partialAllowed, CancellationToken cancellationToken)
    {
        string transaction; long original;
        lock (stateLock) { transaction = lastTransactionId; original = lastAmountCents; }
        if (string.IsNullOrWhiteSpace(transaction)) return [ZvtResponses.Abort(ZvtResultCode.ReversalNotPossible)];
        long? requested = partialAllowed ? ZvtCodec.ExtractAmount(command.Data.Span) : null;
        if (requested is <= 0) requested = null;
        long amount = requested ?? original;
        CheckoutResult result = await sumUp.RefundAsync(transaction, requested, cancellationToken).ConfigureAwait(false);
        await journal.AddRefundAsync(new TransactionRecord
        {
            TerminalId = sumUp.TerminalId,
            AmountCents = amount,
            Currency = options.Currency,
            TransactionId = transaction,
            CheckoutId = result.Id,
            Status = "REFUNDED"
        }, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> lines = await ReversalReceiptAsync(transaction, amount, cancellationToken).ConfigureAwait(false);
        lock (stateLock) { lastReceipt = lines; if (!requested.HasValue || amount >= original) { lastTransactionId = string.Empty; lastAmountCents = 0; } }
        List<byte[]> responses = [ZvtResponses.StatusInfo(ZvtResponses.TransactionStatus(ZvtResultCode.Ok, amount, currencyCode,
            (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000)))];
        responses.AddRange(ZvtResponses.PrintReceipt(lines)); responses.Add(ZvtResponses.Completion()); return responses;
    }

    private async Task<IReadOnlyList<byte[]>> ReconcileAsync(ZvtCommand command, bool close, bool print, CancellationToken cancellationToken)
    {
        IReadOnlyList<TransactionRecord> open = await journal.GetOpenAsync(sumUp.TerminalId, cancellationToken).ConfigureAwait(false);
        JournalSummary summary = JournalSummary.From(open);
        List<byte[]> result = [ZvtResponses.StatusInfo(ZvtResponses.TransactionStatus(ZvtResultCode.Ok, summary.NetTotalCents, currencyCode))];
        if (print)
        {
            IReadOnlyList<string> lines = await ReconciliationReceiptAsync(summary, cancellationToken).ConfigureAwait(false);
            lock (stateLock) lastReceipt = lines; result.AddRange(ZvtResponses.PrintReceipt(lines));
        }
        result.Add(ZvtResponses.Completion());
        if (close && options.ResetAfterReconciliation) await journal.CloseOpenAsync(sumUp.TerminalId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<byte[]>> StatusAsync(CancellationToken cancellationToken)
    {
        ConnectionResult connection = await sumUp.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        byte code = connection.Success ? ZvtStatusCode.Ready : ZvtStatusCode.OutOfOrder;
        return [ZvtResponses.StatusInfo([0x27, code]), ZvtResponses.CompletionResult(connection.Success ? ZvtResultCode.Ok : ZvtResultCode.NoConnection)];
    }

    private async Task<IReadOnlyList<byte[]>> DiagnosisAsync(CancellationToken cancellationToken)
    {
        ConnectionResult result = await sumUp.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        return result.Success ? [ZvtResponses.IntermediateStatus("Diagnose OK"), ZvtResponses.CompletionResult(ZvtResultCode.Ok)] :
            [ZvtResponses.IntermediateStatus("Diagnose fehlgeschlagen"), ZvtResponses.Abort(ZvtResultCode.NoConnection)];
    }

    private async Task<IReadOnlyList<byte[]>> AbortAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? active; lock (stateLock) active = activePayment;
        if (active is not null)
        {
            try { await active.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
#pragma warning disable CA1031 // Abbruch bleibt idempotent, auch wenn der externe Terminal-Endpunkt fehlschlägt.
        try { await sumUp.TerminateCheckoutAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { LogTerminationFailure(logger, exception); }
#pragma warning restore CA1031
        return [ZvtResponses.Completion()];
    }

    private IReadOnlyList<byte[]> Reset() { lock (stateLock) { lastTransactionId = string.Empty; lastAmountCents = 0; } return [ZvtResponses.CompletionResult(ZvtResultCode.Ok)]; }
    private IReadOnlyList<byte[]> RepeatReceipt() { lock (stateLock) return lastReceipt.Count == 0 ? [ZvtResponses.Abort(ZvtResultCode.FunctionNotPossible)] : [.. ZvtResponses.PrintReceipt(lastReceipt), ZvtResponses.Completion()]; }

    private async Task<IReadOnlyList<byte[]>> SelfTestAsync(CancellationToken cancellationToken)
    {
        ConnectionResult result = await sumUp.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success) return [ZvtResponses.Abort(ZvtResultCode.NoConnection)];
        IReadOnlyList<string> lines = ["ZVT2SUMUP SELFTEST", $"Terminal: {sumUp.TerminalId}", "SumUp: OK"];
        lock (stateLock) lastReceipt = lines; return [.. ZvtResponses.PrintReceipt(lines), ZvtResponses.Completion()];
    }

    private async Task<IReadOnlyList<string>> PaymentReceiptAsync(CheckoutResult result, string transaction, long amount, CancellationToken cancellationToken)
    {
        string number = await receipts.NextReceiptNumberAsync(cancellationToken).ConfigureAwait(false);
        return receipts.Render("payment", new Dictionary<string, object?>
        {
            ["receipt_number"] = number,
            ["amount"] = Money.Format(amount),
            ["currency"] = options.Currency,
            ["transaction_id"] = transaction,
            ["auth_code"] = result.AuthorizationCode,
            ["payment_method"] = string.IsNullOrWhiteSpace(result.CardType) ? "Karte" : result.CardType,
            ["status_text"] = "Zahlung erfolgt",
            ["terminal_id"] = sumUp.TerminalId,
            ["now"] = DateTimeOffset.Now
        });
    }
    private async Task<IReadOnlyList<string>> ReversalReceiptAsync(string transaction, long amount, CancellationToken cancellationToken) =>
        receipts.Render("reversal", new Dictionary<string, object?>
        {
            ["receipt_number"] = await receipts.NextReceiptNumberAsync(cancellationToken).ConfigureAwait(false),
            ["amount"] = Money.Format(amount),
            ["currency"] = options.Currency,
            ["transaction_id"] = transaction,
            ["terminal_id"] = sumUp.TerminalId,
            ["status_text"] = "Rückerstattung erfolgt",
            ["now"] = DateTimeOffset.Now
        });
    private async Task<IReadOnlyList<string>> ReconciliationReceiptAsync(JournalSummary s, CancellationToken cancellationToken) =>
        receipts.Render("end_of_day", new Dictionary<string, object?>
        {
            ["receipt_number"] = await receipts.NextReceiptNumberAsync(cancellationToken).ConfigureAwait(false),
            ["payment_count"] = s.PaymentCount,
            ["refund_count"] = s.RefundCount,
            ["transaction_count"] = s.TransactionCount,
            ["payment_total"] = Money.Format(s.PaymentTotalCents),
            ["refund_total"] = Money.Format(s.RefundTotalCents),
            ["total_amount"] = Money.Format(s.NetTotalCents),
            ["currency"] = options.Currency,
            ["terminal_id"] = sumUp.TerminalId,
            ["now"] = DateTimeOffset.Now
        });

    private static byte? CardType(string value) => value.ToUpperInvariant() switch
    {
        string x when x.Contains("VISA", StringComparison.Ordinal) => 10,
        string x when x.Contains("MASTER", StringComparison.Ordinal) || x.Contains("MAESTRO", StringComparison.Ordinal) => 6,
        string x when x.Contains("AMEX", StringComparison.Ordinal) => 8,
        string x when x.Contains("GIRO", StringComparison.Ordinal) => 5,
        _ => null
    };

    [LoggerMessage(EventId = 2201, Level = LogLevel.Error, Message = "Fehler bei ZVT-Kommando {Command}")]
    private static partial void LogCommandFailure(ILogger logger, Exception exception, string command);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning, Message = "Reader-Checkout konnte nicht terminiert werden.")]
    private static partial void LogTerminationFailure(ILogger logger, Exception exception);
}

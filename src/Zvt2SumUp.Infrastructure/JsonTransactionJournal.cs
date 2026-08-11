using System.Text.Json;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed class JsonTransactionJournal(string path) : ITransactionJournal, IDisposable
{
    private sealed record JournalFile(int Version, DateTimeOffset UpdatedAt, List<TransactionRecord> Items);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Path { get; } = path;

    public Task AddPaymentAsync(TransactionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AddAsync(record with { Type = "PAYMENT", AmountCents = Math.Abs(record.AmountCents) }, true, cancellationToken);
    }

    public Task AddRefundAsync(TransactionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AddAsync(record with { Type = "REFUND", AmountCents = -Math.Abs(record.AmountCents) }, false, cancellationToken);
    }

    public async Task<IReadOnlyList<TransactionRecord>> GetOpenAsync(string? terminalId = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).Where(x => !x.Closed && Matches(x, terminalId)).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<int> CloseOpenAsync(string? terminalId = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TransactionRecord> items = await LoadCoreAsync(cancellationToken).ConfigureAwait(false); DateTimeOffset now = DateTimeOffset.UtcNow; int count = 0;
            for (int i = 0; i < items.Count; i++) if (!items[i].Closed && Matches(items[i], terminalId)) { items[i] = items[i] with { Closed = true, ClosedAt = now }; count++; }
            await SaveCoreAsync(items, cancellationToken).ConfigureAwait(false); return count;
        }
        finally { gate.Release(); }
    }

    private async Task AddAsync(TransactionRecord record, bool deduplicate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<TransactionRecord> items = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (deduplicate && items.Any(x => (!string.IsNullOrEmpty(record.TransactionId) && x.TransactionId == record.TransactionId) ||
                (!string.IsNullOrEmpty(record.CheckoutId) && x.CheckoutId == record.CheckoutId))) return;
            items.Add(record with { Timestamp = record.Timestamp == default ? DateTimeOffset.UtcNow : record.Timestamp });
            await SaveCoreAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<List<TransactionRecord>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path)) return [];
        try
        {
            using FileStream input = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement; JsonElement list = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
            return JsonSerializer.Deserialize<List<TransactionRecord>>(list.GetRawText(), Options) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            string backup = Path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(Path, backup, false);
            throw new InvalidDataException($"Das Journal ist beschädigt. Sicherung: {backup}", exception);
        }
    }

    private Task SaveCoreAsync(List<TransactionRecord> items, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllBytesAsync(Path, JsonSerializer.SerializeToUtf8Bytes(new JournalFile(1, DateTimeOffset.UtcNow, items), Options), cancellationToken);
    private static bool Matches(TransactionRecord record, string? terminalId) => string.IsNullOrWhiteSpace(terminalId) || string.IsNullOrWhiteSpace(record.TerminalId) || record.TerminalId.Equals(terminalId, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => gate.Dispose();
}

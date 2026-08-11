using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;

namespace Zvt2SumUp.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "zvt2sumup-tests", Guid.NewGuid().ToString("N"));
    public StorageTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task JournalDeduplicatesPaymentsAndClosesOnlySelectedTerminal()
    {
        JsonTransactionJournal journal = new(Path.Combine(root, "journal.json"));
        TransactionRecord a = new() { TerminalId = "A", AmountCents = 100, TransactionId = "tx1", CheckoutId = "co1" };
        await journal.AddPaymentAsync(a); await journal.AddPaymentAsync(a); await journal.AddPaymentAsync(new() { TerminalId = "B", AmountCents = 250, TransactionId = "tx2" });
        Assert.Equal(2, (await journal.GetOpenAsync()).Count); Assert.Equal(1, await journal.CloseOpenAsync("A"));
        IReadOnlyList<TransactionRecord> remaining = await journal.GetOpenAsync(); Assert.Single(remaining); Assert.Equal("B", remaining[0].TerminalId);
    }

    [Fact]
    public async Task JournalConcurrentWritesRemainLossless()
    {
        JsonTransactionJournal journal = new(Path.Combine(root, "journal.json"));
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => journal.AddPaymentAsync(new() { TerminalId = "A", AmountCents = i + 1, TransactionId = $"tx{i}" })));
        IReadOnlyList<TransactionRecord> records = await journal.GetOpenAsync("A"); Assert.Equal(50, records.Count); Assert.Equal(1275, JournalSummary.From(records).PaymentTotalCents);
    }

    [Fact]
    public async Task CorruptJournalIsBackedUpAndReported()
    {
        string path = Path.Combine(root, "journal.json"); await File.WriteAllTextAsync(path, "{broken"); JsonTransactionJournal journal = new(path);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => journal.GetOpenAsync()); Assert.Contains("Sicherung", error.Message); Assert.Single(Directory.GetFiles(root, "*.bak"));
    }

    [Fact]
    public async Task ReceiptNestedPlaceholdersOptionalLinesAndCounterAreSafe()
    {
        string template = Path.Combine(root, "receipt.ini"), counter = Path.Combine(root, "counter.txt");
        await File.WriteAllTextAsync(template, "[settings]\nenabled=true\nfooter=Ende {amount}\n[fiscal]\nreceipt_number_prefix=R-\nreceipt_number_digits=3\n[merchant]\nname=Shop\n[payment]\nlines=\n  {merchant_name}\n  Wert: {footer}\n  Optional: {missing}\n  {long}\n");
        ReceiptTemplateRenderer renderer = new(template, counter); IReadOnlyList<string> lines = renderer.Render("payment", new Dictionary<string, object?> { ["amount"] = "1,23", ["long"] = new string('X', 45) });
        Assert.Equal(["Shop", "Wert: Ende 1,23", new string('X', 40), new string('X', 5)], lines);
        string[] numbers = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => renderer.NextReceiptNumberAsync())); Assert.Equal(25, numbers.Distinct().Count()); Assert.Contains("R-025", numbers);
    }

    [Fact]
    public async Task ConfigurationWritesOnlyEncryptedSecretReferences()
    {
        string path = Path.Combine(root, "config.ini");
        IniOptionsStore store = new(path);
        await store.SaveAsync(new GatewayOptions { MerchantCode = "merchant-test", TerminalId = "terminal-test" });

        string text = await File.ReadAllTextAsync(path);
        Assert.Contains("api_key = {{encrypted:secrets.dat:api_key}}", text);
        Assert.Contains("affiliate_key = {{encrypted:secrets.dat:affiliate_key}}", text);
        Assert.Contains("affiliate_app_id = {{encrypted:secrets.dat:affiliate_app_id}}", text);
        Assert.Contains("keine Secrets in dieser Datei speichern", text);
    }

    [Fact]
    public void SecretRedactionCoversHeadersKeysAndPairingCodes()
    {
        const string input = "Authorization: Bearer abc123 api_key=xyz pairing code: 4WLFDSBF " +
            "{\"affiliate_key\":\"affiliate-secret-value\",\"pairing_code\":\"9A7B6C5D\"} " +
            "?access_token=query-secret-value&client_secret=client-secret-value";
        string redacted = SensitiveDataRedactor.Redact(input);

        foreach (string secret in new[] { "abc123", "xyz", "4WLFDSBF", "affiliate-secret-value", "9A7B6C5D", "query-secret-value", "client-secret-value" })
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("redigiert", redacted);
    }

    [Fact]
    public void ConfigurationRejectsLogPathTraversalAndInvalidUpdateRepository()
    {
        GatewayOptions traversal = new() { LogFile = "..\\outside.log" };
        Assert.Contains(traversal.Validate(false), error => error.Contains("Datenordner", StringComparison.OrdinalIgnoreCase));
        GatewayOptions repository = new() { UpdateRepository = "https://example.invalid/repository" };
        Assert.Contains(repository.Validate(false), error => error.Contains("Update-Repository", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(Path.GetFullPath(AppPaths.Root), AppPaths.ResolveLogFile("logs/safe.log"), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

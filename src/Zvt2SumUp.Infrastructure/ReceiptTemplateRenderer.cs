using System.Globalization;
using System.Text.RegularExpressions;
using Zvt2SumUp.Core;

namespace Zvt2SumUp.Infrastructure;

public sealed partial class ReceiptTemplateRenderer : IReceiptRenderer, IDisposable
{
    private readonly string templatePath;
    private readonly string counterPath;
    private readonly SemaphoreSlim counterGate = new(1, 1);
    private IniDocument templates;

    public ReceiptTemplateRenderer(string templatePath, string counterPath)
    {
        this.templatePath = templatePath; this.counterPath = counterPath;
        if (!File.Exists(templatePath)) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(templatePath)!); File.WriteAllText(templatePath, DefaultTemplate); }
        templates = IniDocument.Parse(File.ReadAllText(templatePath));
    }

    public void Reload() => templates = IniDocument.Parse(File.ReadAllText(templatePath));

    public async Task<string> NextReceiptNumberAsync(CancellationToken cancellationToken = default)
    {
        await counterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long current = 0;
            if (File.Exists(counterPath) && !long.TryParse(
                    await File.ReadAllTextAsync(counterPath, cancellationToken).ConfigureAwait(false),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out current))
                throw new InvalidDataException("Der Belegzähler ist beschädigt.");
            current = checked(current + 1); await AtomicFile.WriteAllTextAsync(counterPath, current.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            string prefix = templates.Get("fiscal", "receipt_number_prefix", "ZVT-"); int digits = templates.GetInt("fiscal", "receipt_number_digits", 7);
            return prefix + current.ToString(new string('0', Math.Clamp(digits, 1, 18)), CultureInfo.InvariantCulture);
        }
        finally { counterGate.Release(); }
    }

    public IReadOnlyList<string> Render(string section, IReadOnlyDictionary<string, object?> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!templates.GetBool("settings", "enabled", true)) return [];
        Dictionary<string, string> values = BuildContext(context); string raw = templates.Get(section, "lines"); List<string> output = [];
        foreach (string input in raw.Replace("\\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = Replace(input.Trim(), values).Trim();
            if (line.Length == 0 || IsEmptyOptionalLine(line)) continue;
            for (int index = 0; index < line.Length; index += 40) output.Add(line.Substring(index, Math.Min(40, line.Length - index)).Replace("€", "EUR", StringComparison.Ordinal));
        }
        return output;
    }

    public string RenderValue(string section, string optionName, IReadOnlyDictionary<string, object?> context, string fallback, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fallback);
        string value = Replace(templates.Get(section, optionName, fallback), BuildContext(context));
        value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("€", "EUR", StringComparison.Ordinal);
        return value.Length > maximumLength ? value[..maximumLength].TrimEnd() : value;
    }

    private Dictionary<string, string> BuildContext(IReadOnlyDictionary<string, object?> source)
    {
        DateTimeOffset now = source.TryGetValue("now", out object? time) && time is DateTimeOffset dto ? dto : DateTimeOffset.Now;
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase)
        {
            ["merchant_name"] = templates.Get("merchant", "name"),
            ["merchant_address_line1"] = templates.Get("merchant", "address_line1"),
            ["merchant_address_line2"] = templates.Get("merchant", "address_line2"),
            ["merchant_tax_id"] = templates.Get("merchant", "tax_id"),
            ["merchant_vat_id"] = templates.Get("merchant", "vat_id"),
            ["terminal_id"] = templates.Get("merchant", "terminal_id"),
            ["item_line1"] = templates.Get("fiscal", "item_line1"),
            ["item_line2"] = templates.Get("fiscal", "item_line2"),
            ["item_line3"] = templates.Get("fiscal", "item_line3"),
            ["vat_label"] = templates.Get("fiscal", "vat_label"),
            ["payment_method"] = templates.Get("fiscal", "payment_method", "Karte"),
            ["footer"] = templates.Get("settings", "footer"),
            ["date"] = now.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["time"] = now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        };
        foreach (string key in new[] { "tse_certificate", "tse_transaction_number", "tse_signature_counter", "tse_start", "tse_end", "tse_serial", "tse_signature", "tse_process_type" }) result[key] = templates.Get("fiscal", key);
        foreach ((string key, object? value) in source) result[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return result;
    }

    private static string Replace(string source, Dictionary<string, string> values)
    {
        string value = source;
        for (int pass = 0; pass < 3; pass++) { string replaced = Placeholder().Replace(value, m => values.GetValueOrDefault(m.Groups[1].Value, string.Empty)); if (replaced == value) break; value = replaced; }
        return value;
    }
    private static bool IsEmptyOptionalLine(string value) =>
        value.Contains(':', StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(value[(value.IndexOf(':', StringComparison.Ordinal) + 1)..]);
    [GeneratedRegex("\\{([A-Za-z0-9_]+)\\}")] private static partial Regex Placeholder();

    private const string DefaultTemplate = """
[settings]
enabled = true
footer = Vielen Dank fuer Ihren Besuch

[merchant]
name = Muster GmbH
address_line1 = Musterstrasse 1
address_line2 = 50667 Koeln
tax_id = St.-Nr.: 000000000
vat_id = USt-IdNr.: DE000000000
terminal_id = SUMUP001

[fiscal]
receipt_number_prefix = ZVT-
receipt_number_digits = 7
item_line1 = 1x Kartenzahlung laut Kasse {amount} {currency}
item_line2 =
item_line3 =
tax_rate_percent = 19
vat_label = B 19%
payment_method = Karte
tse_certificate =
tse_transaction_number =
tse_signature_counter =
tse_start =
tse_end =
tse_serial =
tse_signature =
tse_process_type =

[sumup_display]
checkout_description = Kassenzahlung {amount} {currency}

[payment]
lines = {merchant_name}\n{merchant_address_line1}\n{merchant_address_line2}\n--------------------------------\n{item_line1}\n{item_line2}\n{item_line3}\n--------------------------------\nZu bezahlen: {amount} {currency}\n{payment_method}: {amount} {currency}\nDatum: {date}\nUhrzeit: {time}\nBon-Nr.: {receipt_number}\nTerminal-ID: {terminal_id}\nTransaktion: {transaction_id}\nAuth-Code: {auth_code}\nStatus: {status_text}\n{footer}

[reversal]
lines = {merchant_name}\nSTORNO / RUECKERSTATTUNG\nDatum: {date}\nUhrzeit: {time}\nBon-Nr.: {receipt_number}\nBetrag: {amount} {currency}\nTransaktion: {transaction_id}\nStatus: {status_text}\n{footer}

[end_of_day]
lines = {merchant_name}\nTAGESABSCHLUSS ZAHLTERMINAL\nDatum: {date}\nUhrzeit: {time}\nBeleg-Nr.: {receipt_number}\nTerminal-ID: {terminal_id}\nZahlungen: {payment_count}\nSumme Zahlungen: {payment_total} {currency}\nStornos: {refund_count}\nSumme Stornos: {refund_total} {currency}\nNetto-Gesamt: {total_amount} {currency}\nTransaktionen: {transaction_count}\nHinweis: Kein fiskalischer KORONA-/TSE-Bon.\n{footer}
""";

    public void Dispose() => counterGate.Dispose();
}

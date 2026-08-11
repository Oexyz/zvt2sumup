using System.Text;

namespace Zvt2SumUp.Infrastructure;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Sections => sections.Keys;

    public static IniDocument Parse(string text)
    {
        IniDocument document = new(); string section = string.Empty; string? lastKey = null;
        using StringReader reader = new(text ?? string.Empty); string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#') continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1].Trim(); lastKey = null; document.Ensure(section); continue; }
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0 && char.IsWhiteSpace(line[0]) && lastKey is not null && !string.IsNullOrWhiteSpace(section))
            { document.Set(section, lastKey, document.Get(section, lastKey) + "\n" + trimmed); continue; }
            if (separator < 0 || string.IsNullOrWhiteSpace(section)) continue;
            lastKey = line[..separator].Trim(); document.Set(section, lastKey, line[(separator + 1)..].Trim());
        }
        return document;
    }

    public string Get(string section, string key, string fallback = "") =>
        sections.TryGetValue(section, out Dictionary<string, string>? values) && values.TryGetValue(key, out string? value) ? value : fallback;
    public int GetInt(string section, string key, int fallback) => int.TryParse(Get(section, key), out int value) ? value : fallback;
    public bool GetBool(string section, string key, bool fallback) => bool.TryParse(Get(section, key), out bool value) ? value : fallback;
    public void Set(string section, string key, object? value) => Ensure(section)[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public IReadOnlyDictionary<string, string> GetSection(string section) => Ensure(section);

    public override string ToString()
    {
        StringBuilder output = new();
        foreach ((string name, Dictionary<string, string> values) in sections)
        {
            output.Append('[').Append(name).AppendLine("]");
            foreach ((string key, string value) in values) output.Append(key).Append(" = ").AppendLine(value);
            output.AppendLine();
        }
        return output.ToString();
    }

    private Dictionary<string, string> Ensure(string section)
    {
        if (!sections.TryGetValue(section, out Dictionary<string, string>? values)) sections[section] = values = new(StringComparer.OrdinalIgnoreCase);
        return values;
    }
}

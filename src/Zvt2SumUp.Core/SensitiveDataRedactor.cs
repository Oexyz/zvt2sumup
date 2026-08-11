using System.Text.RegularExpressions;

namespace Zvt2SumUp.Core;

public static partial class SensitiveDataRedactor
{
    [GeneratedRegex("(?i)([\"']?authorization[\"']?\\s*[:=]\\s*[\"']?(?:bearer\\s+)?)[^\"\\s,;}]+")]
    private static partial Regex AuthorizationPattern();
    [GeneratedRegex("(?i)([\"']?(?:api[_-]?key|affiliate[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token|password)[\"']?\\s*[:=]\\s*[\"']?)[^\"\\s,;}]+")]
    private static partial Regex NamedSecretPattern();
    [GeneratedRegex("(?i)([\"']?pairing[_ -]?code[\"']?\\s*[:=]\\s*[\"']?)[A-Z0-9 .-]{8,14}")]
    private static partial Regex PairingCodePattern();

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string result = AuthorizationPattern().Replace(value, "$1<redigiert>");
        result = NamedSecretPattern().Replace(result, "$1<redigiert>");
        return PairingCodePattern().Replace(result, "$1<redigiert>");
    }

    public static string Mask(string? secret) => string.IsNullOrEmpty(secret) ? string.Empty :
        secret.Length <= 4 ? "••••" : $"{secret[..2]}••••{secret[^2..]}";
}

namespace Zvt2SumUp.Core;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZVT2SumUp");
    public static string Configuration => Path.Combine(Root, "config.ini");
    public static string Secrets => Path.Combine(Root, "secrets.dat");
    public static string Journal => Path.Combine(Root, "transaction_journal.json");
    public static string ReceiptTemplates => Path.Combine(Root, "receipt_templates.ini");
    public static string ReceiptCounter => Path.Combine(Root, "receipt_counter.txt");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Updates => Path.Combine(Root, "updates");

    public static string ResolveLogFile(string configuredPath)
    {
        string relative = string.IsNullOrWhiteSpace(configuredPath) ? Path.Combine("logs", "zvt2sumup.log") : configuredPath.Trim();
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Die Logdatei muss relativ zum ZVT2SumUp-Datenordner angegeben werden.");
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Die Logdatei darf den ZVT2SumUp-Datenordner nicht verlassen.");
        return full;
    }
}

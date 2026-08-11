using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;
using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Tools;

internal sealed class CashRegisterSimulator(string host, int port) : IAsyncDisposable
{
    private TcpClient? client;
    private NetworkStream? stream;

    public bool Connected => client?.Connected == true && stream is not null;

    public static async Task<int> RunMenuAsync(string[] args)
    {
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        GatewayOptions options = await optionsStore.LoadAsync().ConfigureAwait(false);
        string targetHost = Value(args, "--host") ?? options.TcpHost;
        int targetPort = int.TryParse(Value(args, "--port"), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort)
            ? parsedPort
            : options.TcpPort;
        if (targetPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(args), "Der TCP-Port muss zwischen 1 und 65535 liegen.");

        await using CashRegisterSimulator simulator = new(targetHost, targetPort);
        Console.Title = "ZVT2SumUp - Kassensimulator";
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            simulator.Disconnect();
        };

        if (!IsLoopback(targetHost))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"WARNUNG: Der Kassensimulator soll eine externe Adresse kontaktieren: {targetHost}:{targetPort}");
            Console.ResetColor();
            Console.Write("Zum Fortfahren exakt EXTERN eingeben: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "EXTERN", StringComparison.Ordinal)) return 2;
        }

        while (true)
        {
            PrintMenu(simulator, targetHost, targetPort);
            Console.Write("Auswahl: ");
            string? choice = Console.ReadLine()?.Trim();
            try
            {
                switch (choice)
                {
                    case "0":
                        simulator.Disconnect();
                        return 0;
                    case "1":
                        await simulator.ConnectAsync().ConfigureAwait(false);
                        break;
                    case "2":
                        await simulator.SendAsync(Registration(), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        break;
                    case "3":
                        await simulator.SendPaymentInteractiveAsync().ConfigureAwait(false);
                        break;
                    case "4":
                        if (Confirm("STORNO", "Storno kann eine reale Rückerstattung auslösen."))
                            await simulator.SendAsync(ZvtCodec.BuildApdu(0x06, 0x30), TimeSpan.FromSeconds(130)).ConfigureAwait(false);
                        break;
                    case "5":
                        if (Confirm("REFUND", "Refund kann eine reale vollständige Rückerstattung auslösen."))
                            await simulator.SendAsync(ZvtCodec.BuildApdu(0x06, 0x31), TimeSpan.FromSeconds(130)).ConfigureAwait(false);
                        break;
                    case "6":
                        if (Confirm("KASSENSCHNITT", "Kassenschnitt kann offene Journalposten schließen."))
                            await simulator.SendAsync(ZvtCodec.BuildApdu(0x06, 0x50), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        break;
                    case "7":
                        await simulator.SendAsync(ZvtCodec.BuildApdu(0x05, 0x01), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        break;
                    case "8":
                        await simulator.SendAsync(ZvtCodec.BuildApdu(0x06, 0x70), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        break;
                    case "9":
                        await simulator.SendAsync(ZvtCodec.BuildApdu(0x06, 0xB0), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        break;
                    case "10":
                        simulator.Disconnect();
                        break;
                    default:
                        WriteWarning("Bitte eine Zahl von 0 bis 10 eingeben.");
                        break;
                }
            }
            catch (Exception exception)
            {
                simulator.Disconnect();
                WriteError(SensitiveDataRedactor.Redact(exception.Message));
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Connected) { WriteSuccess("Die bestehende Gateway-Verbindung wird weiterverwendet."); return; }
        Disconnect();
        TcpClient next = new();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await next.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            client = next;
            stream = next.GetStream();
            WriteSuccess($"Verbunden mit dem Gateway auf {host}:{port}.");
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    public void Disconnect()
    {
        stream?.Dispose();
        client?.Dispose();
        stream = null;
        client = null;
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        await ValueTask.CompletedTask;
    }

    private async Task SendPaymentInteractiveAsync()
    {
        Console.Write("Betrag in EUR, zum Beispiel 1,00: ");
        string raw = Console.ReadLine()?.Trim() ?? string.Empty;
        if (!TryParseAmount(raw, out long amountCents))
        {
            WriteWarning("Ungültiger Betrag. Höchstens zwei Nachkommastellen und ein positiver Wert sind erlaubt.");
            return;
        }
        string amount = Money.Format(amountCents);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"ACHTUNG: Dies kann eine reale Zahlung über {amount} EUR am verbundenen SumUp-Terminal auslösen.");
        Console.ResetColor();
        Console.Write($"Zum Senden exakt ZAHLUNG {amount} eingeben: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), $"ZAHLUNG {amount}", StringComparison.Ordinal))
        {
            WriteWarning("Zahlung nicht gesendet.");
            return;
        }

        byte[] payload = [0x04, .. ZvtCodec.IntToBcd(amountCents, 6), 0x49, 0x09, 0x78, 0x19, 0x40];
        await SendAsync(ZvtCodec.BuildApdu(0x06, 0x01, payload), TimeSpan.FromSeconds(130)).ConfigureAwait(false);
    }

    private async Task SendAsync(byte[] apdu, TimeSpan timeout)
    {
        await ConnectAsync().ConfigureAwait(false);
        NetworkStream activeStream = stream ?? throw new IOException("Gateway-Verbindung fehlt.");
        PrintFrame("TX", apdu);
        await activeStream.WriteAsync(TcpFrameDecoder.Frame(apdu, TcpTransport.LengthPrefixed)).ConfigureAwait(false);
        await activeStream.FlushAsync().ConfigureAwait(false);

        using CancellationTokenSource operationTimeout = new(timeout);
        while (true)
        {
            byte[]? response;
            try { response = await ReadFrameAsync(activeStream, operationTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (operationTimeout.IsCancellationRequested)
            {
                WriteWarning("Zeitüberschreitung: Keine abschließende Gateway-Antwort empfangen.");
                return;
            }
            if (response is null) throw new IOException("Das Gateway hat die Verbindung geschlossen.");
            PrintFrame("RX", response);
            if (!ZvtCodec.TryParseApdu(response, out ZvtCommand? command, out int consumed) || consumed != response.Length)
                throw new InvalidDataException("Das Gateway lieferte keine gültige, eindeutig begrenzte ZVT-APDU.");

            PrintDetails(command!);
            if (!command!.IsAcknowledgement)
            {
                byte[] acknowledgement = ZvtCodec.BuildApdu(0x80, 0x00);
                await activeStream.WriteAsync(TcpFrameDecoder.Frame(acknowledgement, TcpTransport.LengthPrefixed), operationTimeout.Token).ConfigureAwait(false);
                await activeStream.FlushAsync(operationTimeout.Token).ConfigureAwait(false);
                PrintFrame("TX", acknowledgement);
            }

            if (command.Id is ZvtCommandIds.Completion or ZvtCommandIds.AbortResponse) return;
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        if (!await ReadExactlyOrEndAsync(network, header, cancellationToken).ConfigureAwait(false)) return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length is < 3 or > TcpFrameDecoder.MaximumApduLength) throw new InvalidDataException("Ungültige TCP-ZVT-Länge empfangen.");
        byte[] frame = new byte[length];
        return await ReadExactlyOrEndAsync(network, frame, cancellationToken).ConfigureAwait(false) ? frame : null;
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await source.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static byte[] Registration() =>
        ZvtCodec.BuildApdu(0x06, 0x00, [0x00, 0x00, 0x00, 0x00, 0x09, 0x78]);

    private static void PrintMenu(CashRegisterSimulator simulator, string targetHost, int targetPort)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("============================================================");
        Console.WriteLine(" ZVT2SumUp KASSENSIMULATOR - ZVT 13.13 -> Gateway");
        Console.WriteLine("============================================================");
        Console.ResetColor();
        Console.WriteLine($" Gateway: {targetHost}:{targetPort}");
        Console.Write(" Status:  ");
        Console.ForegroundColor = simulator.Connected ? ConsoleColor.Green : ConsoleColor.DarkYellow;
        Console.WriteLine(simulator.Connected ? "VERBUNDEN" : "GETRENNT");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  1) Verbinden / Verbindung prüfen");
        Console.WriteLine("  2) Registrierung senden       06 00");
        Console.WriteLine("  3) Zahlung senden             06 01   [reale Wirkung möglich]");
        Console.WriteLine("  4) Storno senden              06 30   [reale Wirkung möglich]");
        Console.WriteLine("  5) Refund senden              06 31   [reale Wirkung möglich]");
        Console.WriteLine("  6) Kassenschnitt senden       06 50   [Journaländerung möglich]");
        Console.WriteLine("  7) Statusabfrage              05 01");
        Console.WriteLine("  8) Diagnose                   06 70");
        Console.WriteLine("  9) Abbruch                    06 B0");
        Console.WriteLine(" 10) Verbindung trennen");
        Console.WriteLine("  0) Beenden");
        Console.WriteLine();
    }

    private static void PrintFrame(string direction, byte[] apdu)
    {
        string name = ZvtCodec.TryParseApdu(apdu, out ZvtCommand? command, out _) ? command!.Name : "Ungültige APDU";
        Console.ForegroundColor = direction == "TX" ? ConsoleColor.Cyan : ConsoleColor.Green;
        Console.WriteLine($"{direction} {name}");
        Console.ResetColor();
        Console.WriteLine($"   {Convert.ToHexString(apdu).Chunk(2).Select(pair => new string(pair)).Aggregate((left, right) => left + " " + right)}");
    }

    private static void PrintDetails(ZvtCommand command)
    {
        if (ZvtCodec.ExtractAmount(command.Data.Span) is long amount) Console.WriteLine($"   Betrag: {Money.Format(amount)} EUR");
        if (command.Id == 0x040F && ZvtCodec.GetBmpValue(command.Data.Span, 0x27) is { } result && !result.IsEmpty)
            Console.WriteLine($"   Result-Code: 0x{result.Span[0]:X2}");
        if (command.Id == ZvtCommandIds.AbortResponse && !command.Data.IsEmpty)
            Console.WriteLine($"   Abbruchcode: 0x{command.Data.Span[0]:X2}");
        if (command.Id == 0x04FF && ZvtCodec.GetBmpValue(command.Data.Span, 0x24) is { } statusText)
            Console.WriteLine("   Status: " + ZvtCodec.DecodeText(statusText.Span));
        if (command.Id == 0x06D1 && !command.Data.IsEmpty)
            Console.WriteLine("   Druck: " + ZvtCodec.DecodeText(command.Data.Span[1..]).Trim());
    }

    private static bool Confirm(string exact, string warning)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("ACHTUNG: " + warning);
        Console.ResetColor();
        Console.Write($"Zum Senden exakt {exact} eingeben: ");
        bool confirmed = string.Equals(Console.ReadLine()?.Trim(), exact, StringComparison.Ordinal);
        if (!confirmed) WriteWarning("Befehl nicht gesendet.");
        return confirmed;
    }

    private static bool TryParseAmount(string value, out long cents)
    {
        cents = 0;
        string normalized = value.Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount) || amount <= 0 || decimal.Round(amount, 2) != amount)
            return false;
        try { cents = Money.ToMinor(amount); return cents is > 0 and <= 999_999_999_999; }
        catch (OverflowException) { return false; }
    }

    private static bool IsLoopback(string value) =>
        value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(value, out IPAddress? address) && IPAddress.IsLoopback(address);

    private static string? Value(string[] args, string name)
    {
        int index = Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void WriteSuccess(string message) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(message); Console.ResetColor(); }
    private static void WriteWarning(string message) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(message); Console.ResetColor(); }
    private static void WriteError(string message) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("FEHLER: " + message); Console.ResetColor(); }
}

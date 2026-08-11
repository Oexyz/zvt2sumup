using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;
using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Tools;

internal static class Program
{
    private const string ServiceName = "ZVT2SumUpGateway";
    public static async Task<int> Main(string[] args)
    {
        int exitCode;
        try
        {
            string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "cash-register-simulator";
            exitCode = command switch
            {
                "cash-register-simulator" or "cash-simulator" => await CashRegisterSimulator.RunMenuAsync(args),
                "help" or "--help" or "-h" => Help(),
                "install" => await InstallServiceAsync(args.Skip(1).FirstOrDefault()),
                "uninstall" => await ScAsync("delete", ServiceName),
                "start" => await ScAsync("start", ServiceName),
                "stop" => await ScAsync("stop", ServiceName),
                "restart" => await RestartServiceAsync(),
                "status" => await ScAsync("query", ServiceName),
                "run-console" => await RunConsoleAsync(),
                "com-list" => ListPorts(),
                "com0com-detect" => DetectCom0Com(),
                "register" => await SendCommandAsync(ZvtCodec.BuildApdu(0x06, 0x00, [0, 0, 0, 0, 0x09, 0x78]), args),
                "status-zvt" => await SendCommandAsync(ZvtCodec.BuildApdu(0x05, 0x01), args),
                "reconcile" => await SendReconciliationAsync(args),
                "refund" => await SendRefundAsync(args),
                "payment" => await SendPaymentAsync(args),
                "raw" => await SendRawAsync(args),
                "gateway-simulator" => await GatewaySimulatorAsync(args),
                "sumup-test" => await SumUpTestAsync(),
                "transactions" => await SumUpTransactionsAsync(),
                "verify-release" => await VerifyReleaseAsync(),
                "update" => await BeginSecureUpdateAsync(args),
                "apply-update" => await ApplySecureUpdateAsync(args),
                "cleanup-update" => await CleanupSecureUpdateAsync(args),
                _ => throw new ArgumentException($"Unbekannter Befehl: {command}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FEHLER: " + SensitiveDataRedactor.Redact(exception.Message));
            exitCode = 1;
        }

        if (args.Contains("--wait", StringComparer.OrdinalIgnoreCase)) WaitForDismissal();
        return exitCode;
    }

    private static void WaitForDismissal()
    {
        Console.WriteLine();
        Console.Write("Zum Schließen eine Taste drücken ...");
        try { Console.ReadKey(intercept: true); }
        catch (InvalidOperationException)
        {
            // Bei ungewöhnlichen Shell-Hosts ohne lesbare Console bleibt die
            // Ausgabe trotzdem lange genug sichtbar, statt sofort zu verschwinden.
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
ZVT2SumUp.Tools - sichere Diagnose und Dienstverwaltung

  cash-register-simulator Interaktiver Kassensimulator; Standard beim Doppelklick
  install [service-exe]   Dienst installieren + Recovery konfigurieren
  uninstall               Dienst entfernen
  start|stop|restart      Dienst steuern
  status                  Dienststatus
  run-console             Gateway interaktiv starten
  com-list                vorhandene COM-Ports
  com0com-detect          com0com erkennen (keine Installation)
  register                ZVT-Registrierung lokal senden
  status-zvt              ZVT-Status lokal senden
  reconcile               ZVT-Kassenschnitt lokal senden (ändert Journalstatus)
  refund                  ZVT-Refund lokal senden (kann real erstatten!)
  payment <Cent>          ZVT-Testzahlung senden (kann real auslösen!)
  raw <Hex>               rohe ZVT-APDU senden (gefährlich; separat bestätigen)
  gateway-simulator       lokales Fake-Terminal; keine SumUp-Zahlung
  sumup-test              API-Zugriff und Terminals prüfen; keine Zahlung
  transactions            letzte SumUp-Transaktionen anzeigen; keine Zahlung
  verify-release          aktuelles GitHub-Release herunterladen und prüfen; keine Installation
  update                   geprüftes GitHub-Update installieren (UAC erforderlich)

Optionen für Kassenbefehle: --host 127.0.0.1 --port 20007 --length-prefixed
Zahlung: --confirm-real-payment | Refund: --confirm-real-refund
Kassenschnitt: --confirm-reconciliation | RAW: --confirm-raw-command
Zustandsändernde RAW-Kommandos verlangen zusätzlich ihren passenden Schalter.
Ein Update erfordert --confirm-update und wird normalerweise von der GUI gestartet.
"""); return 0;
    }

    private static async Task<int> RunConsoleAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions options = await optionsStore.LoadAsync(); GatewaySecrets secrets = await secretStore.LoadAsync();
        IReadOnlyList<string> errors = options.Validate(true, secrets.HasApiKey); if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); builder.Logging.SetMinimumLevel(RuntimeLogging.ParseLevel(options.LogLevel));
        builder.Logging.AddConsole(); builder.Logging.AddProvider(new RollingFileLoggerProvider(AppPaths.ResolveLogFile(options.LogFile)));
        builder.Services.AddZvt2SumUpRuntime(options, secrets); using IHost host = builder.Build(); await host.StartAsync(); IGatewayRuntime runtime = host.Services.GetRequiredService<IGatewayRuntime>();
        using CancellationTokenSource stop = new(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await runtime.StartAsync(stop.Token); Console.WriteLine("Gateway läuft. Strg+C beendet sicher.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); } catch (OperationCanceledException) { }
        await runtime.StopAsync(); await host.StopAsync(); return 0;
    }

    private static async Task<int> SumUpTestAsync()
    {
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions options = await optionsStore.LoadAsync(); GatewaySecrets secrets = await secretStore.LoadAsync();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); builder.Services.AddZvt2SumUpRuntime(options, secrets); using IHost host = builder.Build(); await host.StartAsync();
        ISumUpClient client = host.Services.GetRequiredService<ISumUpClient>(); ConnectionResult result = await client.TestConnectionAsync(CancellationToken.None);
        Console.WriteLine(result.Success ? $"OK: {result.BusinessName} ({result.MerchantCode})" : "FEHLER: " + result.Error);
        if (result.Success) foreach (TerminalDescriptor terminal in await client.GetTerminalsAsync(CancellationToken.None)) Console.WriteLine($"{terminal.Id} | {terminal.Name} | {terminal.Status}");
        return result.Success ? 0 : 2;
    }

    private static async Task<int> SumUpTransactionsAsync()
    {
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions options = await optionsStore.LoadAsync();
        GatewaySecrets secrets = await secretStore.LoadAsync();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddZvt2SumUpRuntime(options, secrets);
        using IHost host = builder.Build();
        await host.StartAsync();
        ISumUpClient client = host.Services.GetRequiredService<ISumUpClient>();
        IReadOnlyList<CheckoutResult> transactions = await client.GetTransactionsAsync(20, CancellationToken.None);
        if (transactions.Count == 0) Console.WriteLine("Keine Transaktionen gefunden.");
        foreach (CheckoutResult transaction in transactions)
            Console.WriteLine($"{transaction.Status,-10} {Money.Format(transaction.AmountCents),10} {transaction.Currency,-3}  {transaction.TransactionId}");
        return 0;
    }

    private static async Task<int> SendPaymentAsync(string[] args)
    {
        if (args.Length < 2 || !long.TryParse(args[1], out long cents) || cents <= 0) throw new ArgumentException("Betrag in Cent fehlt oder ist ungültig.");
        if (!args.Contains("--confirm-real-payment", StringComparer.Ordinal)) throw new InvalidOperationException("Zahlung nicht gesendet. Für einen bewusst realen Test zusätzlich --confirm-real-payment angeben.");
        return await SendCommandAsync(ZvtCodec.BuildApdu(0x06, 0x01, [0x04, .. ZvtCodec.IntToBcd(cents, 6), 0x49, 0x09, 0x78, 0x19, 0x40]), args);
    }

    private static Task<int> SendRefundAsync(string[] args)
    {
        RequireConfirmation(args, "--confirm-real-refund",
            "Refund nicht gesendet. Für eine bewusst reale Rückerstattung zusätzlich --confirm-real-refund angeben.");
        return SendCommandAsync(ZvtCodec.BuildApdu(0x06, 0x31), args);
    }

    private static Task<int> SendReconciliationAsync(string[] args)
    {
        RequireConfirmation(args, "--confirm-reconciliation",
            "Kassenschnitt nicht gesendet. Er kann offene Journalposten schließen; zusätzlich --confirm-reconciliation angeben.");
        return SendCommandAsync(ZvtCodec.BuildApdu(0x06, 0x50), args);
    }

    private static Task<int> SendRawAsync(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Hex-APDU fehlt.");
        RequireConfirmation(args, "--confirm-raw-command",
            "RAW-APDU nicht gesendet. Beliebige APDUs können reale Vorgänge auslösen; zusätzlich --confirm-raw-command angeben.");
        byte[] bytes = Convert.FromHexString(args[1].Replace(" ", string.Empty, StringComparison.Ordinal));
        if (!ZvtCodec.TryParseApdu(bytes, out ZvtCommand? command, out int consumed) || consumed != bytes.Length)
            throw new ArgumentException("Ungültige oder nicht eindeutig begrenzte RAW-APDU.");
        switch (command!.Id)
        {
            case ZvtCommandIds.Authorization:
                RequireConfirmation(args, "--confirm-real-payment",
                    "Zahlungs-APDU nicht gesendet. Zusätzlich --confirm-real-payment angeben.");
                break;
            case ZvtCommandIds.Reversal or ZvtCommandIds.Refund:
                RequireConfirmation(args, "--confirm-real-refund",
                    "Refund-/Storno-APDU nicht gesendet. Zusätzlich --confirm-real-refund angeben.");
                break;
            case ZvtCommandIds.Reconciliation:
                RequireConfirmation(args, "--confirm-reconciliation",
                    "Kassenschnitt-APDU nicht gesendet. Zusätzlich --confirm-reconciliation angeben.");
                break;
            case ZvtCommandIds.Reset:
                RequireConfirmation(args, "--confirm-state-change",
                    "Reset-APDU nicht gesendet. Zusätzlich --confirm-state-change angeben.");
                break;
        }
        return SendCommandAsync(bytes, args);
    }

    private static async Task<int> SendCommandAsync(byte[] apdu, string[] args)
    {
        string host = Value(args, "--host") ?? "127.0.0.1"; int port = int.TryParse(Value(args, "--port"), out int parsed) ? parsed : 20007;
        TcpTransport transport = args.Contains("--length-prefixed", StringComparer.OrdinalIgnoreCase) ? TcpTransport.LengthPrefixed : TcpTransport.RawApdu;
        using TcpClient client = new(); await client.ConnectAsync(host, port); NetworkStream stream = client.GetStream();
        byte[] request = TcpFrameDecoder.Frame(apdu, transport); Console.WriteLine("TX " + BitConverter.ToString(apdu).Replace('-', ' ')); await stream.WriteAsync(request);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(130)); TcpFrameDecoder decoder = new(); byte[] buffer = new byte[8192];
        while (!timeout.IsCancellationRequested)
        {
            int read; try { read = await stream.ReadAsync(buffer, timeout.Token); } catch (OperationCanceledException) { break; }
            if (read == 0) break;
            foreach ((byte[] response, _) in decoder.Push(buffer.AsSpan(0, read)))
            {
                Console.WriteLine("RX " + BitConverter.ToString(response).Replace('-', ' '));
                if (ZvtCodec.TryParseApdu(response, out ZvtCommand? parsedResponse, out _) && parsedResponse!.Id is ZvtCommandIds.Completion or ZvtCommandIds.AbortResponse) return 0;
            }
        }
        return 0;
    }

    private static async Task<int> GatewaySimulatorAsync(string[] args)
    {
        int port = int.TryParse(Value(args, "--port"), out int parsed) ? parsed : 20008; TcpListener listener = new(IPAddress.Loopback, port); listener.Start();
        Console.WriteLine($"Fake-Gateway auf 127.0.0.1:{port}. Keine SumUp-Verbindung. Strg+C beendet."); using CancellationTokenSource stop = new(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token); NetworkStream stream = client.GetStream(); TcpFrameDecoder decoder = new(); byte[] buffer = new byte[4096];
                int read = await stream.ReadAsync(buffer, stop.Token); foreach ((byte[] apdu, TcpTransport transport) in decoder.Push(buffer.AsSpan(0, read)))
                {
                    if (!ZvtCodec.TryParseApdu(apdu, out ZvtCommand? command, out _)) continue;
                    await stream.WriteAsync(TcpFrameDecoder.Frame(ZvtResponses.Ack(), transport), stop.Token);
                    byte[][] responses = command!.Id == ZvtCommandIds.Authorization
                        ? [ZvtResponses.StatusInfo(ZvtResponses.TransactionStatus(ZvtResultCode.Ok, ZvtCodec.ExtractAmount(command.Data.Span) ?? 0, [0x09, 0x78])), ZvtResponses.Completion()]
                        : [ZvtResponses.Completion()];
                    foreach (byte[] response in responses) await stream.WriteAsync(TcpFrameDecoder.Frame(response, transport), stop.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
        return 0;
    }

    private static int ListPorts() { string[] ports = SerialPort.GetPortNames().Order().ToArray(); Console.WriteLine(ports.Length == 0 ? "Keine COM-Ports gefunden." : string.Join(Environment.NewLine, ports)); return 0; }
    private static int DetectCom0Com()
    {
        string[] candidates = [@"C:\Program Files\com0com\setupc.exe", @"C:\Program Files (x86)\com0com\setupc.exe"];
        string? found = candidates.FirstOrDefault(File.Exists); Console.WriteLine(found is null ? "com0com nicht gefunden. Anleitung: https://sourceforge.net/projects/com0com/" : "com0com gefunden: " + found); return found is null ? 1 : 0;
    }

    private static async Task<int> BeginSecureUpdateAsync(string[] args)
    {
        RequireConfirmation(args, "--confirm-update",
            "Update nicht gestartet. Für die bewusste Installation zusätzlich --confirm-update angeben.");
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Die sichere Updateinstallation benötigt Administratorrechte.");

        string target = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string expectedTools = Path.Combine(target, SecureReleaseUpdateService.ToolsExecutableName);
        string currentProcess = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Aktueller EXE-Pfad ist nicht verfügbar."));
        if (!currentProcess.Equals(expectedTools, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update muss aus ZVT2SumUp.Tools.exe im unveränderten Zwei-EXE-Paket gestartet werden.");
        EnsureExactInstalledPackage(target);

        if (int.TryParse(Value(args, "--wait-pid"), out int guiProcessId) && guiProcessId > 0)
            await WaitForProcessExitAsync(guiProcessId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        bool restartService = IsServiceRunning();
        bool serviceStopped = false;
        try
        {
            if (restartService)
            {
                StopService();
                serviceStopped = true;
            }

            using IniOptionsStore optionsStore = new(AppPaths.Configuration);
            GatewayOptions options = await optionsStore.LoadAsync().ConfigureAwait(false);
            using HttpClient updateHttp = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(20) };
            updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("ZVT2SumUp-Updater/1.0");
            updateHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            SecureReleaseUpdateService updateService = new(updateHttp, options, NullLogger<SecureReleaseUpdateService>.Instance);
            UpdateInformation information = await updateService.CheckAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(information.Error)) throw new InvalidOperationException(information.Error);
            if (!information.Available || information.RemoteVersion is null) throw new InvalidOperationException("Kein neueres stabiles GitHub-Release verfügbar.");
            string? expectedVersionText = Value(args, "--expected-version");
            if (expectedVersionText is not null &&
                (!Version.TryParse(expectedVersionText, out Version? expectedVersion) || NormalizeVersion(expectedVersion) != NormalizeVersion(information.RemoteVersion)))
                throw new InvalidDataException("Das inzwischen angebotene GitHub-Release entspricht nicht mehr der bestätigten Version.");

            Console.WriteLine($"Lade ZVT2SumUp {NormalizeVersion(information.RemoteVersion)} ausschließlich von GitHub ...");
            PreparedUpdate prepared = await updateService.PrepareAsync(information).ConfigureAwait(false);
            await VerifyNewPayloadAsync(prepared).ConfigureAwait(false);

            string runner = Path.Combine(prepared.StagingDirectory, "ZVT2SumUp.UpdateRunner.exe");
            File.Copy(currentProcess, runner, overwrite: false);
            if (!FilesHaveEqualSha256(currentProcess, runner))
                throw new CryptographicException("Der externe Updateprozess stimmt nicht mit der geprüften Tools-EXE überein.");
            UpdateApplyPlan plan = new(target, prepared.PayloadDirectory, prepared.StagingDirectory,
                NormalizeVersion(prepared.Version), prepared.GatewaySha256, prepared.ToolsSha256,
                Environment.ProcessId, restartService, RestartGui: true);
            string planPath = Path.Combine(prepared.StagingDirectory, "apply-plan.json");
            await SecureTwoFileUpdateInstaller.WritePlanAsync(planPath, plan).ConfigureAwait(false);

            ProcessStartInfo helperStart = new(runner)
            {
                UseShellExecute = false,
                WorkingDirectory = prepared.StagingDirectory
            };
            helperStart.ArgumentList.Add("apply-update");
            helperStart.ArgumentList.Add("--plan");
            helperStart.ArgumentList.Add(planPath);
            helperStart.ArgumentList.Add("--confirm-update");
            using Process helper = Process.Start(helperStart) ?? throw new InvalidOperationException("Externer Updateprozess konnte nicht gestartet werden.");
            AppendUpdateLog($"Update {plan.Version} geprüft; Austauschprozess {helper.Id} gestartet.");
            Console.WriteLine("Download und SHA-256-Prüfung erfolgreich. Die Anwendung wird jetzt atomar aktualisiert.");
            return 0;
        }
        catch
        {
            if (serviceStopped)
            {
                try { StartService(); } catch (Exception restartError) { AppendUpdateLog("Dienst konnte nach Updatefehler nicht neu gestartet werden: " + restartError.Message); }
            }
            throw;
        }
    }

    private static async Task<int> VerifyReleaseAsync()
    {
        string verificationRoot = Path.Combine(Path.GetTempPath(), "ZVT2SumUp", "release-verification", Guid.NewGuid().ToString("N"));
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        GatewayOptions options = await optionsStore.LoadAsync().ConfigureAwait(false);
        using HttpClient updateHttp = new()
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromMinutes(20)
        };
        updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("ZVT2SumUp-Updater/1.0");
        updateHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        SecureReleaseUpdateService updateService = new(
            updateHttp, options, NullLogger<SecureReleaseUpdateService>.Instance,
            verificationRoot, hardenStaging: false);
        UpdateInformation information = await updateService.CheckAsync().ConfigureAwait(false);
        if (!string.IsNullOrEmpty(information.Error)) throw new InvalidOperationException(information.Error);
        if (information.RemoteVersion is null || information.PackageUrl is null || information.ChecksumsUrl is null)
            throw new InvalidOperationException("Kein vollständig prüfbares stabiles GitHub-Release verfügbar.");

        PreparedUpdate? prepared = null;
        try
        {
            prepared = await updateService.PrepareAsync(information with { Available = true }).ConfigureAwait(false);
            await VerifyNewPayloadAsync(prepared).ConfigureAwait(false);
            Console.WriteLine($"Release {NormalizeVersion(prepared.Version)} erfolgreich von GitHub geladen, " +
                $"per SHA-256 geprüft und mit beiden EXE-Smoke-Tests bestätigt.");
            Console.WriteLine($"Paket-SHA-256: {prepared.PackageSha256.ToLowerInvariant()}");
            return 0;
        }
        finally
        {
            if (prepared is not null)
            {
                string staging = EnsurePathInsideRoot(prepared.StagingDirectory, verificationRoot, requireDirectory: true);
                Directory.Delete(staging, recursive: true);
            }
            if (Directory.Exists(verificationRoot) && !Directory.EnumerateFileSystemEntries(verificationRoot).Any())
                Directory.Delete(verificationRoot);
        }
    }

    private static async Task<int> ApplySecureUpdateAsync(string[] args)
    {
        RequireConfirmation(args, "--confirm-update", "Interner Updateprozess wurde ohne Bestätigung abgelehnt.");
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Der Update-Austausch benötigt Administratorrechte.");
        string planPath = Value(args, "--plan") ?? throw new ArgumentException("Updateplan fehlt.");
        EnsurePathInsideUpdates(planPath, requireDirectory: false);
        UpdateApplyPlan plan = await SecureTwoFileUpdateInstaller.ReadPlanAsync(planPath).ConfigureAwait(false);
        EnsurePathInsideUpdates(plan.StagingDirectory, requireDirectory: true);
        EnsurePathInsideUpdates(plan.PayloadDirectory, requireDirectory: true);
        ValidateUpdateRunner(planPath, plan);
        if (plan.WaitProcessId > 0) await WaitForProcessExitAsync(plan.WaitProcessId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        SecureTwoFileUpdateInstaller installer = new();
        using UpdateTransaction transaction = installer.Apply(plan);
        try
        {
            if (plan.RestartService) StartService();
            transaction.Commit();
            AppendUpdateLog($"Update {plan.Version} erfolgreich installiert.");
        }
        catch
        {
            transaction.Rollback();
            if (plan.RestartService)
            {
                try { StartService(); } catch (Exception restartError) { AppendUpdateLog("Vorheriger Dienst konnte nach Rollback nicht neu gestartet werden: " + restartError.Message); }
            }
            AppendUpdateLog($"Update {plan.Version} zurückgerollt.");
            throw;
        }

        if (plan.RestartGui)
        {
            try { LaunchUpdatedGui(plan.TargetDirectory); }
            catch (Exception exception) { AppendUpdateLog("Update installiert; Oberfläche konnte nicht automatisch neu gestartet werden: " + exception.Message); }
        }
        try { StartCleanupProcess(plan.TargetDirectory, plan.StagingDirectory, Environment.ProcessId); }
        catch (Exception exception) { AppendUpdateLog("Update installiert; Staging-Cleanup wird beim nächsten Lauf erneut versucht: " + exception.Message); }
        return 0;
    }

    private static async Task<int> CleanupSecureUpdateAsync(string[] args)
    {
        RequireConfirmation(args, "--confirm-update", "Interner Cleanup wurde ohne Bestätigung abgelehnt.");
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Update-Cleanup benötigt Administratorrechte.");
        string directory = Value(args, "--directory") ?? throw new ArgumentException("Cleanup-Ordner fehlt.");
        string full = EnsurePathInsideUpdates(directory, requireDirectory: true);
        if (int.TryParse(Value(args, "--wait-pid"), out int processId) && processId > 0)
            await WaitForProcessExitAsync(processId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        Directory.Delete(full, recursive: true);
        AppendUpdateLog("Update-Staging sicher entfernt.");
        return 0;
    }

    private static async Task VerifyNewPayloadAsync(PreparedUpdate prepared)
    {
        string gateway = Path.Combine(prepared.PayloadDirectory, SecureReleaseUpdateService.GatewayExecutableName);
        string tools = Path.Combine(prepared.PayloadDirectory, SecureReleaseUpdateService.ToolsExecutableName);
        await RunCheckedProcessAsync(gateway, ["--smoke-test"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await RunCheckedProcessAsync(gateway, ["--service-smoke-test"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await RunCheckedProcessAsync(tools, ["help"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private static async Task RunCheckedProcessAsync(string executable, string[] arguments, TimeSpan timeout)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Update-Smoke-Test konnte nicht gestartet werden.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeoutSource = new(timeout);
        try { await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new System.TimeoutException("Update-Smoke-Test hat das Zeitlimit überschritten.");
        }
        string combined = SensitiveDataRedactor.Redact(await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        if (process.ExitCode != 0) throw new InvalidDataException($"Update-Smoke-Test fehlgeschlagen ({process.ExitCode}): {combined}");
    }

    private static void EnsureExactInstalledPackage(string target)
    {
        string[] expected = [SecureReleaseUpdateService.ToolsExecutableName, SecureReleaseUpdateService.GatewayExecutableName];
        FileInfo[] files = new DirectoryInfo(target).GetFiles();
        if (files.Length != 2 || !files.Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Automatische Updates sind nur aus dem unveränderten Zwei-EXE-Paket zulässig.");
        if (files.Any(x => x.Attributes.HasFlag(FileAttributes.ReparsePoint))) throw new InvalidDataException("Installationspaket enthält einen Umleitungspunkt.");
    }

    private static string EnsurePathInsideUpdates(string path, bool requireDirectory)
        => EnsurePathInsideRoot(path, AppPaths.Updates, requireDirectory);

    private static string EnsurePathInsideRoot(string path, string allowedRoot, bool requireDirectory)
    {
        string root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Updatepfad liegt außerhalb des erlaubten Stagingordners.");
        UpdateDirectorySecurity.EnsureNoReparsePoints(full);
        FileAttributes attributes = File.GetAttributes(full);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Updatepfad ist ein Umleitungspunkt.");
        if (requireDirectory != attributes.HasFlag(FileAttributes.Directory)) throw new InvalidDataException("Updatepfad besitzt einen unerwarteten Typ.");
        return full;
    }

    private static void ValidateUpdateRunner(string planPath, UpdateApplyPlan plan)
    {
        string staging = Path.GetFullPath(plan.StagingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string expectedPlan = Path.Combine(staging, "apply-plan.json");
        if (!Path.GetFullPath(planPath).Equals(expectedPlan, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Updateplan gehört nicht zum bestätigten Stagingordner.");

        string currentProcess = Path.GetFullPath(Environment.ProcessPath ??
            throw new InvalidOperationException("Aktueller EXE-Pfad ist nicht verfügbar."));
        string expectedRunner = Path.Combine(staging, "ZVT2SumUp.UpdateRunner.exe");
        if (!currentProcess.Equals(expectedRunner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Interner Update-Austausch darf nur durch den geschützten Stagingprozess ausgeführt werden.");
        UpdateDirectorySecurity.EnsureNoReparsePoints(currentProcess);
        if (File.GetAttributes(currentProcess).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Updateprozess ist ein nicht erlaubter Umleitungspunkt.");

        string installedTools = Path.Combine(Path.GetFullPath(plan.TargetDirectory), SecureReleaseUpdateService.ToolsExecutableName);
        if (!File.Exists(installedTools) || !FilesHaveEqualSha256(currentProcess, installedTools))
            throw new CryptographicException("Updateprozess stimmt nicht mehr mit der installierten Tools-EXE überein.");
    }

    private static bool FilesHaveEqualSha256(string first, string second)
    {
        using FileStream firstInput = new(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream secondInput = new(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] firstHash = SHA256.HashData(firstInput);
        byte[] secondHash = SHA256.HashData(secondInput);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        if (processId == Environment.ProcessId) throw new InvalidOperationException("Updateprozess darf nicht auf sich selbst warten.");
        Process? process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        using (CancellationTokenSource timeoutSource = new(timeout))
        {
            try { await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new System.TimeoutException("Zu aktualisierender Prozess wurde nicht rechtzeitig beendet."); }
        }
    }

    private static bool IsServiceRunning()
    {
        using ServiceController? service = TryOpenService();
        if (service is null) return false;
        service.Refresh();
        return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.Paused;
    }

    private static void StopService()
    {
        using ServiceController service = TryOpenService() ?? throw new InvalidOperationException("Installierter Dienst konnte nicht geöffnet werden.");
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }

    private static void StartService()
    {
        using ServiceController service = TryOpenService() ?? throw new InvalidOperationException("Installierter Dienst konnte nicht geöffnet werden.");
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        if (service.Status != ServiceControllerStatus.StartPending) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
    }

    private static ServiceController? TryOpenService()
    {
        ServiceController service = new(ServiceName);
        try { _ = service.Status; return service; }
        catch (InvalidOperationException) { service.Dispose(); return null; }
    }

    private static void LaunchUpdatedGui(string targetDirectory)
    {
        string gateway = Path.Combine(targetDirectory, SecureReleaseUpdateService.GatewayExecutableName);
        ProcessStartInfo start = new("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(gateway);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Aktualisierte Oberfläche konnte nicht gestartet werden.");
    }

    private static void StartCleanupProcess(string targetDirectory, string stagingDirectory, int waitProcessId)
    {
        string tools = Path.Combine(targetDirectory, SecureReleaseUpdateService.ToolsExecutableName);
        ProcessStartInfo start = new(tools) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = targetDirectory };
        foreach (string argument in new[] { "cleanup-update", "--directory", stagingDirectory, "--wait-pid", waitProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--confirm-update" })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Update-Cleanup konnte nicht gestartet werden.");
    }

    private static string NormalizeVersion(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    private static void AppendUpdateLog(string message)
    {
        Directory.CreateDirectory(AppPaths.Updates);
        string line = $"{DateTimeOffset.Now:O} {SensitiveDataRedactor.Redact(message)}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(AppPaths.Updates, "update.log"), line);
    }

    private static async Task<int> InstallServiceAsync(string? explicitPath)
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Dienstinstallation benötigt eine als Administrator gestartete Konsole.");
        string path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "ZVT2SumUpGateway.exe"); if (!File.Exists(path)) throw new FileNotFoundException("Dienst-EXE nicht gefunden.", path);
        int code = await ScAsync("create", ServiceName, "binPath=", $"\"{path}\" --service", "start=", "auto", "DisplayName=", "ZVT-zu-SumUp Gateway"); if (code != 0) return code;
        await ScAsync("description", ServiceName, "Sicheres lokales Gateway zwischen ZVT-Kassensystem und SumUp Cloud API.");
        await ScAsync("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        return await ScAsync("failureflag", ServiceName, "1");
    }
    private static async Task<int> RestartServiceAsync() { await ScAsync("stop", ServiceName); return await ScAsync("start", ServiceName); }
    private static Task<int> ScAsync(params string[] arguments) => RunAsync("sc.exe", arguments);
    private static async Task<int> RunAsync(string file, params string[] arguments)
    {
        ProcessStartInfo info = new(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Prozessstart fehlgeschlagen."); string stdout = await process.StandardOutput.ReadToEndAsync(); string stderr = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Console.Write(SensitiveDataRedactor.Redact(stdout + stderr)); return process.ExitCode;
    }
    private static string? Value(string[] args, string name) { int index = Array.FindIndex(args, x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static void RequireConfirmation(string[] args, string confirmation, string error)
    {
        if (!args.Contains(confirmation, StringComparer.Ordinal)) throw new InvalidOperationException(error);
    }
    private static bool IsAdministrator() => new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

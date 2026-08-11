using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;
using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Infrastructure;

public sealed partial class SerialZvtServer : IGatewayTransport
{
    private readonly GatewayOptions options; private readonly ZvtGatewayHandler handler; private readonly ILogger<SerialZvtServer> logger;
#pragma warning disable CA2213 // StopAsync entsorgt die atomar ausgetauschte CancellationTokenSource.
    private SerialPort? port; private CancellationTokenSource? runSource; private Task? loop;
#pragma warning restore CA2213
    public SerialZvtServer(GatewayOptions options, ZvtGatewayHandler handler, ILogger<SerialZvtServer> logger)
    { this.options = options; this.handler = handler; this.logger = logger; }
    public bool IsRunning => runSource is { IsCancellationRequested: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;
        port = new SerialPort(options.ComPort, options.ComBaudRate, Parity.None, 8, StopBits.One)
        { Handshake = Handshake.None, ReadTimeout = 1000, WriteTimeout = 5000, DtrEnable = true, RtsEnable = false };
        port.Open(); runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); loop = LoopAsync(runSource.Token);
        LogSerialStarted(logger, options.ComPort, options.ComBaudRate); return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref runSource, null); if (source is null) return;
        await source.CancelAsync().ConfigureAwait(false); try { port?.Close(); } catch (InvalidOperationException) { }
        if (loop is not null) try { await loop.WaitAsync(cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
        port?.Dispose(); port = null; loop = null; source.Dispose(); LogSerialStopped(logger);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        SerialFrameDecoder decoder = new(); byte[] buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try { read = await port!.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            if (read <= 0) continue;
            foreach (byte[] frame in decoder.Push(buffer.AsSpan(0, read)))
            {
                if (!SerialFraming.TryParse(frame, out byte[]? apdu) || !ZvtCodec.TryParseApdu(apdu!, out ZvtCommand? command, out int consumed) || consumed != apdu!.Length) continue;
                if (command!.IsAcknowledgement) continue;
                if (!ZvtGatewayHandler.Supports(command.Id)) { await WriteFrameAsync(ZvtResponses.NegativeAck(), cancellationToken).ConfigureAwait(false); continue; }
                await port.BaseStream.WriteAsync(SerialFraming.SerialAck(), cancellationToken).ConfigureAwait(false);
                foreach (byte[] response in await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false)) await WriteFrameAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }
    private async Task WriteFrameAsync(byte[] apdu, CancellationToken cancellationToken)
    { byte[] frame = SerialFraming.Frame(apdu); await port!.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false); await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false); }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        runSource?.Dispose();
        port?.Dispose();
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "ZVT-COM-Server geöffnet: {Port} mit {BaudRate} Baud")]
    private static partial void LogSerialStarted(ILogger logger, string port, int baudRate);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "ZVT-COM-Server gestoppt")]
    private static partial void LogSerialStopped(ILogger logger);
}

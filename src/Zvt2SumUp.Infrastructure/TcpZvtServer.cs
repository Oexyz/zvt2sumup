using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Zvt2SumUp.Core;
using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Infrastructure;

public sealed partial class TcpZvtServer : IGatewayTransport
{
    private readonly GatewayOptions options;
    private readonly ZvtGatewayHandler handler;
    private readonly ILogger<TcpZvtServer> logger;
    private readonly ConcurrentDictionary<long, Task> clients = new();
#pragma warning disable CA2213 // StopAsync entsorgt die atomar ausgetauschte CancellationTokenSource.
    private CancellationTokenSource? runSource;
#pragma warning restore CA2213
    private TcpListener? listener;
    private Task? acceptTask;
    private long clientId;

    public TcpZvtServer(GatewayOptions options, ZvtGatewayHandler handler, ILogger<TcpZvtServer> logger)
    { this.options = options; this.handler = handler; this.logger = logger; }
    public bool IsRunning => runSource is { IsCancellationRequested: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;
        if (!IPAddress.TryParse(options.TcpHost, out IPAddress? address)) throw new InvalidOperationException("Ungültige TCP-Bind-Adresse.");
        runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listener = new TcpListener(address, options.TcpPort); listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start(32); acceptTask = AcceptLoopAsync(runSource.Token);
        LogTcpStarted(logger, options.TcpHost, options.TcpPort); return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref runSource, null); if (source is null) return;
        await source.CancelAsync().ConfigureAwait(false); listener?.Stop();
        if (acceptTask is not null) await IgnoreCancellation(acceptTask).WaitAsync(cancellationToken).ConfigureAwait(false);
        Task[] active = clients.Values.ToArray(); if (active.Length > 0) await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
        source.Dispose(); listener = null; acceptTask = null; LogTcpStopped(logger);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true; client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                long id = Interlocked.Increment(ref clientId); Task task = HandleClientAsync(client, cancellationToken);
                clients[id] = task; _ = task.ContinueWith(completed => { _ = completed.Exception; clients.TryRemove(id, out Task? _); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
#pragma warning disable CA1031 // Die Servergrenze muss unerwartete Clientfehler isolieren und weiter lauschen.
            catch (Exception exception) { LogAcceptFailure(logger, exception); await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
#pragma warning restore CA1031
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unbekannt"; LogClientConnected(logger, endpoint);
        using (client)
        {
            NetworkStream stream = client.GetStream(); TcpFrameDecoder decoder = new(); byte[] buffer = new byte[8192];
            using CancellationTokenSource clientSource = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            using SemaphoreSlim writeGate = new(1, 1); ConcurrentDictionary<long, Task> operations = new(); long operationId = 0;
            try
            {
                while (!clientSource.IsCancellationRequested)
                {
                    int count = await ReadWithIdleTimeoutAsync(stream, buffer, clientSource.Token).ConfigureAwait(false); if (count == 0) break;
                    foreach ((byte[] apdu, TcpTransport transport) in decoder.Push(buffer.AsSpan(0, count)))
                        await ProcessAsync(stream, apdu, transport, writeGate, operations, () => Interlocked.Increment(ref operationId), clientSource.Token).ConfigureAwait(false);
                }
            }
            catch (TimeoutException) { LogClientIdle(logger, endpoint); }
            catch (OperationCanceledException) when (clientSource.IsCancellationRequested) { }
            catch (IOException exception) { LogClientEnded(logger, exception, endpoint); }
#pragma warning disable CA1031 // Ein fehlerhafter Client darf weder Listener noch andere Kassensysteme beenden.
            catch (Exception exception) { LogClientFailure(logger, exception, endpoint); }
#pragma warning restore CA1031
            finally
            {
                await clientSource.CancelAsync().ConfigureAwait(false); Task[] pending = operations.Values.ToArray();
                if (pending.Length > 0) try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
            }
        }
        LogClientDisconnected(logger, endpoint);
    }

    private async Task ProcessAsync(NetworkStream stream, byte[] apdu, TcpTransport transport, SemaphoreSlim writeGate,
        ConcurrentDictionary<long, Task> operations, Func<long> nextOperationId, CancellationToken cancellationToken)
    {
#pragma warning disable CA1873 // Die Hex-Konvertierung ist explizit durch IsEnabled geschützt.
        if (logger.IsEnabled(LogLevel.Debug)) LogReceived(logger, transport, Convert.ToHexString(apdu));
#pragma warning restore CA1873
        if (!ZvtCodec.TryParseApdu(apdu, out ZvtCommand? command, out int consumed) || consumed != apdu.Length)
        { await SendAsync(stream, ZvtResponses.NegativeAck(ZvtResultCode.ProtocolError), transport, writeGate, cancellationToken).ConfigureAwait(false); return; }
        if (command!.IsAcknowledgement) return;
        if (!ZvtGatewayHandler.Supports(command.Id))
        { await SendAsync(stream, ZvtResponses.NegativeAck(), transport, writeGate, cancellationToken).ConfigureAwait(false); return; }
        await SendAsync(stream, ZvtResponses.Ack(), transport, writeGate, cancellationToken).ConfigureAwait(false);
        if (command.Id == ZvtCommandIds.Authorization)
        {
            long id = nextOperationId(); Task operation = HandleAndSendAsync(stream, command, transport, writeGate, cancellationToken);
            operations[id] = operation; _ = operation.ContinueWith(completed => { _ = completed.Exception; operations.TryRemove(id, out Task? _); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); return;
        }
        await HandleAndSendAsync(stream, command, transport, writeGate, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAndSendAsync(NetworkStream stream, ZvtCommand command, TcpTransport transport,
        SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        IReadOnlyList<byte[]> responses = await handler.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        foreach (byte[] original in responses)
        {
            byte[] response = original;
            if (transport == TcpTransport.RawApdu && response.Length >= 2)
            {
                ushort id = (ushort)((response[0] << 8) | response[1]);
                if (id is 0x04FF or 0x06D1 or 0x06D3) continue;
                if (id == ZvtCommandIds.Completion) response = ZvtResponses.Completion();
            }
            await SendAsync(stream, response, transport, writeGate, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> ReadWithIdleTimeoutAsync(NetworkStream stream, byte[] buffer, CancellationToken serverToken)
    {
        if (options.TcpIdleTimeoutSeconds <= 0) return await stream.ReadAsync(buffer, serverToken).ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TcpIdleTimeoutSeconds));
        try { return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!serverToken.IsCancellationRequested) { throw new TimeoutException(); }
    }

    private async Task SendAsync(NetworkStream stream, byte[] apdu, TcpTransport transport, SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] message = TcpFrameDecoder.Frame(apdu, transport); await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1873 // Die Hex-Konvertierung ist explizit durch IsEnabled geschützt.
            if (logger.IsEnabled(LogLevel.Debug)) LogSent(logger, transport, Convert.ToHexString(apdu));
#pragma warning restore CA1873
        }
        finally { writeGate.Release(); }
    }

    private static async Task IgnoreCancellation(Task task) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (SocketException) { } }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        runSource?.Dispose();
        listener?.Dispose();
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "ZVT-TCP-Server lauscht auf {Host}:{Port}")]
    private static partial void LogTcpStarted(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "ZVT-TCP-Server gestoppt")]
    private static partial void LogTcpStopped(ILogger logger);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "Fehler beim Annehmen einer ZVT-Verbindung")]
    private static partial void LogAcceptFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Information, Message = "Kassensystem verbunden: {Endpoint}")]
    private static partial void LogClientConnected(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Information, Message = "Inaktive Verbindung geschlossen: {Endpoint}")]
    private static partial void LogClientIdle(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Debug, Message = "ZVT-Verbindung beendet: {Endpoint}")]
    private static partial void LogClientEnded(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Warning, Message = "Fehler in ZVT-Verbindung {Endpoint}")]
    private static partial void LogClientFailure(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(EventId = 2108, Level = LogLevel.Information, Message = "Kassensystem getrennt: {Endpoint}")]
    private static partial void LogClientDisconnected(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2109, Level = LogLevel.Debug, Message = "ZVT RX {Transport}: {Hex}")]
    private static partial void LogReceived(ILogger logger, TcpTransport transport, string hex);

    [LoggerMessage(EventId = 2110, Level = LogLevel.Debug, Message = "ZVT TX {Transport}: {Hex}")]
    private static partial void LogSent(ILogger logger, TcpTransport transport, string hex);
}

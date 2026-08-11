using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;
using Zvt2SumUp.Protocol;

namespace Zvt2SumUp.Tests;

public sealed class GatewayTests
{
    [Fact]
    public async Task RegistrationPaymentRefundAndReconciliationMapCorrectly()
    {
        FakeSumUp sumUp = new(); MemoryJournal journal = new(); ZvtGatewayHandler handler = Handler(sumUp, journal);
        IReadOnlyList<byte[]> registration = await handler.HandleAsync(Command(0x06, 0x00, [0, 0, 0, 0, 0x09, 0x78]), CancellationToken.None);
        Assert.Equal(0x04FF, Id(registration[0])); Assert.Equal(ZvtCommandIds.Completion, Id(registration[1]));
        IReadOnlyList<byte[]> payment = await handler.HandleAsync(Command(0x06, 0x01, [0x04, .. ZvtCodec.IntToBcd(1234, 6), 0x49, 0x09, 0x78]), CancellationToken.None);
        Assert.Equal(0x040F, Id(payment[0])); Assert.Equal(ZvtCommandIds.Completion, Id(payment[^1])); Assert.Single(journal.Records); Assert.Equal(1234, journal.Records[0].AmountCents);
        IReadOnlyList<byte[]> refund = await handler.HandleAsync(Command(0x06, 0x31, [0x04, .. ZvtCodec.IntToBcd(234, 6)]), CancellationToken.None);
        Assert.Equal(ZvtCommandIds.Completion, Id(refund[^1])); Assert.Equal(-234, journal.Records[^1].AmountCents);
        IReadOnlyList<byte[]> partial = await handler.HandleAsync(Command(0x06, 0x52), CancellationToken.None); Assert.Equal(0, journal.CloseCalls); Assert.Equal(ZvtCommandIds.Completion, Id(partial[^1]));
        await handler.HandleAsync(Command(0x06, 0x50), CancellationToken.None); Assert.Equal(1, journal.CloseCalls);
    }

    [Theory]
    [InlineData("FAILED", 0xFF)]
    [InlineData("TIMEOUT", 0x6C)]
    public async Task PaymentFailureMapsToAbort(string status, int code)
    {
        FakeSumUp sumUp = new() { PaymentStatus = status }; ZvtGatewayHandler handler = Handler(sumUp, new MemoryJournal());
        IReadOnlyList<byte[]> result = await handler.HandleAsync(Command(0x06, 0x01, [0x04, .. ZvtCodec.IntToBcd(100, 6)]), CancellationToken.None);
        byte[] abort = result[^1]; Assert.Equal(ZvtCommandIds.AbortResponse, Id(abort)); Assert.Equal(code, abort[3]);
    }

    [Fact]
    public async Task ParallelPaymentsForSameTerminalAreSerialized()
    {
        FakeSumUp sumUp = new() { Delay = TimeSpan.FromMilliseconds(80) }; ZvtGatewayHandler handler = Handler(sumUp, new MemoryJournal());
        ZvtCommand payment = Command(0x06, 0x01, [0x04, .. ZvtCodec.IntToBcd(100, 6)]);
        await Task.WhenAll(handler.HandleAsync(payment, CancellationToken.None), handler.HandleAsync(payment, CancellationToken.None)); Assert.Equal(1, sumUp.MaximumConcurrentPayments);
    }

    [Fact]
    public async Task RawKoronaOrderIsAckStatusThenMinimalCompletionAndOptionalFramesAreSuppressed()
    {
        int port = FreePort(); GatewayOptions options = new() { TcpPort = port, TerminalId = "rdr_test" }; ZvtGatewayHandler handler = Handler(new FakeSumUp(), new MemoryJournal(), options);
        await using TcpZvtServer server = new(options, handler, NullLogger<TcpZvtServer>.Instance); await server.StartAsync(CancellationToken.None);
        using TcpClient client = new(); await client.ConnectAsync(IPAddress.Loopback, port); NetworkStream stream = client.GetStream();
        byte[] request = ZvtCodec.BuildApdu(0x06, 0x01, [0x04, .. ZvtCodec.IntToBcd(321, 6), 0x49, 0x09, 0x78]); await stream.WriteAsync(request);
        List<byte[]> responses = await ReadRawUntilCompletionAsync(stream);
        Assert.Equal([0x8000, 0x040F, 0x060F], responses.Select(Id)); Assert.Equal("060F00", Convert.ToHexString(responses[^1]));
        await server.StopAsync(CancellationToken.None); await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnsupportedCommandGetsOnly848300AndAckInputIsIgnored()
    {
        int port = FreePort(); GatewayOptions options = new() { TcpPort = port }; ZvtGatewayHandler handler = Handler(new FakeSumUp(), new MemoryJournal(), options);
        await using TcpZvtServer server = new(options, handler, NullLogger<TcpZvtServer>.Instance); await server.StartAsync(CancellationToken.None);
        using TcpClient client = new(); await client.ConnectAsync(IPAddress.Loopback, port); NetworkStream stream = client.GetStream();
        await stream.WriteAsync(ZvtCodec.BuildApdu(0x06, 0x03)); byte[] response = new byte[3]; await stream.ReadExactlyAsync(response); Assert.Equal("848300", Convert.ToHexString(response));
        await stream.WriteAsync(ZvtResponses.Ack()); using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { byte[] value = new byte[1]; await stream.ReadAsync(value, timeout.Token); });
    }

    [Fact]
    public async Task AbortOnSameConnectionCancelsActivePaymentAndTerminatesReader()
    {
        int port = FreePort(); GatewayOptions options = new() { TcpPort = port, TerminalId = "rdr_test" };
        FakeSumUp sumUp = new() { BlockUntilCancelled = true }; ZvtGatewayHandler handler = Handler(sumUp, new MemoryJournal(), options);
        await using TcpZvtServer server = new(options, handler, NullLogger<TcpZvtServer>.Instance); await server.StartAsync(CancellationToken.None);
        using TcpClient client = new(); await client.ConnectAsync(IPAddress.Loopback, port); NetworkStream stream = client.GetStream(); RawReader reader = new(stream);
        await stream.WriteAsync(ZvtCodec.BuildApdu(0x06, 0x01, [0x04, .. ZvtCodec.IntToBcd(100, 6)]));
        Assert.Equal(0x8000, Id(await reader.ReadOneAsync()));
        await stream.WriteAsync(ZvtCodec.BuildApdu(0x06, 0xB0));
        List<ushort> ids = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!(ids.Contains(0x8000) && ids.Contains(ZvtCommandIds.AbortResponse) && ids.Contains(ZvtCommandIds.Completion)))
            ids.Add(Id(await reader.ReadOneAsync(timeout.Token)));
        Assert.Equal(1, sumUp.TerminateCalls);
    }

    private static async Task<List<byte[]>> ReadRawUntilCompletionAsync(NetworkStream stream)
    {
        List<byte[]> result = []; TcpFrameDecoder decoder = new(); byte[] buffer = new byte[4096]; using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!timeout.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token); foreach ((byte[] apdu, _) in decoder.Push(buffer.AsSpan(0, read))) { result.Add(apdu); if (Id(apdu) == ZvtCommandIds.Completion) return result; }
        }
        return result;
    }
    private sealed class RawReader(NetworkStream stream)
    {
        private readonly TcpFrameDecoder decoder = new(); private readonly Queue<byte[]> pending = new();
        public async Task<byte[]> ReadOneAsync(CancellationToken cancellationToken = default)
        {
            if (pending.Count > 0) return pending.Dequeue(); byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken);
                foreach ((byte[] apdu, _) in decoder.Push(buffer.AsSpan(0, read))) pending.Enqueue(apdu);
                if (pending.Count > 0) return pending.Dequeue();
            }
        }
    }
    private static int FreePort() { TcpListener listener = new(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static ushort Id(byte[] apdu) => (ushort)((apdu[0] << 8) | apdu[1]);
    private static ZvtCommand Command(byte c, byte i, byte[]? data = null) => new(c, i, data ?? []);
    private static ZvtGatewayHandler Handler(FakeSumUp sumUp, MemoryJournal journal, GatewayOptions? options = null) =>
        new(sumUp, journal, new FakeReceipts(), options ?? new GatewayOptions { TerminalId = "rdr_test" }, NullLogger<ZvtGatewayHandler>.Instance);

    private sealed class FakeSumUp : ISumUpClient
    {
        private int active; public int MaximumConcurrentPayments { get; private set; }
        public string PaymentStatus { get; init; } = "PAID"; public TimeSpan Delay { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public int TerminateCalls { get; private set; }
        public string MerchantCode => "M1"; public string TerminalId { get; set; } = "rdr_test";
        public Task<ConnectionResult> TestConnectionAsync(CancellationToken cancellationToken) => Task.FromResult(new ConnectionResult(true, "M1", "Test"));
        public Task<IReadOnlyList<TerminalDescriptor>> GetTerminalsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TerminalDescriptor>>([]);
        public Task<TerminalDescriptor> PairReaderAsync(string pairingCode, string name, CancellationToken cancellationToken) => Task.FromResult(new TerminalDescriptor("rdr_test", name, "paired"));
        public Task<CheckoutResult> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken) => Task.FromResult(new CheckoutResult(Guid.NewGuid().ToString(), "PENDING"));
        public async Task<CheckoutResult> WaitForPaymentAsync(string checkoutId, TimeSpan timeout, CancellationToken cancellationToken)
        { int now = Interlocked.Increment(ref active); MaximumConcurrentPayments = Math.Max(MaximumConcurrentPayments, now); try { if (BlockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken); return new(checkoutId, PaymentStatus, "tx-1", CardType: "VISA", AuthorizationCode: "A1"); } finally { Interlocked.Decrement(ref active); } }
        public Task TerminateCheckoutAsync(CancellationToken cancellationToken) { TerminateCalls++; return Task.CompletedTask; }
        public Task<CheckoutResult> RefundAsync(string transactionId, long? amountCents, CancellationToken cancellationToken) => Task.FromResult(new CheckoutResult("refund", "REFUNDED", transactionId));
        public Task<IReadOnlyList<CheckoutResult>> GetTransactionsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CheckoutResult>>([]);
    }
    private sealed class MemoryJournal : ITransactionJournal
    {
        private readonly object gate = new(); public List<TransactionRecord> Records { get; } = []; public int CloseCalls { get; private set; }
        public Task AddPaymentAsync(TransactionRecord record, CancellationToken cancellationToken = default) { lock (gate) Records.Add(record); return Task.CompletedTask; }
        public Task AddRefundAsync(TransactionRecord record, CancellationToken cancellationToken = default) { lock (gate) Records.Add(record with { AmountCents = -Math.Abs(record.AmountCents), Type = "REFUND" }); return Task.CompletedTask; }
        public Task<IReadOnlyList<TransactionRecord>> GetOpenAsync(string? terminalId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TransactionRecord>>(Records.Where(x => !x.Closed).ToArray());
        public Task<int> CloseOpenAsync(string? terminalId = null, CancellationToken cancellationToken = default) { CloseCalls++; return Task.FromResult(Records.Count); }
    }
    private sealed class FakeReceipts : IReceiptRenderer
    {
        private int number; public Task<string> NextReceiptNumberAsync(CancellationToken cancellationToken = default) => Task.FromResult("R-" + Interlocked.Increment(ref number));
        public IReadOnlyList<string> Render(string section, IReadOnlyDictionary<string, object?> context) => ["Beleg", context.GetValueOrDefault("amount")?.ToString() ?? ""];
        public string RenderValue(string section, string optionName, IReadOnlyDictionary<string, object?> context, string fallback, int maximumLength) => "Test";
    }
}

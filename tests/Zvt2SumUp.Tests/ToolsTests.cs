using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Zvt2SumUp.Tests;

public sealed class ToolsTests
{
    [Fact]
    public async Task CashRegisterSimulatorConsumesStatusCompletionBeforeFollowingCommand()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<(byte[] Status, byte[] StatusAck, byte[] CompletionAck, byte[] Following, byte[] FollowingAck)> server =
            ServeStatusThenRegistrationAsync(listener, timeout.Token);

        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tools = Path.Combine(repositoryRoot, "src", "Zvt2SumUp.Tools", "bin", configuration,
            "net10.0-windows", "win-x64", "ZVT2SumUp.Tools.exe");
        Assert.True(File.Exists(tools), $"Gebauter Kassensimulator fehlt: {tools}");

        ProcessStartInfo start = new(tools)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "cash-register-simulator", "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Kassensimulator konnte nicht gestartet werden.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("7");
            await process.StandardInput.WriteLineAsync("2");
            await process.StandardInput.WriteLineAsync("0");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        (byte[] status, byte[] statusAck, byte[] completionAck, byte[] following, byte[] followingAck) = await server;
        string output = await standardOutput + await standardError;
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(output, "RX Completion"));
        Assert.Equal([0x05, 0x01], status[..2]);
        Assert.Equal([0x80, 0x00], statusAck[..2]);
        Assert.Equal([0x80, 0x00], completionAck[..2]);
        Assert.Equal([0x06, 0x00], following[..2]);
        Assert.Equal([0x80, 0x00], followingAck[..2]);
    }

    private static async Task<(byte[], byte[], byte[], byte[], byte[])> ServeStatusThenRegistrationAsync(
        TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            NetworkStream stream = client.GetStream();
            byte[] status = await ReadFrameAsync(stream, cancellationToken);
            await SendFrameAsync(stream, [0x80, 0x00, 0x00], cancellationToken);
            await SendFrameAsync(stream, [0x04, 0x0F, 0x02, 0x27, 0x00], cancellationToken);
            await SendFrameAsync(stream, [0x06, 0x0F, 0x00], cancellationToken);
            byte[] statusAck = await ReadFrameAsync(stream, cancellationToken);
            byte[] completionAck = await ReadFrameAsync(stream, cancellationToken);
            byte[] following = await ReadFrameAsync(stream, cancellationToken);
            await SendFrameAsync(stream, [0x80, 0x00, 0x00], cancellationToken);
            await SendFrameAsync(stream, [0x06, 0x0F, 0x00], cancellationToken);
            byte[] followingAck = await ReadFrameAsync(stream, cancellationToken);
            return (status, statusAck, completionAck, following, followingAck);
        }
        finally { listener.Stop(); }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken);
        byte[] payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task SendFrameAsync(Stream stream, byte[] apdu, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[apdu.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)apdu.Length);
        apdu.CopyTo(frame, 2);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

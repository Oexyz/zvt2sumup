using System.Buffers.Binary;

namespace Zvt2SumUp.Protocol;

public enum TcpTransport { LengthPrefixed, RawApdu }

public sealed class TcpFrameDecoder
{
    private readonly List<byte> buffer = [];
    public const int MaximumApduLength = ushort.MaxValue + 5;

    public IReadOnlyList<(byte[] Apdu, TcpTransport Transport)> Push(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) buffer.Add(value);
        List<(byte[], TcpTransport)> messages = [];
        while (TryReadOne(out byte[]? apdu, out TcpTransport transport)) messages.Add((apdu!, transport));
        if (buffer.Count > MaximumApduLength + 2) throw new InvalidDataException("TCP-Puffer überschreitet die zulässige ZVT-Größe.");
        return messages;
    }

    private bool TryReadOne(out byte[]? apdu, out TcpTransport transport)
    {
        apdu = null; transport = TcpTransport.LengthPrefixed;
        if (buffer.Count < 2) return false;
        if (LooksLikeApduClass(buffer[0]))
        {
            transport = TcpTransport.RawApdu;
            if (buffer.Count < 3) return false;
            int lengthBytes = buffer[2] == 0xFF ? 3 : 1;
            if (buffer.Count < 2 + lengthBytes) return false;
            int dataLength = lengthBytes == 1 ? buffer[2] : (buffer[3] << 8) | buffer[4];
            int total = 2 + lengthBytes + dataLength;
            if (buffer.Count < total) return false;
            apdu = buffer.GetRange(0, total).ToArray(); buffer.RemoveRange(0, total); return true;
        }
        int messageLength = (buffer[0] << 8) | buffer[1];
        if (messageLength is < 3 or > MaximumApduLength) throw new InvalidDataException("Ungültige TCP-ZVT-Länge.");
        if (buffer.Count < messageLength + 2) return false;
        apdu = buffer.GetRange(2, messageLength).ToArray(); buffer.RemoveRange(0, messageLength + 2); return true;
    }

    public static bool LooksLikeApduClass(byte value) => value is 0x04 or 0x05 or 0x06 or 0x08 or 0x0F or 0x80 or 0x84;

    public static byte[] Frame(ReadOnlySpan<byte> apdu, TcpTransport transport)
    {
        if (transport == TcpTransport.RawApdu) return apdu.ToArray();
        if (apdu.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(apdu));
        byte[] result = new byte[apdu.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)apdu.Length); apdu.CopyTo(result.AsSpan(2)); return result;
    }
}
public static class SerialFraming
{
    public const byte Dle = 0x10, Stx = 0x02, Etx = 0x03, Ack = 0x06, Nak = 0x15;

    public static byte[] Frame(ReadOnlySpan<byte> apdu)
    {
        List<byte> result = [Dle, Stx]; byte lrc = Etx;
        foreach (byte value in apdu) { result.Add(value); if (value == Dle) result.Add(Dle); lrc ^= value; }
        result.Add(Dle); result.Add(Etx); result.Add(lrc); return [.. result];
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out byte[]? apdu)
    {
        apdu = null; if (frame.Length < 6 || frame[0] != Dle || frame[1] != Stx) return false;
        List<byte> data = []; int index = 2;
        while (index < frame.Length - 2)
        {
            byte value = frame[index++];
            if (value != Dle) { data.Add(value); continue; }
            if (index >= frame.Length) return false;
            byte escaped = frame[index++];
            if (escaped == Dle) { data.Add(Dle); continue; }
            if (escaped != Etx || index != frame.Length - 1) return false;
            byte lrc = Etx; foreach (byte item in data) lrc ^= item;
            if (frame[index] != lrc) return false;
            apdu = [.. data]; return true;
        }
        return false;
    }

    public static byte[] SerialAck() => [Dle, Ack];
}

public sealed class SerialFrameDecoder
{
    private readonly List<byte> buffer = [];
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) buffer.Add(value);
        List<byte[]> frames = [];
        while (TryExtract(out byte[]? frame)) frames.Add(frame!);
        if (buffer.Count > 131_080) { buffer.Clear(); throw new InvalidDataException("Serieller ZVT-Pufferüberlauf."); }
        return frames;
    }

    private bool TryExtract(out byte[]? frame)
    {
        frame = null; int start = -1;
        for (int i = 0; i + 1 < buffer.Count; i++) if (buffer[i] == SerialFraming.Dle && buffer[i + 1] == SerialFraming.Stx) { start = i; break; }
        if (start < 0) { if (buffer.Count > 1) buffer.RemoveRange(0, buffer.Count - 1); return false; }
        if (start > 0) buffer.RemoveRange(0, start);
        for (int i = 2; i + 1 < buffer.Count; i++)
        {
            if (buffer[i] != SerialFraming.Dle) continue;
            if (buffer[i + 1] == SerialFraming.Dle) { i++; continue; }
            if (buffer[i + 1] != SerialFraming.Etx) continue;
            int end = i + 3; if (buffer.Count < end) return false;
            frame = buffer.GetRange(0, end).ToArray(); buffer.RemoveRange(0, end); return true;
        }
        return false;
    }
}

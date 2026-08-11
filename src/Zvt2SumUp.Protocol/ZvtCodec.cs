using System.Buffers.Binary;
using System.Text;

namespace Zvt2SumUp.Protocol;

public sealed record BmpField(byte Tag, ReadOnlyMemory<byte> Value, int Offset, bool Complete);

public static class ZvtCodec
{
    private enum BmpKind { Fixed, Llvar, Lllvar, Tlv }
    private sealed record BmpDefinition(BmpKind Kind, int Length = 0);

    private static readonly Dictionary<byte, BmpDefinition> Definitions = BuildDefinitions();

    static ZvtCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static byte[] EncodeLength(int length)
    {
        if (length < 0 || length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
        if (length < 0xFF) return [(byte)length];
        byte[] result = new byte[3];
        result[0] = 0xFF;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1), (ushort)length);
        return result;
    }

    public static bool TryDecodeLength(ReadOnlySpan<byte> input, out int length, out int bytesRead)
    {
        length = 0; bytesRead = 0;
        if (input.IsEmpty) return false;
        if (input[0] < 0xFF) { length = input[0]; bytesRead = 1; return true; }
        if (input.Length < 3) return false;
        length = BinaryPrimitives.ReadUInt16BigEndian(input[1..3]); bytesRead = 3; return true;
    }

    public static byte[] BuildApdu(byte commandClass, byte instruction, ReadOnlySpan<byte> data = default)
    {
        byte[] length = EncodeLength(data.Length);
        byte[] result = new byte[2 + length.Length + data.Length];
        result[0] = commandClass; result[1] = instruction;
        length.CopyTo(result, 2); data.CopyTo(result.AsSpan(2 + length.Length));
        return result;
    }

    public static bool TryParseApdu(ReadOnlySpan<byte> raw, out ZvtCommand? command, out int consumed)
    {
        command = null; consumed = 0;
        if (raw.Length < 3 || !TryDecodeLength(raw[2..], out int length, out int lengthBytes)) return false;
        int total = 2 + lengthBytes + length;
        if (raw.Length < total) return false;
        command = new ZvtCommand(raw[0], raw[1], raw.Slice(2 + lengthBytes, length).ToArray());
        consumed = total;
        return true;
    }

    public static long BcdToInt(ReadOnlySpan<byte> data, bool strict = true)
    {
        long result = 0;
        foreach (byte value in data)
        {
            int high = value >> 4, low = value & 0x0F;
            if (high > 9 || low > 9)
            {
                if (strict) throw new FormatException("Ungültige BCD-Ziffer.");
                high = high > 9 ? 0 : high; low = low > 9 ? 0 : low;
            }
            checked { result = result * 100 + high * 10 + low; }
        }
        return result;
    }

    public static byte[] IntToBcd(long value, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        byte[] result = new byte[byteCount];
        for (int index = byteCount - 1; index >= 0; index--)
        {
            result[index] = (byte)((value % 10) | (((value / 10) % 10) << 4));
            value /= 100;
        }
        if (value != 0) throw new OverflowException("Wert passt nicht in das BCD-Feld.");
        return result;
    }

    public static long? ExtractAmount(ReadOnlySpan<byte> data) =>
        GetBmpValue(data, 0x04) is { } amount ? BcdToInt(amount.Span) : null;

    public static ReadOnlyMemory<byte>? GetBmpValue(ReadOnlySpan<byte> data, byte wantedTag)
    {
        foreach (BmpField field in EnumerateBmpFields(data))
            if (field.Tag == wantedTag && field.Complete) return field.Value;
        return null;
    }

    public static IReadOnlyList<BmpField> EnumerateBmpFields(ReadOnlySpan<byte> data)
    {
        List<BmpField> fields = [];
        int index = 0;
        while (index < data.Length)
        {
            int offset = index; byte tag = data[index++];
            if (!Definitions.TryGetValue(tag, out BmpDefinition? definition))
            {
                fields.Add(new BmpField(tag, data[index..].ToArray(), offset, false)); break;
            }
            int length;
            switch (definition.Kind)
            {
                case BmpKind.Fixed: length = definition.Length; break;
                case BmpKind.Llvar:
                    if (index >= data.Length) { fields.Add(new(tag, ReadOnlyMemory<byte>.Empty, offset, false)); return fields; }
                    length = data[index++]; break;
                case BmpKind.Lllvar:
                    if (index >= data.Length) { fields.Add(new(tag, ReadOnlyMemory<byte>.Empty, offset, false)); return fields; }
                    length = DecodeLllvarLength(data, ref index); break;
                case BmpKind.Tlv:
                    if (!TryDecodeLength(data[index..], out length, out int size))
                    { fields.Add(new(tag, ReadOnlyMemory<byte>.Empty, offset, false)); return fields; }
                    index += size; break;
                default: throw new InvalidOperationException();
            }
            bool complete = length >= 0 && index + length <= data.Length;
            int available = Math.Max(0, Math.Min(length, data.Length - index));
            fields.Add(new BmpField(tag, data.Slice(index, available).ToArray(), offset, complete));
            index += available;
            if (!complete) break;
        }
        return fields;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<byte[]>> ParseBerTlv(ReadOnlySpan<byte> data)
    {
        Dictionary<string, List<byte[]>> values = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        while (index < data.Length)
        {
            int start = index++;
            if ((data[start] & 0x1F) == 0x1F)
            {
                while (index < data.Length && (data[index++] & 0x80) != 0) { }
            }
            if (index > data.Length) break;
            string tag = Convert.ToHexString(data[start..index]);
            if (!TryReadBerLength(data, ref index, out int length) || index + length > data.Length) break;
            if (!values.TryGetValue(tag, out List<byte[]>? list)) values[tag] = list = [];
            list.Add(data.Slice(index, length).ToArray()); index += length;
        }
        return values.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<byte[]>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static byte[] BerTlv(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> value)
    {
        if (tag.IsEmpty) throw new ArgumentException("BER-Tag fehlt.", nameof(tag));
        byte[] length = value.Length < 0x80 ? [(byte)value.Length] :
            value.Length <= byte.MaxValue ? [0x81, (byte)value.Length] :
            value.Length <= ushort.MaxValue ? [0x82, (byte)(value.Length >> 8), (byte)value.Length] :
            throw new ArgumentOutOfRangeException(nameof(value));
        byte[] result = new byte[tag.Length + length.Length + value.Length];
        tag.CopyTo(result); length.CopyTo(result, tag.Length); value.CopyTo(result.AsSpan(tag.Length + length.Length));
        return result;
    }

    public static byte[] EncodeText(string? text, int maximumBytes = int.MaxValue)
    {
        string safe = (text ?? string.Empty).Replace("€", "EUR", StringComparison.Ordinal);
        byte[] bytes = Encoding.GetEncoding(437, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback).GetBytes(safe);
        return bytes.Length <= maximumBytes ? bytes : bytes[..maximumBytes];
    }

    public static string DecodeText(ReadOnlySpan<byte> value) => Encoding.GetEncoding(437).GetString(value);

    private static int DecodeLllvarLength(ReadOnlySpan<byte> data, ref int index)
    {
        byte first = data[index];
        if (index + 1 < data.Length && first <= 0x09)
        {
            int candidate = (first >> 4) * 100 + (first & 0x0F) * 10 + (data[index + 1] >> 4);
            if (candidate > 0 && index + 2 + candidate <= data.Length) { index += 2; return candidate; }
        }
        index++; return first;
    }

    private static bool TryReadBerLength(ReadOnlySpan<byte> data, ref int index, out int length)
    {
        length = 0; if (index >= data.Length) return false;
        byte first = data[index++]; if (first < 0x80) { length = first; return true; }
        int count = first & 0x7F; if (count is 0 or > 3 || index + count > data.Length) return false;
        for (int i = 0; i < count; i++) length = (length << 8) | data[index++];
        return true;
    }

    private static Dictionary<byte, BmpDefinition> BuildDefinitions()
    {
        Dictionary<byte, BmpDefinition> d = new();
        void Fixed(int length, params byte[] tags) { foreach (byte tag in tags) d[tag] = new(BmpKind.Fixed, length); }
        void Kind(BmpKind kind, params byte[] tags) { foreach (byte tag in tags) d[tag] = new(kind); }
        Fixed(1, 0x01, 0x02, 0x03, 0x05, 0x17, 0x19, 0x27, 0x70, 0x75, 0x76, 0x8A, 0x8C, 0xA0, 0xD0, 0xD2, 0xD3, 0xE0, 0xE9, 0xEA, 0xF0, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD);
        Fixed(2, 0x0E, 0x3A, 0x49, 0x73, 0x74, 0x87); Fixed(3, 0x0B, 0x0C, 0x0D, 0x37, 0x88, 0xAA);
        Fixed(4, 0x29, 0x70, 0x71); Fixed(5, 0xBA); Fixed(6, 0x04); Fixed(8, 0x3B, 0xEB); Fixed(15, 0x2A);
        Kind(BmpKind.Llvar, 0x22, 0x23, 0x2D, 0x8B, 0xA7, 0xD1, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8);
        Kind(BmpKind.Lllvar, 0x24, 0x2E, 0x3C, 0x60, 0x9A, 0xAF); Kind(BmpKind.Tlv, 0x06, 0x25);
        return d;
    }
}
